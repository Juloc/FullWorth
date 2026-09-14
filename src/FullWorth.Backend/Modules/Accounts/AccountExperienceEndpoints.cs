using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Audit;
using FullWorth.Backend.Security;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Accounts;

public sealed record AccountOrderItem(Guid AccountId, Guid? GroupId, int SortOrder);
public sealed record GroupOrderItem(Guid GroupId, int SortOrder);
public sealed record AccountOrderWrite(IReadOnlyList<GroupOrderItem>? Groups, IReadOnlyList<AccountOrderItem>? Accounts);
public sealed record AccountGroupAppearanceWrite(string? Icon, string? Color);

public static class AccountExperienceEndpoints
{

    public static IEndpointRouteBuilder MapAccountExperienceEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/account-experience/reorder", ReorderAccounts).WithTags("Accounts");
        app.MapGet("/api/account-experience/group-appearances", GroupAppearances).WithTags("Accounts");
        app.MapPut("/api/account-experience/groups/{groupId:guid}/appearance", PutGroupAppearance).WithTags("Accounts");
        return app;
    }

    private static async Task<IResult> ReorderAccounts(
        Guid fullWorthSpaceId, AccountOrderWrite request, CurrentUserContext currentUser,
        FullWorthDbContext db, AuditService audit, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await RawSql.IsMemberAsync(db, userId, fullWorthSpaceId, ct)) return Results.NotFound();
        if (!await SpaceCapabilities.HasCapabilityAsync(db, userId, fullWorthSpaceId, "banking.manage", ct))
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

        var writable = await RawSql.WritableAccountIdsAsync(db, userId, fullWorthSpaceId, ct);
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
        if (!await RawSql.IsMemberAsync(db, userId, fullWorthSpaceId, ct)) return Results.NotFound();
        var connection = await RawSql.OpenAsync(db, ct);
        await using var cmd = RawSql.Command(connection, """
SELECT a."GroupId",a."Icon",a."Color" FROM "AccountGroupAppearances" a
JOIN "AccountGroups" g ON g."Id"=a."GroupId" WHERE g."FullWorthSpaceId"=@space
""", ("@space", fullWorthSpaceId));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<object>();
        while (await reader.ReadAsync(ct)) rows.Add(new
        {
            groupId = RawSql.Guid(reader, "GroupId"),
            icon = RawSql.NullableString(reader, "Icon"),
            color = RawSql.NullableString(reader, "Color")
        });
        return Results.Ok(rows);
    }

    private static async Task<IResult> PutGroupAppearance(
        Guid groupId, Guid fullWorthSpaceId, AccountGroupAppearanceWrite request,
        CurrentUserContext currentUser, FullWorthDbContext db, AuditService audit, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await RawSql.IsMemberAsync(db, userId, fullWorthSpaceId, ct)) return Results.NotFound();
        if (!await SpaceCapabilities.HasCapabilityAsync(db, userId, fullWorthSpaceId, "banking.manage", ct))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (!await db.AccountGroups.AsNoTracking().AnyAsync(group => group.Id == groupId && group.FullWorthSpaceId == fullWorthSpaceId, ct))
            return Results.NotFound();
        if (!ValidColor(request.Color)) return Results.BadRequest(new { error = "Color must be #RRGGBB or #RRGGBBAA." });

        var connection = await RawSql.OpenAsync(db, ct);
        await using var cmd = RawSql.Command(connection, """
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
