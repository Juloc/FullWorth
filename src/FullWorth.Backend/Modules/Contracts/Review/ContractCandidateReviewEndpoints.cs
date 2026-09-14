using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Contracts.Review;

public static class ContractCandidateReviewEndpoints
{
    public static IEndpointRouteBuilder MapContractCandidateReviewEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/contracts/detection/dismiss", async (
            Guid fullWorthSpaceId, CandidateDismissal request,
            CurrentUserContext currentUser, ContractCandidateReviewStore store, CancellationToken ct) =>
        {
            var result = await store.DismissForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, request, ct);
            return result switch
            {
                ContractMutationResult.Success => Results.NoContent(),
                ContractMutationResult.NotFound => Results.NotFound(),
                ContractMutationResult.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
                ContractMutationResult.Invalid => Results.Problem(
                    detail: "A counterparty and a three-letter currency are required.",
                    statusCode: StatusCodes.Status400BadRequest),
                _ => Results.StatusCode(StatusCodes.Status409Conflict),
            };
        }).WithTags("Contracts");

        return app;
    }
}
