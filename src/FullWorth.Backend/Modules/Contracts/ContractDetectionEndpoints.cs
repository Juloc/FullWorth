using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Contracts;

public static class ContractDetectionEndpoints
{
    public static IEndpointRouteBuilder MapContractDetectionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/contracts/detection").WithTags("Contracts");

        group.MapGet("/", async (Guid fullWorthSpaceId, CurrentUserContext currentUser, ContractDetectionService service, CancellationToken ct) =>
        {
            var candidates = await service.DetectForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, ct);
            return candidates is null ? Results.NotFound() : Results.Ok(candidates);
        });

        group.MapPost("/accept", async (Guid fullWorthSpaceId, ContractCandidate candidate, CurrentUserContext currentUser, ContractDetectionService service, CancellationToken ct) =>
        {
            var outcome = await service.AcceptForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, candidate, ct);
            return outcome.Result switch
            {
                ContractMutationResult.Success => Results.Ok(outcome.Contract),
                ContractMutationResult.NotFound => Results.NotFound(),
                ContractMutationResult.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
                ContractMutationResult.Invalid => Results.BadRequest(new { error = outcome.Error ?? "Invalid contract candidate." }),
                _ => Results.StatusCode(StatusCodes.Status409Conflict)
            };
        });

        return app;
    }
}
