using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Portfolio;

public static class VehicleMetalEndpoints
{
    public static IEndpointRouteBuilder MapVehicleMetalEndpoints(this IEndpointRouteBuilder app)
    {
        var vehicle = app.MapGroup("/api/assets/{assetId:guid}/vehicle").WithTags("Vehicle assets");

        vehicle.MapGet("/", async (
            Guid assetId,
            Guid fullWorthSpaceId,
            CurrentUserContext user,
            VehicleMetalStore store,
            CancellationToken ct) =>
            ToResult(await store.GetVehicleAsync(user.RequireUserId(), fullWorthSpaceId, assetId, ct)));

        vehicle.MapPut("/", async (
            Guid assetId,
            Guid fullWorthSpaceId,
            VehicleDetailWrite request,
            CurrentUserContext user,
            VehicleMetalStore store,
            CancellationToken ct) =>
            ToResult(await store.PutVehicleAsync(user.RequireUserId(), fullWorthSpaceId, assetId, request, ct)));

        vehicle.MapPost("/estimate", async (
            Guid assetId,
            Guid fullWorthSpaceId,
            VehicleEstimateWrite request,
            CurrentUserContext user,
            VehicleMetalStore store,
            CancellationToken ct) =>
            ToResult(await store.EstimateVehicleAsync(user.RequireUserId(), fullWorthSpaceId, assetId, request, ct)));

        vehicle.MapDelete("/", async (
            Guid assetId,
            Guid fullWorthSpaceId,
            CurrentUserContext user,
            VehicleMetalStore store,
            CancellationToken ct) =>
            ToResult(await store.DeleteVehicleAsync(user.RequireUserId(), fullWorthSpaceId, assetId, ct)));

        var metal = app.MapGroup("/api/assets/{assetId:guid}/precious-metal").WithTags("Precious metal assets");

        metal.MapGet("/", async (
            Guid assetId,
            Guid fullWorthSpaceId,
            CurrentUserContext user,
            VehicleMetalStore store,
            CancellationToken ct) =>
            ToResult(await store.GetMetalAsync(user.RequireUserId(), fullWorthSpaceId, assetId, ct)));

        metal.MapPut("/", async (
            Guid assetId,
            Guid fullWorthSpaceId,
            PreciousMetalDetailWrite request,
            CurrentUserContext user,
            VehicleMetalStore store,
            CancellationToken ct) =>
            ToResult(await store.PutMetalAsync(user.RequireUserId(), fullWorthSpaceId, assetId, request, ct)));

        metal.MapPost("/estimate", async (
            Guid assetId,
            Guid fullWorthSpaceId,
            PreciousMetalEstimateWrite request,
            CurrentUserContext user,
            VehicleMetalStore store,
            CancellationToken ct) =>
            ToResult(await store.EstimateMetalAsync(user.RequireUserId(), fullWorthSpaceId, assetId, request, ct)));

        metal.MapDelete("/", async (
            Guid assetId,
            Guid fullWorthSpaceId,
            CurrentUserContext user,
            VehicleMetalStore store,
            CancellationToken ct) =>
            ToResult(await store.DeleteMetalAsync(user.RequireUserId(), fullWorthSpaceId, assetId, ct)));

        app.MapRemainingSpecializedAssetEndpoints();
        return app;
    }

    private static IResult ToResult<T>(SpecializedAssetOutcome<T> outcome) => outcome.Result switch
    {
        SpecializedAssetMutationResult.Success => Results.Ok(outcome.Value),
        SpecializedAssetMutationResult.NotFound => Results.NotFound(),
        SpecializedAssetMutationResult.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
        SpecializedAssetMutationResult.Invalid => Results.BadRequest(new { error = outcome.Error ?? "Invalid specialized asset request." }),
        SpecializedAssetMutationResult.Conflict => Results.Conflict(new { error = outcome.Error ?? "Specialized asset conflict." }),
        _ => Results.StatusCode(StatusCodes.Status409Conflict)
    };

    private static IResult ToResult(SpecializedAssetMutationResult result) => result switch
    {
        SpecializedAssetMutationResult.Success => Results.NoContent(),
        SpecializedAssetMutationResult.NotFound => Results.NotFound(),
        SpecializedAssetMutationResult.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
        SpecializedAssetMutationResult.Invalid => Results.BadRequest(),
        SpecializedAssetMutationResult.Conflict => Results.StatusCode(StatusCodes.Status409Conflict),
        _ => Results.StatusCode(StatusCodes.Status409Conflict)
    };
}
