namespace FullWorth.Backend.Modules.Purchases;

public static class PurchaseAnalyticsEndpoints
{
    public static IEndpointRouteBuilder MapPurchaseAnalyticsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/purchase-analytics").WithTags("Purchases");
        group.MapGet("/overview", async (Guid fullWorthSpaceId, DateOnly? from, DateOnly? to, FullWorth.Backend.Security.CurrentUserContext user, PurchaseAnalyticsService service, CancellationToken ct) => Map(await service.OverviewAsync(user.RequireUserId(), fullWorthSpaceId, from, to, ct)));
        group.MapGet("/savings", async (Guid fullWorthSpaceId, DateOnly? from, DateOnly? to, FullWorth.Backend.Security.CurrentUserContext user, PurchaseAnalyticsService service, CancellationToken ct) => Map(await service.SavingsAsync(user.RequireUserId(), fullWorthSpaceId, from, to, ct)));
        group.MapGet("/by-merchant", async (Guid fullWorthSpaceId, DateOnly? from, DateOnly? to, FullWorth.Backend.Security.CurrentUserContext user, PurchaseAnalyticsService service, CancellationToken ct) => Map(await service.ByMerchantAsync(user.RequireUserId(), fullWorthSpaceId, from, to, ct)));
        group.MapGet("/by-category", async (Guid fullWorthSpaceId, DateOnly? from, DateOnly? to, FullWorth.Backend.Security.CurrentUserContext user, PurchaseAnalyticsService service, CancellationToken ct) => Map(await service.ByCategoryAsync(user.RequireUserId(), fullWorthSpaceId, from, to, ct)));
        group.MapGet("/by-product", async (Guid fullWorthSpaceId, DateOnly? from, DateOnly? to, FullWorth.Backend.Security.CurrentUserContext user, PurchaseAnalyticsService service, CancellationToken ct) => Map(await service.ByProductAsync(user.RequireUserId(), fullWorthSpaceId, from, to, ct)));
        group.MapGet("/by-brand", async (Guid fullWorthSpaceId, DateOnly? from, DateOnly? to, FullWorth.Backend.Security.CurrentUserContext user, PurchaseAnalyticsService service, CancellationToken ct) => Map(await service.ByBrandAsync(user.RequireUserId(), fullWorthSpaceId, from, to, ct)));
        group.MapGet("/price-changes", async (Guid fullWorthSpaceId, DateOnly? from, DateOnly? to, FullWorth.Backend.Security.CurrentUserContext user, PurchaseAnalyticsService service, CancellationToken ct) => Map(await service.PriceChangesAsync(user.RequireUserId(), fullWorthSpaceId, from, to, ct)));
        group.MapGet("/products/{productId:guid}/merchants", async (Guid productId, Guid fullWorthSpaceId, DateOnly? from, DateOnly? to, FullWorth.Backend.Security.CurrentUserContext user, PurchaseAnalyticsService service, CancellationToken ct) => Map(await service.ProductMerchantComparisonAsync(user.RequireUserId(), fullWorthSpaceId, productId, from, to, ct)));
        app.MapPurchaseAdvancedInsightsEndpoints();
        return app;
    }

    private static IResult Map(object? value) => value is null ? Results.NotFound() : Results.Ok(value);
}