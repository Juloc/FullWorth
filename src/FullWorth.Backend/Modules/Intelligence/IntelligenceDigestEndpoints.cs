using System.Text.Json;
using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Intelligence;

public static class IntelligenceDigestEndpoints
{
    public static IEndpointRouteBuilder MapIntelligenceDigestEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/intelligence/digests").WithTags("Intelligence");

        group.MapGet("/", async (
            Guid fullWorthSpaceId,
            int? limit,
            CurrentUserContext currentUser,
            SpaceAccess space,
            IntelligenceDigestStore store,
            CancellationToken ct) =>
        {
            if (!await space.IsMemberAsync(currentUser.RequireUserId(), fullWorthSpaceId, ct))
                return Results.NotFound();

            var rows = await store.ListAsync(fullWorthSpaceId, limit ?? 30, ct);
            return Results.Ok(rows.Select(ToView));
        });

        group.MapGet("/{id:guid}", async (
            Guid id,
            CurrentUserContext currentUser,
            SpaceAccess space,
            IntelligenceDigestStore store,
            CancellationToken ct) =>
        {
            // Erst laden, dann pruefen: welchen Space die Zusammenfassung betrifft, steht in ihr.
            var row = await store.FindAsync(id, ct);
            if (row is null) return Results.NotFound();
            return await space.IsMemberAsync(currentUser.RequireUserId(), row.FullWorthSpaceId, ct)
                ? Results.Ok(ToView(row))
                : Results.NotFound();
        });

        return app;
    }

    private static object ToView(IntelligenceDigest row)
    {
        using var document = JsonDocument.Parse(row.SummaryJson);
        return new
        {
            row.Id,
            row.FullWorthSpaceId,
            row.PeriodType,
            row.PeriodKey,
            row.PeriodStart,
            row.PeriodEnd,
            summary = document.RootElement.Clone(),
            row.CreatedAt,
            row.UpdatedAt
        };
    }
}
