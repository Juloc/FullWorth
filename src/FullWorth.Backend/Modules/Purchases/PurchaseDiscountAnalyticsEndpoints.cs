using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Purchases;

public static class PurchaseDiscountAnalyticsEndpoints
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/discount-analytics", GetAsync);
    }

    private static async Task<IResult> GetAsync(
        Guid fullWorthSpaceId,
        DateOnly? from,
        DateOnly? to,
        CurrentUserContext currentUser,
        PurchaseDiscountAnalyticsService service,
        CancellationToken ct)
    {
        try
        {
            var result = await service.GetAsync(currentUser.RequireUserId(), fullWorthSpaceId, from, to, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }
}
