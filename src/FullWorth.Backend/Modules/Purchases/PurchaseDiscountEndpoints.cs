using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Purchases;

public static class PurchaseDiscountEndpoints
{
    public static IEndpointRouteBuilder MapPurchaseDiscountEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/purchases/{purchaseId:guid}/discounts").WithTags("Purchases");
        group.MapGet("/", async (Guid purchaseId, Guid fullWorthSpaceId, FullWorth.Backend.Security.CurrentUserContext user, PurchaseDiscountService service, CancellationToken ct) =>
            Map(await service.ListAsync(user.RequireUserId(), fullWorthSpaceId, purchaseId, ct)));
        group.MapPost("/", async (Guid purchaseId, Guid fullWorthSpaceId, PurchaseDiscountMutationWrite request, FullWorth.Backend.Security.CurrentUserContext user, PurchaseDiscountService service, CancellationToken ct) =>
            Map(await service.CreateAsync(user.RequireUserId(), fullWorthSpaceId, purchaseId, request, ct), true));
        group.MapPatch("/{discountId:guid}", async (Guid purchaseId, Guid discountId, Guid fullWorthSpaceId, PurchaseDiscountMutationWrite request, FullWorth.Backend.Security.CurrentUserContext user, PurchaseDiscountService service, CancellationToken ct) =>
            Map(await service.UpdateAsync(user.RequireUserId(), fullWorthSpaceId, purchaseId, discountId, request, ct)));
        group.MapDelete("/{discountId:guid}", async (Guid purchaseId, Guid discountId, Guid fullWorthSpaceId, FullWorth.Backend.Security.CurrentUserContext user, PurchaseDiscountService service, CancellationToken ct) =>
        {
            var result = await service.DeleteAsync(user.RequireUserId(), fullWorthSpaceId, purchaseId, discountId, ct);
            return result switch
            {
                PurchaseMutationResult.Success => Results.NoContent(),
                PurchaseMutationResult.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
                _ => Results.NotFound()
            };
        });
        return app;
    }

    private static IResult Map((PurchaseMutationResult Result, object? Value, string? Error) outcome, bool created = false) => outcome.Result switch
    {
        PurchaseMutationResult.Success when created => Results.Created(string.Empty, outcome.Value),
        PurchaseMutationResult.Success => Results.Ok(outcome.Value),
        PurchaseMutationResult.Invalid => Results.BadRequest(new { error = outcome.Error }),
        PurchaseMutationResult.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
        _ => Results.NotFound()
    };
}
