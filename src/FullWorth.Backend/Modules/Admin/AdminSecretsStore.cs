using System.Text.Json;
using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.BankConnections;
using FullWorth.Backend.Modules.Intelligence;
using FullWorth.Backend.Security;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Admin;

/// <summary>One credential the caller stored. The value is never part of the listing.</summary>
public sealed record AdminSecretEntry(string Reference, string Group, string Label, string Description);

/// <summary>
/// Die Zugangsdaten, die eine Person selbst eingetragen hat, verschluesselt mit FieldCipher in
/// dieser Datenbank.
///
/// <b>Die Regel ueber allem: der Benutzer, den das Ticket nennt, sieht ausschliesslich seine
/// eigenen Zeilen.</b> Ein Administrator hat keinen Anspruch auf die Bank-PIN eines anderen. Jede
/// Abfrage hier filtert auf den eingetragenen Eigentuemer - darum werden Space-bezogene Tabellen
/// auf den Benutzer gefiltert, der die Verbindung eingerichtet hat, und nicht auf den Space.
///
/// Diese Regel stand bis 2026-09-15 als Kommentar an der Endpunktklasse, also drei Torwaechter
/// entfernt von den Abfragen, die sie einhalten. Hier steht sie dort, wo sie gilt.
/// </summary>
public sealed class AdminSecretsStore(FullWorthDbContext db, IntelligenceDbContext intelligence)
{
    private const string BankGroup = "Bankzugänge";
    private const string AiGroup = "KI-Zugänge";
    private const string ImportGroup = "Importe";

    public async Task<List<AdminSecretEntry>> ListAsync(
        Guid userId, CancellationToken ct)
    {
        var entries = new List<AdminSecretEntry>();

        // FinTS: the credentials travel as one encrypted JSON blob on the connection, so the entry is
        // per connection rather than per field. Matched on who authorised it - a bank connection is
        // space-scoped, and the other members of a space did not type this PIN.
        var fints = await db.BankConnections.AsNoTracking()
            .Where(x => x.Provider == "fints" && x.AuthorizationUserId == userId && x.AuthorizationId != null)
            .Select(x => new { x.Id, x.InstitutionName })
            .ToListAsync(ct);
        entries.AddRange(fints.Select(x => new AdminSecretEntry(
            $"bank.fints.{x.Id:N}", BankGroup, $"FinTS-Zugang · {x.InstitutionName}",
            "Anmeldename und PIN, mit denen sich diese Installation bei der Bank meldet.")));

        var profiles = await db.EnableBankingProfiles.AsNoTracking()
            .Where(x => x.UserId == userId)
            .Select(x => new { x.Id, x.ApplicationName, HasRefresh = x.ControlPanelRefreshToken != null })
            .ToListAsync(ct);
        foreach (var profile in profiles)
        {
            entries.Add(new AdminSecretEntry(
                $"bank.eb-key.{profile.Id:N}", BankGroup, $"Enable Banking Privatschlüssel · {profile.ApplicationName}",
                "Der RSA-Schlüssel, mit dem deine Bankabfragen signiert werden."));
            if (profile.HasRefresh)
                entries.Add(new AdminSecretEntry(
                    $"bank.eb-refresh.{profile.Id:N}", BankGroup, $"Enable Banking Refresh-Token · {profile.ApplicationName}",
                    "Erneuert den Zugang zum Enable-Banking-Control-Panel."));
        }

        var credentials = await intelligence.AiCredentials.AsNoTracking()
            .Where(x => x.OwnerUserId == userId)
            .Select(x => new { x.Id, x.Name, x.Provider })
            .ToListAsync(ct);
        entries.AddRange(credentials.Select(x => new AdminSecretEntry(
            $"ai.credential.{x.Id:N}", AiGroup, $"{x.Name} · {x.Provider}",
            "Dein API-Schlüssel bei diesem Anbieter.")));

        if (await PaperlessTokenRowAsync(userId, ct) is not null)
            entries.Add(new AdminSecretEntry(
                "import.paperless-token", ImportGroup, "Paperless API-Token",
                "Liest Belege aus deiner Paperless-Instanz."));

        if (await AmazonStateRowAsync(userId, ct) is not null)
            entries.Add(new AdminSecretEntry(
                "import.amazon-session", ImportGroup, "Amazon-Sitzung",
                "Die gespeicherte Browser-Sitzung. Enthält aktive Cookies — wer sie hat, ist bei Amazon angemeldet."));

        return entries;
    }

    public async Task<string?> RevealAsync(
        FieldCipher cipher,
        Guid userId, string? reference, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reference)) return null;
        var parts = reference.Split('.');

        // Every branch filters on userId as well as on the id in the reference. A reference is a
        // client-supplied string; without the owner filter it would be a way to name somebody else's
        // row and have the server decrypt it.
        switch (parts)
        {
            case ["bank", "fints", var id] when Guid.TryParseExact(id, "N", out var connectionId):
            {
                var blob = await db.BankConnections.AsNoTracking()
                    .Where(x => x.Id == connectionId && x.AuthorizationUserId == userId && x.Provider == "fints")
                    .Select(x => x.AuthorizationId)
                    .SingleOrDefaultAsync(ct);
                return FinTsCredentialsOf(cipher.Unprotect(blob));
            }

            case ["bank", "eb-key", var id] when Guid.TryParseExact(id, "N", out var profileId):
            {
                var value = await db.EnableBankingProfiles.AsNoTracking()
                    .Where(x => x.Id == profileId && x.UserId == userId)
                    .Select(x => x.PrivateKeyPem)
                    .SingleOrDefaultAsync(ct);
                return cipher.Unprotect(value);
            }

            case ["bank", "eb-refresh", var id] when Guid.TryParseExact(id, "N", out var profileId):
            {
                var value = await db.EnableBankingProfiles.AsNoTracking()
                    .Where(x => x.Id == profileId && x.UserId == userId)
                    .Select(x => x.ControlPanelRefreshToken)
                    .SingleOrDefaultAsync(ct);
                return cipher.Unprotect(value);
            }

            case ["ai", "credential", var id] when Guid.TryParseExact(id, "N", out var credentialId):
            {
                var value = await intelligence.AiCredentials.AsNoTracking()
                    .Where(x => x.Id == credentialId && x.OwnerUserId == userId)
                    .Select(x => x.ProtectedSecret)
                    .SingleOrDefaultAsync(ct);
                return cipher.Unprotect(value);
            }

            case ["import", "paperless-token"]:
                return cipher.Unprotect(await PaperlessTokenRowAsync(userId, ct));

            case ["import", "amazon-session"]:
                return cipher.Unprotect(await AmazonStateRowAsync(userId, ct));

            default:
                return null;
        }
    }

    /// <summary>
    /// The stored FinTS blob is the whole connection secret — credentials, bank parameters and a live
    /// session. Only the two fields a person would recognise come back: handing over the session state
    /// would be handing over a logged-in dialog, and it is not what "show me my credentials" means.
    /// </summary>
    private static string? FinTsCredentialsOf(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var user = Property(root, "UserId");
            var pin = Property(root, "Pin");
            return user is null && pin is null ? null : $"Anmeldename: {user}\nPIN: {pin}";
        }
        catch (JsonException)
        {
            // A blob this process cannot parse is not a blob it should guess at.
            return null;
        }

        static string? Property(JsonElement root, string name) =>
            root.TryGetProperty(name, out var value) ? value.GetString()
            : root.TryGetProperty(char.ToLowerInvariant(name[0]) + name[1..], out var camel) ? camel.GetString()
            : null;
    }

    private async Task<string?> PaperlessTokenRowAsync(
        Guid userId, CancellationToken ct)
    {
        var rows = await db.Database.SqlQuery<string>($"""
            SELECT "ApiTokenProtected" AS "Value"
            FROM "PaperlessReceiptConnections"
            WHERE "UserId" = {userId}
            LIMIT 1
            """).ToListAsync(ct);
        return rows.Count == 0 ? null : rows[0];
    }

    private async Task<string?> AmazonStateRowAsync(
        Guid userId, CancellationToken ct)
    {
        var rows = await db.Database.SqlQuery<string>($"""
            SELECT "EncryptedStorageState" AS "Value"
            FROM "AmazonConnections"
            WHERE "UserId" = {userId}
            LIMIT 1
            """).ToListAsync(ct);
        return rows.Count == 0 ? null : rows[0];
    }
}
