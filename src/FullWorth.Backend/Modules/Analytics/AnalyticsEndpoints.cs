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

        group.MapGet("/budget-status", async (Guid fullWorthSpaceId, int? year, int? month, string? currency, CurrentUserContext currentUser, AnalyticsService service, CancellationToken ct) =>
        {
            var now = DateTime.Today;
            return ToResult(await service.BudgetStatusForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, year ?? now.Year, month ?? now.Month, currency, ct));
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
