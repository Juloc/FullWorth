using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Portfolio;

public static class AssetValuationEndpoints
{
    public static IEndpointRouteBuilder MapAssetValuationEndpoints(this IEndpointRouteBuilder app)
    {
        var valuations = app.MapGroup("/api/assets/{assetId:guid}/valuations").WithTags("Asset valuations");

        valuations.MapGet("/", async (
            Guid assetId,
            Guid fullWorthSpaceId,
            CurrentUserContext currentUser,
            AssetValuationStore store,
            CancellationToken ct) =>
        {
            var rows = await store.ListForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, assetId, ct);
            return rows is null ? Results.NotFound() : Results.Ok(rows);
        });

        valuations.MapPost("/", async (
            Guid assetId,
            Guid fullWorthSpaceId,
            AssetValuationWrite request,
            CurrentUserContext currentUser,
            AssetValuationStore store,
            CancellationToken ct) =>
            ToResult(await store.CreateForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, assetId, request, ct)));

        return app;
    }

    private static IResult ToResult(AssetValuationMutationOutcome outcome) => outcome.Result switch
    {
        AssetValuationMutationResult.Success => Results.Ok(outcome.Valuation),
        AssetValuationMutationResult.NotFound => Results.NotFound(),
        AssetValuationMutationResult.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
        AssetValuationMutationResult.Invalid => Results.BadRequest(new { error = outcome.Error ?? "Invalid valuation." }),
        _ => Results.StatusCode(StatusCodes.Status409Conflict)
    };
}
