using FullWorth.Backend.Data;

namespace FullWorth.Backend.Modules.Import;

/// <summary>
/// Merkt sich, welches Konto eine Kontobezeichnung aus einer Importdatei meint (#131, Abschnitt 3).
///
/// Gemerkt wird erst beim Festschreiben, nicht beim Zuordnen: bis dahin ist die Auswahl ein Entwurf,
/// und ein abgebrochener Import soll die naechste Vorauswahl nicht praegen.
/// </summary>
public sealed class ImportSourceAccountStore(FullWorthDbContext db)
{
    /// <summary>
    /// Die Bezeichnung, unter der eine Zuordnung wiedergefunden wird. Getrimmt und kleingeschrieben,
    /// damit "C24 Girokonto" und "c24 girokonto" nicht zwei Eintraege sind, von denen der Nutzer
    /// einen nie gemacht hat.
    /// </summary>
    public static string? Normalize(string? source)
    {
        var trimmed = source?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed.ToLowerInvariant();
    }

    /// <summary>Was dieser Raum sich bisher gemerkt hat - Bezeichnung auf Kontokennung.</summary>
    public async Task<Dictionary<string, Guid>> ForSpaceAsync(Guid fullWorthSpaceId, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        await using var command = RawSql.Command(connection,
            "SELECT \"SourceKey\",\"AccountId\" FROM \"ImportSourceAccountLinks\" WHERE \"FullWorthSpaceId\"=@space",
            ("@space", fullWorthSpaceId));
        await using var reader = await command.ExecuteReaderAsync(ct);

        var result = new Dictionary<string, Guid>(StringComparer.Ordinal);
        while (await reader.ReadAsync(ct)) result[reader.GetString(0)] = reader.GetGuid(1);
        return result;
    }

    /// <summary>
    /// Haelt fest, was der Nutzer gerade zugeordnet hat. Eine Bezeichnung ohne Zielkonto wird
    /// vergessen statt auf <c>null</c> gemerkt: "keins" ist keine Zuordnung, und beim naechsten Mal
    /// soll wieder der Vorschlag stehen und nicht die letzte Nicht-Entscheidung.
    /// </summary>
    public async Task RememberAsync(
        Guid fullWorthSpaceId, IReadOnlyDictionary<string, Guid?> mappings, CancellationToken ct)
    {
        if (mappings.Count == 0) return;
        var connection = await RawSql.OpenAsync(db, ct);
        var now = DateTimeOffset.UtcNow;

        foreach (var (source, accountId) in mappings)
        {
            if (Normalize(source) is not { } key) continue;
            if (accountId is not { } target)
            {
                await using var forget = RawSql.Command(connection,
                    "DELETE FROM \"ImportSourceAccountLinks\" WHERE \"FullWorthSpaceId\"=@space AND \"SourceKey\"=@key",
                    ("@space", fullWorthSpaceId), ("@key", key));
                await forget.ExecuteNonQueryAsync(ct);
                continue;
            }

            await using var command = RawSql.Command(connection, """
INSERT INTO "ImportSourceAccountLinks" ("FullWorthSpaceId","SourceKey","AccountId","UpdatedAt")
VALUES (@space,@key,@account,@now)
ON CONFLICT ("FullWorthSpaceId","SourceKey")
DO UPDATE SET "AccountId"=EXCLUDED."AccountId","UpdatedAt"=EXCLUDED."UpdatedAt"
""",
                ("@space", fullWorthSpaceId), ("@key", key), ("@account", target), ("@now", now));
            await command.ExecuteNonQueryAsync(ct);
        }
    }
}
