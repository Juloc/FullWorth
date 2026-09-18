using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Analytics;

/// <summary>
/// #139: die durchgehende Zukunfts-Timeline hinter der Buchungsseite. Die Route bleibt bei
/// <c>/api/transactions</c>, obwohl die Berechnung in Analytics liegt - dafuer gibt es in diesem Repo
/// schon ein Vorbild (<c>/api/cashflow/available</c> wird aus <c>Modules/Reconciliation</c> bedient).
/// Die Modulwahl folgt der Abhaengigkeit, nicht dem Routenpfad: Analytics kennt Reconciliation und
/// Contracts bereits einseitig, der umgekehrte Weg (Reconciliation kennt die CarryOver-korrekte
/// Budget-Logik aus Analytics) haette einen neuen Modul-Zyklus erzeugt.
/// </summary>
public static class ForecastEndpoints
{
    public static IEndpointRouteBuilder MapForecastEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/transactions/forecast", async (
            Guid fullWorthSpaceId, Guid? accountId, Guid? groupId, int? horizonDays,
            CurrentUserContext currentUser, AnalyticsService analytics, CancellationToken ct) =>
        {
            var result = await analytics.ForecastTimelineAsync(
                currentUser.RequireUserId(), fullWorthSpaceId, accountId, groupId, horizonDays, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        }).WithTags("Transactions");
        return app;
    }
}
