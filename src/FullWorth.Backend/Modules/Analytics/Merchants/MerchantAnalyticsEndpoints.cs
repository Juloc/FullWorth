using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Analytics.Merchants;

public static class MerchantAnalyticsEndpoints
{
    public static IEndpointRouteBuilder MapMerchantAnalyticsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/analytics/merchants", async (
            Guid fullWorthSpaceId,
            int? year,
            int? month,
            DateOnly? from,
            DateOnly? to,
            string? granularity,
            string? currency,
            int? top,
            Guid? accountId,
            Guid? accountGroupId,
            CurrentUserContext currentUser,
            MerchantAnalyticsService service,
            CancellationToken ct) =>
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            MerchantAnalyticsResult? result;
            if (from.HasValue || to.HasValue)
            {
                var resolvedTo = to ?? today;
                var resolvedFrom = from ?? resolvedTo.AddMonths(-1).AddDays(1);
                result = await service.MerchantSpendForRangeForUserAsync(
                    currentUser.RequireUserId(), fullWorthSpaceId, resolvedFrom, resolvedTo,
                    granularity ?? "month", currency ?? "EUR", top ?? 10, accountId, accountGroupId, ct);
            }
            else
            {
                result = await service.MerchantSpendForUserAsync(
                    currentUser.RequireUserId(), fullWorthSpaceId, year ?? today.Year, month ?? today.Month,
                    currency ?? "EUR", top ?? 10, ct);
            }
            return result is null ? Results.NotFound() : Results.Ok(result);
        }).WithTags("Analytics");

        return app;
    }
}