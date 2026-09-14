using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Purchases;

public static class PurchaseReceiptSourceEndpoints
{
    public static IEndpointRouteBuilder MapPurchaseReceiptSourceEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/purchases/{purchaseId:guid}/receipt-sources", async (
            Guid purchaseId,
            Guid fullWorthSpaceId,
            FullWorth.Backend.Security.CurrentUserContext user,
            PurchaseReceiptSourceService service,
            CancellationToken ct) =>
        {
            var value = await service.GetAsync(user.RequireUserId(), fullWorthSpaceId, purchaseId, ct);
            return value is null ? Results.NotFound() : Results.Ok(value);
        }).WithTags("Purchases");
        return app;
    }
}
