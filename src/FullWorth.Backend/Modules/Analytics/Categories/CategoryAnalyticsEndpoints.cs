using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Analytics.Categories;

public static class CategoryAnalyticsEndpoints
{
    public static IEndpointRouteBuilder MapCategoryAnalyticsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/analytics/categories", async (
            Guid fullWorthSpaceId,
            int? year,
            int? month,
            DateOnly? from,
            DateOnly? to,
            string? granularity,
            string? currency,
            Guid? accountId,
            Guid? accountGroupId,
            CurrentUserContext currentUser,
            CategoryAnalyticsService service,
            CancellationToken ct) =>
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            CategoryAnalyticsResult? result;
            if (from.HasValue || to.HasValue)
            {
                var resolvedTo = to ?? today;
                var resolvedFrom = from ?? resolvedTo.AddMonths(-1).AddDays(1);
                result = await service.CategorySpendForRangeForUserAsync(
                    currentUser.RequireUserId(), fullWorthSpaceId, resolvedFrom, resolvedTo,
                    granularity ?? "month", currency ?? "EUR", accountId, accountGroupId, ct);
            }
            else
            {
                result = await service.CategorySpendForUserAsync(
                    currentUser.RequireUserId(), fullWorthSpaceId, year ?? today.Year, month ?? today.Month, currency ?? "EUR", ct);
            }
            return result is null ? Results.NotFound() : Results.Ok(result);
        }).WithTags("Analytics");

        return app;
    }
}
