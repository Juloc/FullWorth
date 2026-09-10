using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Pension;

/// <summary>
/// The Altersvorsorge (bAV) API. Every route is scoped to a fullworth space and gated by the store:
/// a non-member gets 404 so the existence of a contract does not leak, a member without the owner
/// role gets 403 on a write, and a write that would duplicate an existing contract or an existing
/// snapshot date gets 409 with the id of what already exists — which is how an annual statement ends
/// up as a snapshot instead of a second contract.
/// </summary>
public static class PensionEndpoints
{
    public static IEndpointRouteBuilder MapPensionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/pension").WithTags("Pension");

        group.MapGet("/overview", async (
            Guid fullWorthSpaceId,
            CurrentUserContext currentUser,
            PensionStore store,
            CancellationToken ct) =>
        {
            var overview = await store.OverviewAsync(currentUser.RequireUserId(), fullWorthSpaceId, ct);
            return overview is null ? Results.NotFound() : Results.Ok(overview);
        });

        group.MapGet("/contracts", async (
            Guid fullWorthSpaceId,
            CurrentUserContext currentUser,
            PensionStore store,
            CancellationToken ct) =>
        {
            var rows = await store.ListAsync(currentUser.RequireUserId(), fullWorthSpaceId, ct);
            return rows is null ? Results.NotFound() : Results.Ok(rows);
        });

        // Existing-contract detection. A POST because a policy number is personal data and must never
        // end up in a URL, a query string or an access log.
        group.MapPost("/contracts/match", async (
            Guid fullWorthSpaceId,
            BavContractMatchRequest request,
            CurrentUserContext currentUser,
            PensionStore store,
            CancellationToken ct) =>
        {
            var result = await store.MatchAsync(currentUser.RequireUserId(), fullWorthSpaceId, request, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });

        group.MapPost("/contracts", async (
            Guid fullWorthSpaceId,
            BavContractWrite request,
            CurrentUserContext currentUser,
            PensionStore store,
            CancellationToken ct) =>
            ContractResult(await store.CreateAsync(currentUser.RequireUserId(), fullWorthSpaceId, request, ct)));

        group.MapGet("/contracts/{contractId:guid}", async (
            Guid contractId,
            Guid fullWorthSpaceId,
            CurrentUserContext currentUser,
            PensionStore store,
            CancellationToken ct) =>
        {
            var detail = await store.GetAsync(currentUser.RequireUserId(), fullWorthSpaceId, contractId, ct);
            return detail is null ? Results.NotFound() : Results.Ok(detail);
        });

        group.MapPut("/contracts/{contractId:guid}", async (
            Guid contractId,
            Guid fullWorthSpaceId,
            BavContractWrite request,
            CurrentUserContext currentUser,
            PensionStore store,
            CancellationToken ct) =>
            ContractResult(await store.UpdateAsync(currentUser.RequireUserId(), fullWorthSpaceId, contractId, request, ct)));

        group.MapDelete("/contracts/{contractId:guid}", async (
            Guid contractId,
            Guid fullWorthSpaceId,
            CurrentUserContext currentUser,
            PensionStore store,
            CancellationToken ct) =>
            await store.DeleteAsync(currentUser.RequireUserId(), fullWorthSpaceId, contractId, ct) switch
            {
                BavMutationResult.Success => Results.NoContent(),
                BavMutationResult.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
                _ => Results.NotFound()
            });

        group.MapGet("/contracts/{contractId:guid}/snapshots", async (
            Guid contractId,
            Guid fullWorthSpaceId,
            CurrentUserContext currentUser,
            PensionStore store,
            CancellationToken ct) =>
        {
            var rows = await store.SnapshotsAsync(currentUser.RequireUserId(), fullWorthSpaceId, contractId, ct);
            return rows is null ? Results.NotFound() : Results.Ok(rows);
        });

        group.MapPost("/contracts/{contractId:guid}/snapshots", async (
            Guid contractId,
            Guid fullWorthSpaceId,
            BavSnapshotWrite request,
            CurrentUserContext currentUser,
            PensionStore store,
            CancellationToken ct) =>
        {
            var outcome = await store.AddSnapshotAsync(currentUser.RequireUserId(), fullWorthSpaceId, contractId, request, ct);
            return outcome.Result switch
            {
                BavMutationResult.Success => Results.Ok(outcome.Snapshot),
                BavMutationResult.NotFound => Results.NotFound(),
                BavMutationResult.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
                BavMutationResult.Conflict => Results.Conflict(new
                {
                    error = outcome.Error,
                    existingSnapshotId = outcome.ExistingSnapshotId
                }),
                _ => Results.BadRequest(new { error = outcome.Error ?? "Invalid snapshot." })
            };
        });

        group.MapPost("/contracts/{contractId:guid}/contributions", async (
            Guid contractId,
            Guid fullWorthSpaceId,
            BavContributionWrite request,
            CurrentUserContext currentUser,
            PensionStore store,
            CancellationToken ct) =>
        {
            var outcome = await store.AddContributionAsync(currentUser.RequireUserId(), fullWorthSpaceId, contractId, request, ct);
            return Simple(outcome.Result, outcome.Contribution, outcome.Error, "Invalid contribution.");
        });

        group.MapPost("/contracts/{contractId:guid}/costs", async (
            Guid contractId,
            Guid fullWorthSpaceId,
            BavCostWrite request,
            CurrentUserContext currentUser,
            PensionStore store,
            CancellationToken ct) =>
        {
            var outcome = await store.AddCostAsync(currentUser.RequireUserId(), fullWorthSpaceId, contractId, request, ct);
            return Simple(outcome.Result, outcome.Cost, outcome.Error, "Invalid cost.");
        });

        group.MapPost("/contracts/{contractId:guid}/allocations", async (
            Guid contractId,
            Guid fullWorthSpaceId,
            BavAllocationWrite request,
            CurrentUserContext currentUser,
            PensionStore store,
            CancellationToken ct) =>
        {
            var outcome = await store.AddAllocationAsync(currentUser.RequireUserId(), fullWorthSpaceId, contractId, request, ct);
            return Simple(outcome.Result, outcome.Allocation, outcome.Error, "Invalid fund allocation.");
        });

        return app;
    }

    private static IResult ContractResult(BavContractOutcome outcome) => outcome.Result switch
    {
        BavMutationResult.Success => Results.Ok(outcome.Contract),
        BavMutationResult.NotFound => Results.NotFound(),
        BavMutationResult.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
        // The caller is told which contract this is, so a statement for it becomes a snapshot.
        BavMutationResult.Conflict => Results.Conflict(new
        {
            error = outcome.Error,
            existingContractId = outcome.ExistingContractId
        }),
        _ => Results.BadRequest(new { error = outcome.Error ?? "Invalid contract." })
    };

    private static IResult Simple<T>(BavMutationResult result, T? value, string? error, string fallback) => result switch
    {
        BavMutationResult.Success => Results.Ok(value),
        BavMutationResult.NotFound => Results.NotFound(),
        BavMutationResult.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
        BavMutationResult.Conflict => Results.Conflict(new { error = error ?? fallback }),
        _ => Results.BadRequest(new { error = error ?? fallback })
    };
}
