using System.Text.Json;
using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.BankConnections;
using FullWorth.Backend.Modules.Intelligence;
using FullWorth.Backend.Security;
using FullWorth.Shared;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Admin;

/// <summary>One credential the caller stored. The value is never part of the listing.</summary>
public sealed record AdminSecretEntry(string Reference, string Group, string Label, string Description);

/// <summary>
/// The finance side of the admin vault: the credentials a person entered themselves and which are
/// encrypted with <see cref="FieldCipher"/> in this database.
///
/// Three gates, and none of them alone is enough:
///
/// <list type="number">
/// <item>The ingest key, which the <c>/internal</c> middleware already demands.</item>
/// <item>A signed <see cref="AdminVaultTicket"/>, minted by the BFF only after the step-up. It is
/// signed with a key DERIVED from the internal key, not the internal key itself — otherwise the
/// internal key would silently become a read-anything key.</item>
/// <item>In the unified host, a loopback check. These routes exist for one caller in the same
/// container; nothing should be able to reach them across a Docker network even holding both keys.</item>
/// </list>
///
/// And one rule above the gates: <b>the ticket names a finance user, and only that user's own rows
/// are ever read.</b> An administrator is not entitled to somebody else's bank PIN. Every query here
/// filters on the recorded owner, which is why space-scoped tables are matched on the user who set
/// the connection up rather than on the space.
/// </summary>
public static class AdminSecretsEndpoints
{
    private const string BankGroup = "Bankzugänge";
    private const string AiGroup = "KI-Zugänge";
    private const string ImportGroup = "Importe";

    public static IEndpointRouteBuilder MapAdminSecretsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/internal/admin/secrets").WithTags("Internal admin");

        group.MapPost("/inventory", async (
            HttpContext context,
            FullWorthDbContext db,
            IntelligenceDbContext intelligence,
            IConfiguration configuration,
            CancellationToken ct) =>
        {
            if (!Authorised(context, configuration, out var userId)) return Results.Unauthorized();
            NoStore(context);
            return Results.Ok(await ListAsync(db, intelligence, userId, ct));
        });

        group.MapPost("/reveal", async (
            AdminSecretRevealRequest request,
            HttpContext context,
            FullWorthDbContext db,
            IntelligenceDbContext intelligence,
            FieldCipher cipher,
            IConfiguration configuration,
            CancellationToken ct) =>
        {
            if (!Authorised(context, configuration, out var userId)) return Results.Unauthorized();
            NoStore(context);

            var value = await RevealAsync(db, intelligence, cipher, userId, request.Reference, ct);
            return value is null
                ? Results.NotFound(new { error = "not_set" })
                : Results.Ok(new { value });
        });

        return app;
    }

    public sealed record AdminSecretRevealRequest(string? Reference);

    // No TimeProvider parameter: this project does not register one, and the minimal-API binder would
    // fail to infer it at route-build time rather than at call time - the whole backend stops routing.
    private static bool Authorised(
        HttpContext context, IConfiguration configuration, out Guid financeUserId)
    {
        financeUserId = Guid.Empty;

        // Gate 3. In the unified host the BFF and the backend share a process and talk over loopback,
        // so anything arriving from elsewhere is by definition not the caller these routes exist for.
        //
        // The key is read here rather than through Shared/UnifiedHost.cs, which this project does not
        // link. One key, read the same way the image sets it.
        if (bool.TryParse(configuration["FullWorthHost:Unified"], out var unified) && unified)
        {
            var remote = context.Connection.RemoteIpAddress;
            if (remote is null || !System.Net.IPAddress.IsLoopback(remote)) return false;
        }

        // Gate 2. The ingest key (gate 1) was already checked by the /internal middleware.
        return AdminVaultTicket.TryValidate(
            configuration["Security:InternalKey"],
            context.Request.Headers[AdminVaultTicket.HeaderName],
            DateTimeOffset.UtcNow,
            out financeUserId);
    }

    private static void NoStore(HttpContext context) =>
        context.Response.Headers.CacheControl = "no-store, max-age=0";

    private static async Task<List<AdminSecretEntry>> ListAsync(
        FullWorthDbContext db, IntelligenceDbContext intelligence, Guid userId, CancellationToken ct)
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

        if (await PaperlessTokenRowAsync(db, userId, ct) is not null)
            entries.Add(new AdminSecretEntry(
                "import.paperless-token", ImportGroup, "Paperless API-Token",
                "Liest Belege aus deiner Paperless-Instanz."));

        if (await AmazonStateRowAsync(db, userId, ct) is not null)
            entries.Add(new AdminSecretEntry(
                "import.amazon-session", ImportGroup, "Amazon-Sitzung",
                "Die gespeicherte Browser-Sitzung. Enthält aktive Cookies — wer sie hat, ist bei Amazon angemeldet."));

        return entries;
    }

    private static async Task<string?> RevealAsync(
        FullWorthDbContext db, IntelligenceDbContext intelligence, FieldCipher cipher,
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
                return cipher.Unprotect(await PaperlessTokenRowAsync(db, userId, ct));

            case ["import", "amazon-session"]:
                return cipher.Unprotect(await AmazonStateRowAsync(db, userId, ct));

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

    private static async Task<string?> PaperlessTokenRowAsync(
        FullWorthDbContext db, Guid userId, CancellationToken ct)
    {
        var rows = await db.Database.SqlQuery<string>($"""
            SELECT "ApiTokenProtected" AS "Value"
            FROM "PaperlessReceiptConnections"
            WHERE "UserId" = {userId}
            LIMIT 1
            """).ToListAsync(ct);
        return rows.Count == 0 ? null : rows[0];
    }

    private static async Task<string?> AmazonStateRowAsync(
        FullWorthDbContext db, Guid userId, CancellationToken ct)
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
