using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Audit;

public static class AuditEndpoints
{
    public static IEndpointRouteBuilder MapAuditEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/audit", async (
            Guid fullWorthSpaceId,
            string? action,
            string? entityType,
            DateTimeOffset? before,
            Guid? beforeId,
            int? limit,
            CurrentUserContext currentUser,
            AuditStore store,
            CancellationToken ct) =>
        {
            var events = await store.ListForSpaceAsync(
                currentUser.RequireUserId(), fullWorthSpaceId, action, entityType, before, beforeId, limit ?? 100, ct);
            return events is null ? Results.NotFound() : Results.Ok(events);
        }).WithTags("Audit");

        return app;
    }
}
