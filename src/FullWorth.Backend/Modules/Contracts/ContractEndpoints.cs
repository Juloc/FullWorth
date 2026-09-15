using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Contracts;

public sealed record ContractSplitComponent(string Name, decimal Amount, Guid? CategoryId, string? Kind);
public sealed record ContractSplitWrite(string BundleName, IReadOnlyList<ContractSplitComponent> Components, string HistoryMode = "from_now");

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

        // Die Gegenrichtung zu merge-execute, und deshalb hier und nicht woanders: ein Vertrag, den
        // ein Zusammenfuehren geschluckt hat, wird wieder herausgeloest.
        group.MapDelete("/merge/{targetContractId:guid}/{sourceContractId:guid}", UnmergeContracts);
        group.MapPost("/{contractId:guid}/split", SplitContract);

        group.MapPost("/", async (Guid fullWorthSpaceId, ContractWrite request, CurrentUserContext currentUser, ContractStore store, CancellationToken ct) =>
            ToResult(await store.CreateForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, request, ct)));

        group.MapPut("/{id:guid}", async (Guid id, Guid fullWorthSpaceId, ContractWrite request, CurrentUserContext currentUser, ContractStore store, CancellationToken ct) =>
            ToResult(await store.UpdateForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, request, ct)));

        group.MapDelete("/{id:guid}", async (Guid id, Guid fullWorthSpaceId, CurrentUserContext currentUser, ContractStore store, CancellationToken ct) =>
            ToResult(await store.ArchiveForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, ct)));

        return app;
    }

    private static async Task<IResult> UnmergeContracts(
        Guid targetContractId,
        Guid sourceContractId,
        Guid fullWorthSpaceId,
        CurrentUserContext currentUser,
        ContractStore store,
        CancellationToken ct)
        => ToResult(await store.UnmergeForUserAsync(
            currentUser.RequireUserId(), fullWorthSpaceId, targetContractId, sourceContractId, ct));

    private static async Task<IResult> SplitContract(
        Guid contractId, Guid fullWorthSpaceId, ContractSplitWrite request, CurrentUserContext currentUser,
        ContractStore contracts, ContractLinkStore links, CancellationToken ct)
    {
        var uid = currentUser.RequireUserId();
        if (!await links.CanWriteContract(uid, fullWorthSpaceId, contractId, ct)) return Results.StatusCode(403);

        var parent = await contracts.FindInSpaceAsync(fullWorthSpaceId, contractId, ct);
        if (parent is null) return Results.NotFound();

        var components = (request.Components ?? [])
            .Where(part => !string.IsNullOrWhiteSpace(part.Name) && part.Amount > 0)
            .ToArray();
        if (components.Length < 2 || Math.Abs(components.Sum(part => part.Amount) - parent.Amount) > 0.01m)
            return Results.BadRequest(new { error = "Split components must contain at least two rows and equal the expected contract amount." });

        foreach (var part in components)
            if (part.CategoryId.HasValue
                && !await contracts.CategoryBelongsToSpaceAsync(fullWorthSpaceId, part.CategoryId.Value, ct))
                return Results.BadRequest(new { error = "Split contains an invalid category." });

        // Die Historie mitzunehmen heisst, fremde Buchungen anzufassen - dafuer reicht das
        // Vertragsrecht allein nicht, es braucht auch das Recht an jedem beteiligten Konto.
        var copyHistory = string.Equals(request.HistoryMode, "same_split", StringComparison.OrdinalIgnoreCase);
        if (copyHistory && !await links.AllContractLinksWritableAsync(uid, fullWorthSpaceId, contractId, ct))
            return Results.StatusCode(403);

        var (bundleId, children) = await contracts.SplitAsync(
            uid, fullWorthSpaceId, parent, request.BundleName, components, copyHistory, ct);

        return Results.Ok(new
        {
            bundleId,
            archivedContractId = contractId,
            contracts = children.Select(child => new { child.Id, child.Name, child.Amount })
        });
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
