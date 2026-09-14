using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Contracts;

public static class ContractEndpoints
{
    public static IEndpointRouteBuilder MapContractEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/contracts").WithTags("Contracts");

        group.MapGet("/", async (Guid fullWorthSpaceId, CurrentUserContext currentUser, ContractStore store, CancellationToken ct) =>
            Results.Ok(await store.ListForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, ct)));

        group.MapGet("/{id:guid}", async (Guid id, Guid fullWorthSpaceId, CurrentUserContext currentUser, ContractStore store, CancellationToken ct) =>
        {
            var contract = await store.GetForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, ct);
            return contract is null ? Results.NotFound() : Results.Ok(contract);
        });

        group.MapGet("/{id:guid}/activity", async (Guid id, Guid fullWorthSpaceId, CurrentUserContext currentUser, ContractStore store, CancellationToken ct) =>
        {
            var activity = await store.GetActivityForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, ct);
            return activity is null ? Results.NotFound() : Results.Ok(activity);
        });

        group.MapGet("/{id:guid}/merged-sources", async (Guid id, Guid fullWorthSpaceId, CurrentUserContext currentUser, ContractStore store, CancellationToken ct) =>
        {
            var sources = await store.ListMergedSourcesForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, ct);
            return sources is null ? Results.NotFound() : Results.Ok(sources);
        });

        group.MapPost("/merge-preview", async (
            Guid fullWorthSpaceId,
            ContractMergePreviewRequest request,
            CurrentUserContext currentUser,
            ContractMergePreviewService previewService,
            CancellationToken ct) =>
        {
            var outcome = await previewService.PreviewAsync(
                currentUser.RequireUserId(),
                fullWorthSpaceId,
                request,
                ct);
            return outcome.Result switch
            {
                ContractMergePreviewResult.Success => Results.Ok(outcome.Preview),
                ContractMergePreviewResult.NotFound => Results.NotFound(),
                ContractMergePreviewResult.Invalid => Results.BadRequest(new { error = outcome.Error ?? "Invalid contract merge preview." }),
                _ => Results.StatusCode(StatusCodes.Status409Conflict)
            };
        });

        group.MapPost("/merge-execute", async (
            Guid fullWorthSpaceId,
            ContractMergeExecuteRequest request,
            CurrentUserContext currentUser,
            ContractMergeExecutionService executionService,
            CancellationToken ct) =>
        {
            var outcome = await executionService.ExecuteAsync(
                currentUser.RequireUserId(),
                fullWorthSpaceId,
                request,
                ct);
            return outcome.Result switch
            {
                ContractMergeExecuteResult.Success => Results.Ok(outcome.ResultView),
                ContractMergeExecuteResult.NotFound => Results.NotFound(),
                ContractMergeExecuteResult.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
                ContractMergeExecuteResult.Invalid => Results.BadRequest(new { error = outcome.Error ?? "Invalid contract merge confirmation." }),
                ContractMergeExecuteResult.Conflict => Results.Conflict(new { error = outcome.Error ?? "Contract state changed. Open a new preview." }),
                _ => Results.StatusCode(StatusCodes.Status409Conflict)
            };
        });

        group.MapPost("/", async (Guid fullWorthSpaceId, ContractWrite request, CurrentUserContext currentUser, ContractStore store, CancellationToken ct) =>
            ToResult(await store.CreateForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, request, ct)));

        group.MapPut("/{id:guid}", async (Guid id, Guid fullWorthSpaceId, ContractWrite request, CurrentUserContext currentUser, ContractStore store, CancellationToken ct) =>
            ToResult(await store.UpdateForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, request, ct)));

        group.MapDelete("/{id:guid}", async (Guid id, Guid fullWorthSpaceId, CurrentUserContext currentUser, ContractStore store, CancellationToken ct) =>
            ToResult(await store.ArchiveForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, ct)));

        return app;
    }

    private static IResult ToResult(ContractMutationOutcome outcome) => outcome.Result switch
    {
        ContractMutationResult.Success => Results.Ok(outcome.Contract),
        ContractMutationResult.NotFound => Results.NotFound(),
        ContractMutationResult.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
        ContractMutationResult.Invalid => Results.BadRequest(new { error = outcome.Error ?? "Invalid contract." }),
        _ => Results.StatusCode(StatusCodes.Status409Conflict)
    };

    private static IResult ToResult(ContractMutationResult result) => result switch
    {
        ContractMutationResult.Success => Results.NoContent(),
        ContractMutationResult.NotFound => Results.NotFound(),
        ContractMutationResult.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
        ContractMutationResult.Invalid => Results.BadRequest(),
        _ => Results.StatusCode(StatusCodes.Status409Conflict)
    };
}
