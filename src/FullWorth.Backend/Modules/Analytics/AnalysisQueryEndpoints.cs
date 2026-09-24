using FullWorth.Backend.Modules.Reconciliation;
using System.Text.Json;
using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Analytics;

/// <summary>
/// Eine gemerkte Auswertung.
///
/// <c>Period</c> kam mit #177 dazu, und zwar aus einem Grund, der sonst still verlorengegangen waere:
/// die Oberflaeche laesst "letzte 12 Monate" waehlen, der Server kennt nur von-bis. Ohne dieses Feld
/// haette eine gemerkte Auswertung ihren Zeitraum EINGEFROREN - beim naechsten Oeffnen stuenden
/// dieselben Tage da statt derselben Frage. <c>Query</c> bleibt trotzdem vollstaendig: was gespeichert
/// ist, muss sich auch ohne die Oberflaeche wieder abfragen lassen.
/// </summary>
public sealed record SavedAnalysisWrite(string Name, AnalysisQueryWrite Query, string ChartType = "bar", int SchemaVersion = 1, string? Period = null);

/// <summary>
/// Die freie Auswertung: eine Kennzahl, ueber eine Dimension gruppiert - und die Auswertungen, die
/// sich jemand gemerkt hat.
///
/// Hier steht, was die Antwort ist: welche Kennzahlen und Dimensionen es gibt, wie ein Betrag seinem
/// Schluessel zugeordnet wird, und wie aus den Betraegen eine Zahl wird. Woher die Betraege kommen,
/// entscheidet <see cref="AnalysisContributionService"/>.
///
/// <c>Query</c> und <c>Sankey</c> hatten hier bis 2026-09-23 eine eigene Rechnung, und sie lief nie:
/// <c>FinancialReconciliationMiddleware</c> fing beide Routen vor der Zuordnung ab und beantwortete
/// sie aus <c>FinancialReconciliationReportService</c>. Die Handler rufen den Dienst jetzt selbst auf,
/// die Middleware ist weg.
/// </summary>
public static class AnalysisQueryEndpoints
{
    public static IEndpointRouteBuilder MapAnalysisQueryEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/analytics/query", Query).WithTags("Analytics");
        app.MapPost("/api/analytics/sankey", Sankey).WithTags("Analytics");
        var saved = app.MapGroup("/api/saved-analyses").WithTags("Analytics");
        saved.MapGet("/", ListSaved);
        saved.MapPost("/", CreateSaved);
        saved.MapPut("/{id:guid}", UpdateSaved);
        saved.MapDelete("/{id:guid}", DeleteSaved);
        return app;
    }

    private static async Task<IResult> Query(
        Guid fullWorthSpaceId, AnalysisQueryWrite request, CurrentUserContext currentUser,
        FinancialReconciliationReportService reports, CancellationToken ct) =>
        await AnswerAsync(fullWorthSpaceId, request, currentUser, reports, sankey: false, ct);

    /// <summary>
    /// Das Flussdiagramm derselben Auswertung: Einnahmen fliessen in "verfuegbar", von dort in die
    /// obersten Kategorien, und der Rest bleibt stehen.
    /// </summary>
    private static async Task<IResult> Sankey(
        Guid fullWorthSpaceId, AnalysisQueryWrite request, CurrentUserContext currentUser,
        FinancialReconciliationReportService reports, CancellationToken ct) =>
        await AnswerAsync(fullWorthSpaceId, request, currentUser, reports, sankey: true, ct);

    private static async Task<IResult> AnswerAsync(
        Guid fullWorthSpaceId, AnalysisQueryWrite request, CurrentUserContext currentUser,
        FinancialReconciliationReportService reports, bool sankey, CancellationToken ct)
    {
        try
        {
            var result = await reports.AnalyticsAsync(currentUser.RequireUserId(), fullWorthSpaceId, request, sankey, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        }
        catch (ArgumentException exception)
        {
            return Results.BadRequest(new { error = exception.Message });
        }
        catch (UnauthorizedAccessException)
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }
    }
    private static async Task<IResult> ListSaved(
        Guid fullWorthSpaceId, CurrentUserContext currentUser, SpaceAccess space,
        SavedAnalysisStore store, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await space.IsMemberAsync(userId, fullWorthSpaceId, ct)) return Results.NotFound();

        var rows = await store.ListAsync(userId, fullWorthSpaceId, ct);
        return Results.Ok(rows.Select(row => new
        {
            id = row.Id,
            name = row.Name,
            schemaVersion = row.SchemaVersion,
            config = JsonSerializer.Deserialize<JsonElement>(row.ConfigJson),
            createdAt = row.CreatedAt,
            updatedAt = row.UpdatedAt
        }));
    }

    private static async Task<IResult> CreateSaved(
        Guid fullWorthSpaceId, SavedAnalysisWrite request, CurrentUserContext currentUser, SpaceAccess space,
        SavedAnalysisStore store, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await space.IsMemberAsync(userId, fullWorthSpaceId, ct)) return Results.NotFound();
        if (string.IsNullOrWhiteSpace(request.Name) || FinancialReconciliationReportService.ValidateAnalysis(request.Query) is not null)
            return Results.BadRequest(new { error = "Invalid saved analysis." });

        return Results.Ok(new { id = await store.CreateAsync(userId, fullWorthSpaceId, request, ct) });
    }

    private static async Task<IResult> UpdateSaved(
        Guid id, Guid fullWorthSpaceId, SavedAnalysisWrite request, CurrentUserContext currentUser,
        SavedAnalysisStore store, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || FinancialReconciliationReportService.ValidateAnalysis(request.Query) is not null)
            return Results.BadRequest();

        return await store.UpdateAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, request, ct)
            ? Results.NoContent()
            : Results.NotFound();
    }

    private static async Task<IResult> DeleteSaved(
        Guid id, Guid fullWorthSpaceId, CurrentUserContext currentUser,
        SavedAnalysisStore store, CancellationToken ct) =>
        await store.DeleteAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, ct)
            ? Results.NoContent()
            : Results.NotFound();
}
