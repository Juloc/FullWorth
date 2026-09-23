using FullWorth.Backend.Modules.Budgets;
using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Analytics;

public static class AnalyticsEndpoints
{
    public static IEndpointRouteBuilder MapAnalyticsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/analytics").WithTags("Analytics");

        group.MapGet("/overview", async (
            Guid fullWorthSpaceId,
            DateOnly? from,
            DateOnly? to,
            string? currency,
            string? granularity,
            Guid? accountId,
            Guid? accountGroupId,
            CurrentUserContext currentUser,
            AnalyticsService service,
            CancellationToken ct) =>
            ToResult(await service.OverviewForUserAsync(
                currentUser.RequireUserId(), fullWorthSpaceId, from, to, currency,
                granularity ?? "month", accountId, accountGroupId, ct)));

        group.MapGet("/dashboard", async (Guid fullWorthSpaceId, string? currency, CurrentUserContext currentUser, AnalyticsService service, CancellationToken ct) =>
        {
            var result = await service.DashboardForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, currency, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });

        // Der Budgetstand kommt aus dem Budget-Modul, nicht aus einer zweiten Rechnung hier. Bis
        // 2026-09-23 stand an dieser Stelle eine eigene, und sie lief trotzdem nie: eine Middleware
        // fing die Route vorher ab und antwortete aus genau dem Dienst, der jetzt hier steht.
        group.MapGet("/budget-status", async (Guid fullWorthSpaceId, int? year, int? month, string? currency, CurrentUserContext currentUser, BudgetReconciliationService budgets, CancellationToken ct) =>
        {
            if (month is < 1 or > 12) return Results.BadRequest(new { error = "month must be between 1 and 12." });
            if (year is < 1 or > 9999) return Results.BadRequest(new { error = "year is invalid." });
            return ToResult(await budgets.GetListAsync(currentUser.RequireUserId(), fullWorthSpaceId, year, month, currency, ct));
        });

        group.MapGet("/forecast", async (Guid fullWorthSpaceId, int? months, string? currency, CurrentUserContext currentUser, AnalyticsService service, CancellationToken ct) =>
            ToResult(await service.ForecastForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, months ?? 12, currency, ct)));

        group.MapGet("/chart", async (Guid fullWorthSpaceId, string? measure, string? dimension, DateOnly? from, DateOnly? to, string? currency, CurrentUserContext currentUser, AnalyticsService service, CancellationToken ct) =>
        {
            var today = DateOnly.FromDateTime(DateTime.Today);
            var f = from ?? today.AddMonths(-12);
            var t = to ?? today;
            if (t < f) (f, t) = (t, f);
            if (t.DayNumber - f.DayNumber > 1830) f = t.AddDays(-1830); // clamp span to ~5 years
            return ToResult(await service.ChartForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, measure ?? "spend", dimension ?? "month", f, t, currency, ct));
        });
        return app;
    }

    private static IResult ToResult(object? result) => result is null ? Results.NotFound() : Results.Ok(result);
}
