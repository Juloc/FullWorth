using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Audit;
using FullWorth.Backend.Security;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Parity;

public sealed record AccountOrderItem(Guid AccountId, Guid? GroupId, int SortOrder);
public sealed record GroupOrderItem(Guid GroupId, int SortOrder);
public sealed record AccountOrderWrite(IReadOnlyList<GroupOrderItem>? Groups, IReadOnlyList<AccountOrderItem>? Accounts);
public sealed record AccountGroupAppearanceWrite(string? Icon, string? Color);

public static class BankingExperienceParityEndpoints
{
    private static readonly object[] PlannedInstitutions =
    [
        new { institutionKey = "dkb", provider = "enable-banking", displayName = "DKB", country = "DE", iconAssetKey = "dkb", validated = false },
        new { institutionKey = "ing", provider = "enable-banking", displayName = "ING", country = "DE", iconAssetKey = "ing", validated = false },
        new { institutionKey = "paypal", provider = "enable-banking", displayName = "PayPal", country = "DE", iconAssetKey = "paypal", validated = false },
        new { institutionKey = "c24", provider = "enable-banking", displayName = "C24 Bank", country = "DE", iconAssetKey = "c24", validated = false },
        new { institutionKey = "revolut", provider = "enable-banking", displayName = "Revolut", country = "LT", iconAssetKey = "revolut", validated = false }
    ];

    public static IEndpointRouteBuilder MapBankingExperienceParityEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/bank-capabilities", GetCapabilities).WithTags("Banking");
        app.MapPost("/api/account-experience/reorder", ReorderAccounts).WithTags("Accounts");
        app.MapGet("/api/account-experience/group-appearances", GroupAppearances).WithTags("Accounts");
        app.MapPut("/api/account-experience/groups/{groupId:guid}/appearance", PutGroupAppearance).WithTags("Accounts");
        return app;
    }

    private static async Task<IResult> GetCapabilities(
        Guid fullWorthSpaceId, CurrentUserContext currentUser, FullWorthDbContext db, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await ParitySql.IsMemberAsync(db, userId, fullWorthSpaceId, ct)) return Results.NotFound();
        var connection = await ParitySql.OpenAsync(db, ct);
        await using var cmd = ParitySql.Command(connection, """
SELECT "InstitutionKey","Provider","DisplayName","Country","IconAssetKey","BalancesTested","TransactionsTested","PendingTested","MultiCurrencyTested","HistoryDepthDays","LastValidatedAt","LastValidatedVersion","KnownLimitations"
FROM "BankValidationRecords" ORDER BY "DisplayName"
""");
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<object>();
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new
            {
                institutionKey = ParitySql.String(reader, "InstitutionKey"),
                provider = ParitySql.String(reader, "Provider"),
                displayName = ParitySql.String(reader, "DisplayName"),
                country = ParitySql.String(reader, "Country"),
                iconAssetKey = ParitySql.NullableString(reader, "IconAssetKey"),
                balancesTested = ParitySql.Bool(reader, "BalancesTested"),
                transactionsTested = ParitySql.Bool(reader, "TransactionsTested"),
                pendingTested = ParitySql.Bool(reader, "PendingTested"),
                multiCurrencyTested = ParitySql.Bool(reader, "MultiCurrencyTested"),
                historyDepthDays = reader.IsDBNull(reader.GetOrdinal("HistoryDepthDays")) ? (int?)null : ParitySql.Int(reader, "HistoryDepthDays"),
                lastValidatedAt = ParitySql.NullableTimestamp(reader, "LastValidatedAt"),
                lastValidatedVersion = ParitySql.NullableString(reader, "LastValidatedVersion"),
                knownLimitations = ParitySql.NullableString(reader, "KnownLimitations"),
                validated = ParitySql.Bool(reader, "BalancesTested") && ParitySql.Bool(reader, "TransactionsTested")
            });
        }
        return Results.Ok(rows.Count == 0 ? PlannedInstitutions : rows);
    }

    private static async Task<IResult> ReorderAccounts(
        Guid fullWorthSpaceId, AccountOrderWrite request, CurrentUserContext currentUser,
        FullWorthDbContext db, AuditService audit, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await ParitySql.IsMemberAsync(db, userId, fullWorthSpaceId, ct)) return Results.NotFound();
        if (!await PermissionsErgonomicsParityEndpoints.HasCapabilityAsync(db, userId, fullWorthSpaceId, "banking.manage", ct))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        var groups = (request.Groups ?? []).DistinctBy(item => item.GroupId).ToArray();
        var accounts = (request.Accounts ?? []).DistinctBy(item => item.AccountId).ToArray();
        if (groups.Length > 200 || accounts.Length > 1000) return Results.BadRequest(new { error = "Reorder request is too large." });

        var validGroupIds = (await db.AccountGroups.AsNoTracking()
            .Where(group => group.FullWorthSpaceId == fullWorthSpaceId)
            .Select(group => group.Id).ToListAsync(ct)).ToHashSet();
        if (groups.Any(item => !validGroupIds.Contains(item.GroupId)) ||
            accounts.Any(item => item.GroupId.HasValue && !validGroupIds.Contains(item.GroupId.Value)))
            return Results.BadRequest(new { error = "Account group does not belong to this FullWorth Space." });

        var writable = await ParitySql.WritableAccountIdsAsync(db, userId, fullWorthSpaceId, ct);
        if (accounts.Any(item => !writable.Contains(item.AccountId))) return Results.StatusCode(403);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        foreach (var item in groups)
        {
            var group = await db.AccountGroups.SingleAsync(group => group.Id == item.GroupId && group.FullWorthSpaceId == fullWorthSpaceId, ct);
            group.SortOrder = item.SortOrder;
        }
        foreach (var item in accounts)
        {
            var account = await db.Accounts.SingleAsync(account => account.Id == item.AccountId && account.FullWorthSpaceId == fullWorthSpaceId, ct);
            account.GroupId = item.GroupId;
            account.SortOrder = item.SortOrder;
            account.UpdatedAt = DateTimeOffset.UtcNow;
        }
        audit.Record(fullWorthSpaceId, userId, "account_groups.reordered", "FullWorthSpace", fullWorthSpaceId);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> GroupAppearances(
        Guid fullWorthSpaceId, CurrentUserContext currentUser, FullWorthDbContext db, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await ParitySql.IsMemberAsync(db, userId, fullWorthSpaceId, ct)) return Results.NotFound();
        var connection = await ParitySql.OpenAsync(db, ct);
        await using var cmd = ParitySql.Command(connection, """
SELECT a."GroupId",a."Icon",a."Color" FROM "AccountGroupAppearances" a
JOIN "AccountGroups" g ON g."Id"=a."GroupId" WHERE g."FullWorthSpaceId"=@space
""", ("@space", fullWorthSpaceId));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<object>();
        while (await reader.ReadAsync(ct)) rows.Add(new
        {
            groupId = ParitySql.Guid(reader, "GroupId"),
            icon = ParitySql.NullableString(reader, "Icon"),
            color = ParitySql.NullableString(reader, "Color")
        });
        return Results.Ok(rows);
    }

    private static async Task<IResult> PutGroupAppearance(
        Guid groupId, Guid fullWorthSpaceId, AccountGroupAppearanceWrite request,
        CurrentUserContext currentUser, FullWorthDbContext db, AuditService audit, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await ParitySql.IsMemberAsync(db, userId, fullWorthSpaceId, ct)) return Results.NotFound();
        if (!await PermissionsErgonomicsParityEndpoints.HasCapabilityAsync(db, userId, fullWorthSpaceId, "banking.manage", ct))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (!await db.AccountGroups.AsNoTracking().AnyAsync(group => group.Id == groupId && group.FullWorthSpaceId == fullWorthSpaceId, ct))
            return Results.NotFound();
        if (!ValidColor(request.Color)) return Results.BadRequest(new { error = "Color must be #RRGGBB or #RRGGBBAA." });

        var connection = await ParitySql.OpenAsync(db, ct);
        await using var cmd = ParitySql.Command(connection, """
INSERT INTO "AccountGroupAppearances" ("GroupId","Icon","Color","UpdatedAt") VALUES (@id,@icon,@color,@now)
ON CONFLICT ("GroupId") DO UPDATE SET "Icon"=EXCLUDED."Icon","Color"=EXCLUDED."Color","UpdatedAt"=EXCLUDED."UpdatedAt"
""", ("@id", groupId), ("@icon", string.IsNullOrWhiteSpace(request.Icon) ? null : request.Icon.Trim()),
            ("@color", string.IsNullOrWhiteSpace(request.Color) ? null : request.Color.Trim().ToUpperInvariant()),
            ("@now", DateTimeOffset.UtcNow));
        await cmd.ExecuteNonQueryAsync(ct);
        audit.Record(fullWorthSpaceId, userId, "account_group.appearance.updated", "AccountGroup", groupId);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static bool ValidColor(string? value) => string.IsNullOrWhiteSpace(value) ||
        (value.StartsWith('#') && (value.Length == 7 || value.Length == 9) && value.Skip(1).All(Uri.IsHexDigit));
}
