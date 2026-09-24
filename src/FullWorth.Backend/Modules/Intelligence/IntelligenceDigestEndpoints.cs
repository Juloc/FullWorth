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

        // Hier stand bis #177 ein zweiter Leser fuer EINE Zusammenfassung. Er hatte keinen Aufrufer,
        // und er haette auch keinen bekommen koennen, ohne etwas Neues zu koennen: die Liste liefert
        // je Eintrag denselben ToView(), samt vollstaendigem summary. Ein Detailabruf haette dieselbe
        // Antwort ein zweites Mal geholt.

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
