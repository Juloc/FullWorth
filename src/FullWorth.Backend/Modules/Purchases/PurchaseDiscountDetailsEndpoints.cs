using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Purchases;

public static class PurchaseDiscountDetailsEndpoints
{
    public static IEndpointRouteBuilder MapPurchaseDiscountDetailsEndpoints(this IEndpointRouteBuilder app)
    {
        Map(app.MapGroup("/api/purchases").WithTags("Purchases"));
        return app;
    }

    public static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/{id:guid}/financials", GetAsync);
        group.MapPut("/{id:guid}/financials", PutAsync);
    }

    private static async Task<IResult> GetAsync(
        Guid id,
        Guid fullWorthSpaceId,
        CurrentUserContext currentUser,
        PurchaseDiscountDetailsStore store,
        CancellationToken ct)
    {
        var result = await store.GetAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, ct);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }

    private static async Task<IResult> PutAsync(
        Guid id,
        Guid fullWorthSpaceId,
        PurchaseFinancialWrite request,
        CurrentUserContext currentUser,
        PurchaseDiscountDetailsStore store,
        CancellationToken ct)
    {
        try
        {
            var result = await store.SaveAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, request, ct);
            return result switch
            {
                PurchaseMutationResult.Success => Results.NoContent(),
                PurchaseMutationResult.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
                PurchaseMutationResult.NotFound => Results.NotFound(),
                _ => Results.BadRequest()
            };
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }
}
