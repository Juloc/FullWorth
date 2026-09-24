using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Accounts;

public sealed record AccountOrderItem(Guid AccountId, Guid? GroupId, int SortOrder);
public sealed record GroupOrderItem(Guid GroupId, int SortOrder);
public sealed record AccountOrderWrite(IReadOnlyList<GroupOrderItem>? Groups, IReadOnlyList<AccountOrderItem>? Accounts);
/// <summary>
/// Drei Werte, genau wie beim Konto. Die Hintergrundfarbe kam mit dem Umzug aus den
/// Benutzereinstellungen dazu (#177): dort hatte eine Gruppe sie seit jeher, hier fehlte sie - der
/// Umzug haette sie sonst stillschweigend verloren.
/// </summary>
public sealed record AccountGroupAppearanceWrite(string? Icon, string? Color, string? BackgroundColor);

public static class AccountExperienceEndpoints
{
    /// <summary>Mehr ordnet niemand von Hand; die Grenzen schuetzen die Transaktion.</summary>
    private const int MaxGroups = 200;
    private const int MaxAccounts = 1000;

    public static IEndpointRouteBuilder MapAccountExperienceEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/account-experience/reorder", ReorderAccounts).WithTags("Accounts");
        app.MapGet("/api/account-experience/group-appearances", GroupAppearances).WithTags("Accounts");
        app.MapPut("/api/account-experience/groups/{groupId:guid}/appearance", PutGroupAppearance).WithTags("Accounts");
        return app;
    }

    private static async Task<IResult> ReorderAccounts(
        Guid fullWorthSpaceId, AccountOrderWrite request, CurrentUserContext currentUser,
        SpaceAccess space, AccountGroupStore store, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await space.IsMemberAsync(userId, fullWorthSpaceId, ct)) return Results.NotFound();
        if (!await space.HasCapabilityAsync(userId, fullWorthSpaceId, "banking.manage", ct))
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        var groups = (request.Groups ?? []).DistinctBy(item => item.GroupId).ToArray();
        var accounts = (request.Accounts ?? []).DistinctBy(item => item.AccountId).ToArray();
        if (groups.Length > MaxGroups || accounts.Length > MaxAccounts)
            return Results.BadRequest(new { error = "Reorder request is too large." });

        var validGroupIds = await store.GroupIdsAsync(fullWorthSpaceId, ct);
        if (groups.Any(item => !validGroupIds.Contains(item.GroupId)) ||
            accounts.Any(item => item.GroupId.HasValue && !validGroupIds.Contains(item.GroupId.Value)))
            return Results.BadRequest(new { error = "Account group does not belong to this FullWorth Space." });

        var writable = await space.WritableAccountIdsAsync(userId, fullWorthSpaceId, ct);
        if (accounts.Any(item => !writable.Contains(item.AccountId))) return Results.StatusCode(403);

        await store.ReorderAsync(userId, fullWorthSpaceId, groups, accounts, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> GroupAppearances(
        Guid fullWorthSpaceId, CurrentUserContext currentUser, SpaceAccess space,
        AccountGroupStore store, CancellationToken ct)
    {
        if (!await space.IsMemberAsync(currentUser.RequireUserId(), fullWorthSpaceId, ct))
            return Results.NotFound();

        var rows = await store.AppearancesAsync(fullWorthSpaceId, ct);
        return Results.Ok(rows.Select(row => new
        {
            groupId = row.GroupId, icon = row.Icon, color = row.Color, backgroundColor = row.BackgroundColor
        }));
    }

    private static async Task<IResult> PutGroupAppearance(
        Guid groupId, Guid fullWorthSpaceId, AccountGroupAppearanceWrite request,
        CurrentUserContext currentUser, SpaceAccess space, AccountGroupStore store, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await space.IsMemberAsync(userId, fullWorthSpaceId, ct)) return Results.NotFound();
        if (!await space.HasCapabilityAsync(userId, fullWorthSpaceId, "banking.manage", ct))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (!await store.GroupExistsAsync(fullWorthSpaceId, groupId, ct)) return Results.NotFound();
        if (!ValidColor(request.Color) || !ValidColor(request.BackgroundColor))
            return Results.BadRequest(new { error = "Colors must be #RRGGBB or #RRGGBBAA." });

        await store.SetAppearanceAsync(userId, fullWorthSpaceId, groupId,
            string.IsNullOrWhiteSpace(request.Icon) ? null : request.Icon.Trim(),
            NormalizeColor(request.Color),
            NormalizeColor(request.BackgroundColor),
            ct);
        return Results.NoContent();
    }

    private static bool ValidColor(string? value) => string.IsNullOrWhiteSpace(value) ||
        (value.StartsWith('#') && (value.Length == 7 || value.Length == 9) && value.Skip(1).All(Uri.IsHexDigit));
    private static string? NormalizeColor(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();
}
