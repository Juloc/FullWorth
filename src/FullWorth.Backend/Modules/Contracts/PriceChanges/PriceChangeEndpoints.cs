using Microsoft.Extensions.Options;
using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Contracts.PriceChanges;

public static class PriceChangeEndpoints
{
    public static IEndpointRouteBuilder MapPriceChangeEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/contracts/price-changes").WithTags("Contract price changes");

        group.MapGet("/", async (Guid fullWorthSpaceId, CurrentUserContext currentUser, PriceChangeStore store, CancellationToken ct) =>
        {
            var suggestions = await store.ListForOwnerAsync(currentUser.RequireUserId(), fullWorthSpaceId, ct);
            return suggestions is null ? Results.NotFound() : Results.Ok(suggestions);
        });

        group.MapPost("/detect", async (Guid fullWorthSpaceId, PriceChangeDetectionRequest request, CurrentUserContext currentUser, PriceChangeStore store, IOptions<PriceChangeDetectionOptions> options, CancellationToken ct) =>
            ToResult(await store.DetectAsync(currentUser.RequireUserId(), fullWorthSpaceId, options.Value, request.DetectedOn, ct)));

        group.MapPost("/{id:guid}/confirm", async (Guid id, Guid fullWorthSpaceId, CurrentUserContext currentUser, PriceChangeStore store, CancellationToken ct) =>
            ToResult(await store.ConfirmPriceChangeAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, ct)));

        group.MapPost("/{id:guid}/ignore", async (Guid id, Guid fullWorthSpaceId, CurrentUserContext currentUser, PriceChangeStore store, CancellationToken ct) =>
            ToResult(await store.IgnorePriceChangeAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, ct)));

        return app;
    }

    private static IResult ToResult(PriceChangeDetectionOutcome outcome) => outcome.Result switch
    {
        PriceChangeMutationResult.Success => Results.Ok(new { suggestions = outcome.Suggestions ?? [], autoRefreshedContracts = outcome.AutoRefreshedContracts }),
        PriceChangeMutationResult.NotFound => Results.NotFound(),
        PriceChangeMutationResult.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
        _ => Results.StatusCode(StatusCodes.Status409Conflict)
    };

    private static IResult ToResult(PriceChangeMutationOutcome outcome) => outcome.Result switch
    {
        PriceChangeMutationResult.Success => Results.Ok(outcome.Suggestion),
        PriceChangeMutationResult.NotFound => Results.NotFound(),
        PriceChangeMutationResult.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
        _ => Results.StatusCode(StatusCodes.Status409Conflict)
    };
}
