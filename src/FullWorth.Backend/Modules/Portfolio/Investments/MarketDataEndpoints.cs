using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Portfolio;

public static class MarketDataEndpoints
{
    public static IEndpointRouteBuilder MapMarketDataEndpoints(this IEndpointRouteBuilder app)
    {
        var group=app.MapGroup("/api/market-data").WithTags("Investments");
        group.MapGet("/securities/{securityId:guid}/effective-price",EffectivePrice);
        group.MapGet("/securities/{securityId:guid}/history",History);
        group.MapPost("/securities/{securityId:guid}/refresh",Refresh);
        group.MapGet("/search",SearchMetadata);
        return app;
    }

    private static async Task<IResult> EffectivePrice(
        Guid securityId,Guid fullWorthSpaceId,DateOnly? date,CurrentUserContext currentUser,
        SpaceAccess space,SecurityMarketDataService service,CancellationToken ct)
    {
        var userId=currentUser.RequireUserId();if(!await space.IsMemberAsync(userId,fullWorthSpaceId,ct))return Results.NotFound();
        var descriptor=await service.GetSecurityAsync(fullWorthSpaceId,securityId,ct);if(descriptor is null)return Results.NotFound();
        return Results.Ok(await service.ResolveEffectivePriceAsync(fullWorthSpaceId,securityId,date??DateOnly.FromDateTime(DateTime.UtcNow),ct));
    }

    private static async Task<IResult> History(
        Guid securityId,Guid fullWorthSpaceId,DateOnly? from,DateOnly? to,CurrentUserContext currentUser,
        SpaceAccess space,SecurityMarketDataService service,CancellationToken ct)
    {
        var userId=currentUser.RequireUserId();if(!await space.IsMemberAsync(userId,fullWorthSpaceId,ct))return Results.NotFound();
        var end=to??DateOnly.FromDateTime(DateTime.UtcNow);var start=from??end.AddYears(-1);
        return Results.Ok(await service.HistoryAsync(fullWorthSpaceId,securityId,start,end,ct));
    }

    private static async Task<IResult> Refresh(
        Guid securityId,Guid fullWorthSpaceId,DateOnly? from,DateOnly? to,CurrentUserContext currentUser,
        SpaceAccess space,SecurityMarketDataService service,CancellationToken ct)
    {
        var userId=currentUser.RequireUserId();
        if(!await space.HasCapabilityAsync(userId,fullWorthSpaceId,"investments.manage",ct))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        var end=to??DateOnly.FromDateTime(DateTime.UtcNow);var start=from??end.AddDays(-14);
        var result=await service.RefreshAsync(fullWorthSpaceId,securityId,start,end,ct);
        if(!result.ProviderAvailable)return Results.Conflict(new{state="provider_unavailable",error=result.Error});
        if(result.Stored==0)return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        return Results.Ok(new{state="updated",stored=result.Stored,provider=result.Provider});
    }

    private static async Task<IResult> SearchMetadata(
        Guid fullWorthSpaceId,string q,CurrentUserContext currentUser,SpaceAccess space,
        SecurityMarketDataService service,CancellationToken ct)
    {
        var userId=currentUser.RequireUserId();if(!await space.IsMemberAsync(userId,fullWorthSpaceId,ct))return Results.NotFound();
        return Results.Ok(await service.SearchMetadataAsync(q,ct));
    }
}
