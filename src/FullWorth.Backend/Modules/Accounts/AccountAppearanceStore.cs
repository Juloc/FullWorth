using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Audit;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Accounts;

/// <summary>Wie ein Konto aussieht - Symbol und zwei Farben, alles optional.</summary>
public sealed record AccountAppearance(string? Icon, string? IconColor, string? BackgroundColor);

/// <summary>
/// Aussehen und Gesehen-Stand der Konten.
///
/// Diese Abfragen standen bis 2026-09-15 im Handler, und zwar in einer Schleife ueber die Konten:
/// pro Konto ein SELECT auf AccountAppearances, eines auf AccountTransactionSeenStates und ein
/// COUNT auf Transactions. Bei zwanzig Konten waren das sechzig Runden zur Datenbank fuer eine
/// Liste. Hier stehen sie als drei Abfragen fuer alle Konten zusammen - derselbe Inhalt, und man
/// sieht endlich, dass es drei sind.
/// </summary>
public sealed class AccountAppearanceStore(FullWorthDbContext db, AuditService audit)
{
    public Task<List<FinanceAccount>> ListAccountsAsync(IReadOnlySet<Guid> accountIds, CancellationToken ct) =>
        db.Accounts.AsNoTracking()
            .Where(account => accountIds.Contains(account.Id))
            .OrderBy(account => account.SortOrder)
            .ThenBy(account => account.DisplayName)
            .ToListAsync(ct);

    public async Task<Dictionary<Guid, AccountAppearance>> AppearancesAsync(
        IReadOnlyCollection<Guid> accountIds, CancellationToken ct)
    {
        var result = new Dictionary<Guid, AccountAppearance>();
        if (accountIds.Count == 0) return result;

        var connection = await RawSql.OpenAsync(db, ct);
        await using var cmd = RawSql.Command(connection,
            "SELECT \"AccountId\",\"Icon\",\"IconColor\",\"BackgroundColor\" FROM \"AccountAppearances\" WHERE \"AccountId\"=ANY(@ids)",
            ("@ids", accountIds.ToArray()));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result[RawSql.Guid(reader, "AccountId")] = new AccountAppearance(
                RawSql.NullableString(reader, "Icon"),
                RawSql.NullableString(reader, "IconColor"),
                RawSql.NullableString(reader, "BackgroundColor"));
        return result;
    }

    /// <summary>Wann dieser Benutzer die Buchungen der Konten zuletzt gesehen hat.</summary>
    public async Task<Dictionary<Guid, DateTimeOffset>> LastSeenAsync(
        Guid userId, IReadOnlyCollection<Guid> accountIds, CancellationToken ct)
    {
        var result = new Dictionary<Guid, DateTimeOffset>();
        if (accountIds.Count == 0) return result;

        var connection = await RawSql.OpenAsync(db, ct);
        await using var cmd = RawSql.Command(connection,
            "SELECT \"AccountId\",\"LastSeenAt\" FROM \"AccountTransactionSeenStates\" WHERE \"UserId\"=@user AND \"AccountId\"=ANY(@ids)",
            ("@user", userId), ("@ids", accountIds.ToArray()));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result[RawSql.Guid(reader, "AccountId")] = RawSql.Timestamp(reader, "LastSeenAt");
        return result;
    }

    /// <summary>
    /// Wie viele Buchungen je Konto seit dem Gesehen-Zeitpunkt dazugekommen sind. Konten ohne
    /// Eintrag in <paramref name="lastSeen"/> zaehlen alles.
    /// </summary>
    public async Task<Dictionary<Guid, int>> UnseenCountsAsync(
        IReadOnlyCollection<Guid> accountIds, IReadOnlyDictionary<Guid, DateTimeOffset> lastSeen, CancellationToken ct)
    {
        var result = new Dictionary<Guid, int>();
        if (accountIds.Count == 0) return result;

        // Je Konto ein eigener Stichtag, und fuer Konten ohne Eintrag gar keiner. Das als zwei
        // entpackte Arrays an die Abfrage zu geben ist der Grund, warum hier eine Runde reicht statt
        // einer je Konto.
        var ids = accountIds.ToArray();
        var seen = ids.Select(id => lastSeen.TryGetValue(id, out var value) ? value : (DateTimeOffset?)null).ToArray();

        var connection = await RawSql.OpenAsync(db, ct);
        await using var cmd = RawSql.Command(connection, """
SELECT t."AccountId", COUNT(*) AS "Unseen"
FROM "Transactions" t
JOIN unnest(@ids, @seen) AS s(id, seen) ON s.id = t."AccountId"
WHERE s.seen IS NULL OR t."FirstSeenAt" > s.seen
GROUP BY t."AccountId"
""", ("@ids", ids), ("@seen", seen));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result[RawSql.Guid(reader, "AccountId")] = (int)reader.GetInt64(reader.GetOrdinal("Unseen"));
        return result;
    }

    public async Task SetAppearanceAsync(
        Guid userId, Guid fullWorthSpaceId, Guid accountId, AccountAppearance appearance, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        await using var cmd = RawSql.Command(connection, """
INSERT INTO "AccountAppearances" ("AccountId","Icon","IconColor","BackgroundColor","UpdatedAt")
VALUES (@id,@icon,@iconColor,@background,@now)
ON CONFLICT ("AccountId") DO UPDATE SET
  "Icon"=EXCLUDED."Icon",
  "IconColor"=EXCLUDED."IconColor",
  "BackgroundColor"=EXCLUDED."BackgroundColor",
  "UpdatedAt"=EXCLUDED."UpdatedAt"
""",
            ("@id", accountId),
            ("@icon", appearance.Icon),
            ("@iconColor", appearance.IconColor),
            ("@background", appearance.BackgroundColor),
            ("@now", DateTimeOffset.UtcNow));
        await cmd.ExecuteNonQueryAsync(ct);

        audit.Record(fullWorthSpaceId, userId, "account.appearance.updated", "FinanceAccount", accountId);
        await db.SaveChangesAsync(ct);
    }

    public async Task MarkSeenAsync(Guid userId, Guid accountId, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        await using var cmd = RawSql.Command(connection, """
INSERT INTO "AccountTransactionSeenStates" ("UserId","AccountId","LastSeenAt")
VALUES (@user,@account,@now)
ON CONFLICT ("UserId","AccountId") DO UPDATE SET "LastSeenAt"=EXCLUDED."LastSeenAt"
""", ("@user", userId), ("@account", accountId), ("@now", DateTimeOffset.UtcNow));
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
