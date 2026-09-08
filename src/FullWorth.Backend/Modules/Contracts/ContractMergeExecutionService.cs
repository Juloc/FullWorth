using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Intelligence;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Contracts;

public sealed record ContractMergeExecuteRequest(
    IReadOnlyList<Guid> ContractIds,
    Guid CanonicalContractId,
    string PreviewToken);

public sealed record ContractMergeExecuteView(
    Guid CanonicalContractId,
    IReadOnlyList<Guid> MergedContractIds,
    bool AlreadyApplied);

public enum ContractMergeExecuteResult
{
    Success,
    NotFound,
    Forbidden,
    Invalid,
    Conflict
}

public sealed record ContractMergeExecuteOutcome(
    ContractMergeExecuteResult Result,
    ContractMergeExecuteView? ResultView = null,
    string? Error = null);

/// <summary>
/// Explicit, user-confirmed execution path for a previously generated ContractMergePreview.
/// The preview token is revalidated immediately before delegating to the existing ContractStore merge.
/// </summary>
public sealed class ContractMergeExecutionService(
    FullWorthDbContext db,
    ContractStore store,
    ContractMergePreviewService previews,
    AutopilotRolloutSettings rollout)
{
    public async Task<ContractMergeExecuteOutcome> ExecuteAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        ContractMergeExecuteRequest request,
        CancellationToken ct)
    {
        if (!rollout.IsOn(AutopilotFeatures.ContractMergeExecution))
            return new(ContractMergeExecuteResult.Forbidden, Error: "Contract merge execution is disabled.");

        var ids = (request.ContractIds ?? Array.Empty<Guid>())
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToArray();

        if (ids.Length < 2 ||
            request.CanonicalContractId == Guid.Empty ||
            !ids.Contains(request.CanonicalContractId) ||
            string.IsNullOrWhiteSpace(request.PreviewToken))
            return new(ContractMergeExecuteResult.Invalid, Error: "Invalid merge confirmation.");

        var rows = await db.Contracts.AsNoTracking()
            .Where(contract =>
                contract.FullWorthSpaceId == fullWorthSpaceId &&
                ids.Contains(contract.Id))
            .Select(contract => new
            {
                contract.Id,
                contract.MergedIntoContractId
            })
            .ToListAsync(ct);

        if (rows.Count != ids.Length)
            return new(ContractMergeExecuteResult.NotFound);

        foreach (var id in ids)
        {
            var access = await store.GetRecordAccessForUserAsync(userId, fullWorthSpaceId, id, ct);
            if (access == ContractAccessLevel.None)
                return new(ContractMergeExecuteResult.NotFound);
            if (access != ContractAccessLevel.Write)
                return new(ContractMergeExecuteResult.Forbidden);
        }

        var target = rows.Single(row => row.Id == request.CanonicalContractId);
        if (target.MergedIntoContractId.HasValue)
            return new(ContractMergeExecuteResult.Conflict, Error: "Canonical contract is already merged.");

        var sourceIds = ids
            .Where(id => id != request.CanonicalContractId)
            .OrderBy(id => id)
            .ToArray();
        var sources = rows.Where(row => row.Id != request.CanonicalContractId).ToArray();

        if (sources.All(source => source.MergedIntoContractId == request.CanonicalContractId))
        {
            return new(
                ContractMergeExecuteResult.Success,
                new ContractMergeExecuteView(request.CanonicalContractId, sourceIds, AlreadyApplied: true));
        }

        if (sources.Any(source => source.MergedIntoContractId.HasValue))
            return new(ContractMergeExecuteResult.Conflict, Error: "A source contract was already merged elsewhere.");

        var previewOutcome = await previews.PreviewAsync(
            userId,
            fullWorthSpaceId,
            new ContractMergePreviewRequest(ids),
            ct);

        if (previewOutcome.Result == ContractMergePreviewResult.NotFound)
            return new(ContractMergeExecuteResult.NotFound);
        if (previewOutcome.Result == ContractMergePreviewResult.Invalid || previewOutcome.Preview is null)
            return new(ContractMergeExecuteResult.Invalid, Error: previewOutcome.Error);

        var preview = previewOutcome.Preview;
        if (!preview.ExecutionEnabled)
            return new(ContractMergeExecuteResult.Forbidden);

        if (preview.CanonicalContractId != request.CanonicalContractId)
            return new(ContractMergeExecuteResult.Conflict, Error: "Canonical contract changed since preview.");

        if (!string.Equals(preview.PreviewToken, request.PreviewToken.Trim(), StringComparison.Ordinal))
            return new(ContractMergeExecuteResult.Conflict, Error: "Contract state changed since preview.");

        var outcome = await store.MergeForUserAsync(
            userId,
            fullWorthSpaceId,
            request.CanonicalContractId,
            new ContractMergeRequest(sourceIds),
            ct);

        if (outcome.Result == ContractMutationResult.Success)
        {
            return new(
                ContractMergeExecuteResult.Success,
                new ContractMergeExecuteView(request.CanonicalContractId, sourceIds, AlreadyApplied: false));
        }

        // A second confirmation can race with the first one after both validated the same preview.
        // Re-read the merge graph before surfacing an error; if the intended state is already present,
        // treat the retry as the same successful operation.
        if (outcome.Result is ContractMutationResult.NotFound or ContractMutationResult.Invalid)
        {
            var after = await db.Contracts.AsNoTracking()
                .Where(contract =>
                    contract.FullWorthSpaceId == fullWorthSpaceId &&
                    sourceIds.Contains(contract.Id))
                .Select(contract => new { contract.Id, contract.MergedIntoContractId })
                .ToListAsync(ct);
            if (after.Count == sourceIds.Length &&
                after.All(contract => contract.MergedIntoContractId == request.CanonicalContractId))
            {
                return new(
                    ContractMergeExecuteResult.Success,
                    new ContractMergeExecuteView(request.CanonicalContractId, sourceIds, AlreadyApplied: true));
            }
        }

        return outcome.Result switch
        {
            ContractMutationResult.NotFound => new(ContractMergeExecuteResult.NotFound),
            ContractMutationResult.Forbidden => new(ContractMergeExecuteResult.Forbidden),
            ContractMutationResult.Invalid => new(ContractMergeExecuteResult.Invalid, Error: outcome.Error),
            _ => new(ContractMergeExecuteResult.Conflict, Error: "Contract merge could not be completed.")
        };
    }
}
