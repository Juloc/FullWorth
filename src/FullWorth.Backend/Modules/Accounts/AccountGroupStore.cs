using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Audit;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Accounts;

/// <summary>Wie eine Kontogruppe aussieht - Symbol und Farbe, beides optional.</summary>
public sealed record AccountGroupAppearance(Guid GroupId, string? Icon, string? Color, string? BackgroundColor);

/// <summary>
/// Die Kontogruppen eines Space: ihre Reihenfolge, die Zuordnung der Konten und ihr Aussehen.
///
/// Die Reihenfolge wird in einer Transaktion gesetzt, weil eine halb umsortierte Liste schlechter
/// ist als die alte: der Benutzer sieht dann eine Anordnung, die er nie gewaehlt hat.
/// </summary>
public sealed class AccountGroupStore(FullWorthDbContext db, AuditService audit)
{
    public async Task<HashSet<Guid>> GroupIdsAsync(Guid fullWorthSpaceId, CancellationToken ct) =>
        (await db.AccountGroups.AsNoTracking()
            .Where(group => group.FullWorthSpaceId == fullWorthSpaceId)
            .Select(group => group.Id)
            .ToListAsync(ct)).ToHashSet();

    public Task<bool> GroupExistsAsync(Guid fullWorthSpaceId, Guid groupId, CancellationToken ct) =>
        db.AccountGroups.AsNoTracking()
            .AnyAsync(group => group.Id == groupId && group.FullWorthSpaceId == fullWorthSpaceId, ct);

    public async Task ReorderAsync(
        Guid userId, Guid fullWorthSpaceId,
        IReadOnlyList<GroupOrderItem> groups, IReadOnlyList<AccountOrderItem> accounts, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        foreach (var item in groups)
        {
            var group = await db.AccountGroups.SingleAsync(
                group => group.Id == item.GroupId && group.FullWorthSpaceId == fullWorthSpaceId, ct);
            group.SortOrder = item.SortOrder;
        }
        foreach (var item in accounts)
        {
            var account = await db.Accounts.SingleAsync(
                account => account.Id == item.AccountId && account.FullWorthSpaceId == fullWorthSpaceId, ct);
            account.GroupId = item.GroupId;
            account.SortOrder = item.SortOrder;
            account.UpdatedAt = DateTimeOffset.UtcNow;
        }

        audit.Record(fullWorthSpaceId, userId, "account_groups.reordered", "FullWorthSpace", fullWorthSpaceId);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<AccountGroupAppearance>> AppearancesAsync(
        Guid fullWorthSpaceId, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        await using var cmd = RawSql.Command(connection, """
SELECT a."GroupId",a."Icon",a."Color",a."BackgroundColor" FROM "AccountGroupAppearances" a
JOIN "AccountGroups" g ON g."Id"=a."GroupId" WHERE g."FullWorthSpaceId"=@space
""", ("@space", fullWorthSpaceId));
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var rows = new List<AccountGroupAppearance>();
        while (await reader.ReadAsync(ct))
            rows.Add(new AccountGroupAppearance(
                RawSql.Guid(reader, "GroupId"),
                RawSql.NullableString(reader, "Icon"),
                RawSql.NullableString(reader, "Color"),
                RawSql.NullableString(reader, "BackgroundColor")));
        return rows;
    }

    public async Task SetAppearanceAsync(
        Guid userId, Guid fullWorthSpaceId, Guid groupId, string? icon, string? color, string? background,
        CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        await using var cmd = RawSql.Command(connection, """
INSERT INTO "AccountGroupAppearances" ("GroupId","Icon","Color","BackgroundColor","UpdatedAt")
VALUES (@id,@icon,@color,@background,@now)
ON CONFLICT ("GroupId") DO UPDATE SET "Icon"=EXCLUDED."Icon","Color"=EXCLUDED."Color",
  "BackgroundColor"=EXCLUDED."BackgroundColor","UpdatedAt"=EXCLUDED."UpdatedAt"
""", ("@id", groupId), ("@icon", icon), ("@color", color), ("@background", background),
   ("@now", DateTimeOffset.UtcNow));
        await cmd.ExecuteNonQueryAsync(ct);

        audit.Record(fullWorthSpaceId, userId, "account_group.appearance.updated", "AccountGroup", groupId);
        await db.SaveChangesAsync(ct);
    }
}
