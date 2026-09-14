using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.FullWorthSpaces;

public sealed record CapabilityGrantWrite(Guid UserId, string Capability, bool IsAllowed);

/// <summary>Einzelfreigaben je Mitglied. Kam aus Parity/ExperienceParityModule.</summary>
public static class CapabilityGrantEndpoints
{
    private static readonly HashSet<string> Capabilities = new(StringComparer.OrdinalIgnoreCase)
    {
        "view", "categorize", "edit_transactions", "edit_budgets", "edit_contracts",
        "manage_banking", "manage_imports", "manage_investments", "admin"
    };

    public static IEndpointRouteBuilder MapCapabilityGrantEndpoints(this IEndpointRouteBuilder app)
    {
        var grants = app.MapGroup("/api/capability-grants").WithTags("Sharing");
        grants.MapGet("/", ListGrants);
        grants.MapPut("/", PutGrant);
        return app;
    }

    private static async Task<IResult> ListGrants(
        Guid fullWorthSpaceId, CurrentUserContext currentUser, SpaceAccess space,
        CapabilityGrantStore store, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await space.IsOwnerAsync(userId, fullWorthSpaceId, ct)) return Results.StatusCode(403);

        var rows = await store.ListAsync(fullWorthSpaceId, ct);
        return Results.Ok(rows.Select(row => new
        {
            userId = row.UserId,
            capability = row.Capability,
            isAllowed = row.IsAllowed,
            updatedAt = row.UpdatedAt
        }));
    }

    private static async Task<IResult> PutGrant(
        Guid fullWorthSpaceId, CapabilityGrantWrite request, CurrentUserContext currentUser,
        SpaceAccess space, CapabilityGrantStore store, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await space.IsOwnerAsync(userId, fullWorthSpaceId, ct)) return Results.StatusCode(403);

        var capability = request.Capability.Trim().ToLowerInvariant();
        if (!Capabilities.Contains(capability)) return Results.BadRequest();
        if (!await store.IsMemberAsync(fullWorthSpaceId, request.UserId, ct)) return Results.BadRequest();

        await store.SetAsync(userId, fullWorthSpaceId, request.UserId, capability, request.IsAllowed, ct);
        return Results.NoContent();
    }
}
