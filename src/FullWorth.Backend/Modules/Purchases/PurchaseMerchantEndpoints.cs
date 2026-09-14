using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Purchases;

public static class PurchaseMerchantEndpoints
{
    public static IEndpointRouteBuilder MapPurchaseMerchantEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/purchases/{id:guid}/merchant").WithTags("Purchases");
        group.MapPost("/resolve", async (Guid id, Guid fullWorthSpaceId, FullWorth.Backend.Security.CurrentUserContext user, PurchaseMerchantService service, CancellationToken ct) =>
        {
            var outcome = await service.ResolveAsync(user.RequireUserId(), fullWorthSpaceId, id, ct);
            return outcome.Result switch { PurchaseMutationResult.Success => Results.Ok(outcome.Value), PurchaseMutationResult.Forbidden => Results.StatusCode(403), PurchaseMutationResult.Invalid => Results.BadRequest(), _ => Results.NotFound() };
        });
        group.MapPut("/", async (Guid id, Guid fullWorthSpaceId, PurchaseMerchantAssignRequest request, FullWorth.Backend.Security.CurrentUserContext user, PurchaseMerchantService service, CancellationToken ct) =>
        {
            var result = await service.AssignAsync(user.RequireUserId(), fullWorthSpaceId, id, request.MerchantId, ct);
            return result switch { PurchaseMutationResult.Success => Results.NoContent(), PurchaseMutationResult.Forbidden => Results.StatusCode(403), PurchaseMutationResult.Invalid => Results.BadRequest(), _ => Results.NotFound() };
        });
        return app;
    }
}
