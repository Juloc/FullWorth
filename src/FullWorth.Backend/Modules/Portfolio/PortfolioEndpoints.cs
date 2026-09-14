using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Portfolio;

public static class PortfolioEndpoints
{
    public static IEndpointRouteBuilder MapPortfolioEndpoints(this IEndpointRouteBuilder app)
    {
        var assets = app.MapGroup("/api/assets").WithTags("Assets");
        assets.MapGet("/", async (Guid fullWorthSpaceId, CurrentUserContext currentUser, PortfolioStore store, CancellationToken ct) =>
            Results.Ok(await store.AssetsForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, ct)));
        assets.MapGet("/{id:guid}", async (Guid id, Guid fullWorthSpaceId, CurrentUserContext currentUser, PortfolioStore store, CancellationToken ct) =>
        {
            var asset = await store.GetAssetForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, ct);
            return asset is null ? Results.NotFound() : Results.Ok(asset);
        });
        assets.MapPost("/", async (Guid fullWorthSpaceId, AssetWrite request, CurrentUserContext currentUser, PortfolioStore store, CancellationToken ct) =>
            ToResult(await store.CreateAssetForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, request, ct)));
        assets.MapPut("/{id:guid}", async (Guid id, Guid fullWorthSpaceId, AssetWrite request, CurrentUserContext currentUser, PortfolioStore store, CancellationToken ct) =>
            ToResult(await store.UpdateAssetForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, request, ct)));

        var liabilities = app.MapGroup("/api/liabilities").WithTags("Liabilities");
        liabilities.MapGet("/", async (Guid fullWorthSpaceId, CurrentUserContext currentUser, PortfolioStore store, CancellationToken ct) =>
            Results.Ok(await store.LiabilitiesForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, ct)));
        liabilities.MapGet("/{id:guid}", async (Guid id, Guid fullWorthSpaceId, CurrentUserContext currentUser, PortfolioStore store, CancellationToken ct) =>
        {
            var liability = await store.GetLiabilityForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, ct);
            return liability is null ? Results.NotFound() : Results.Ok(liability);
        });
        liabilities.MapPost("/", async (Guid fullWorthSpaceId, LiabilityWrite request, CurrentUserContext currentUser, PortfolioStore store, CancellationToken ct) =>
            ToResult(await store.CreateLiabilityForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, request, ct)));
        liabilities.MapPut("/{id:guid}", async (Guid id, Guid fullWorthSpaceId, LiabilityWrite request, CurrentUserContext currentUser, PortfolioStore store, CancellationToken ct) =>
            ToResult(await store.UpdateLiabilityForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, request, ct)));

        app.MapGet("/api/net-worth/history", async (Guid fullWorthSpaceId, DateOnly? from, DateOnly? to, CurrentUserContext currentUser, PortfolioStore store, CancellationToken ct) =>
            Results.Ok(await store.HistoryViewForUserAsync(fullWorthSpaceId, currentUser.RequireUserId(), from, to, ct))).WithTags("Net worth");
        return app;
    }

    private static IResult ToResult(AssetMutationOutcome outcome) => outcome.Result switch
    {
        PortfolioMutationResult.Success => Results.Ok(outcome.Asset),
        PortfolioMutationResult.NotFound => Results.NotFound(),
        PortfolioMutationResult.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
        PortfolioMutationResult.Invalid => Results.BadRequest(new { error = outcome.Error ?? "Invalid asset." }),
        _ => Results.StatusCode(StatusCodes.Status409Conflict)
    };

    private static IResult ToResult(LiabilityMutationOutcome outcome) => outcome.Result switch
    {
        PortfolioMutationResult.Success => Results.Ok(outcome.Liability),
        PortfolioMutationResult.NotFound => Results.NotFound(),
        PortfolioMutationResult.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
        PortfolioMutationResult.Invalid => Results.BadRequest(new { error = outcome.Error ?? "Invalid liability." }),
        _ => Results.StatusCode(StatusCodes.Status409Conflict)
    };
}
