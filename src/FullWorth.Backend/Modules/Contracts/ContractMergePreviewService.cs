namespace FullWorth.Backend.Modules.Contracts;

public sealed record ContractMergePreviewRequest(IReadOnlyList<Guid> ContractIds);

public sealed record ContractMergePreviewContract(
    Guid Id,
    string Name,
    string? ProviderName,
    Guid? AccountId,
    Guid? CategoryId,
    decimal Amount,
    string Currency,
    string BillingCycle,
    int Interval,
    bool AutoDetected,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateOnly? LastPayment,
    int MatchedPaymentCount);

public sealed record ContractMergePreviewField(
    string Field,
    string? Value,
    Guid SourceContractId,
    string Reason);

public sealed record ContractMergePreview(
    Guid CanonicalContractId,
    IReadOnlyList<Guid> SourceContractIds,
    IReadOnlyList<ContractMergePreviewContract> Contracts,
    IReadOnlyList<ContractMergePreviewField> CanonicalFields,
    IReadOnlyList<ContractPayment> CombinedPayments,
    int CombinedPaymentCount,
    IReadOnlyList<Guid> AccountIds,
    IReadOnlyList<string> ProviderAliases,
    IReadOnlyList<string> Warnings,
    bool ExecutionEnabled);

public enum ContractMergePreviewResult
{
    Success,
    NotFound,
    Invalid
}

public sealed record ContractMergePreviewOutcome(
    ContractMergePreviewResult Result,
    ContractMergePreview? Preview = null,
    string? Error = null);

/// <summary>
/// Read-only deterministic preview for an existing ContractStore merge. This service never changes
/// contract rows, links, transactions, or MergedIntoContractId.
/// </summary>
public sealed class ContractMergePreviewService(ContractStore store)
{
    public async Task<ContractMergePreviewOutcome> PreviewAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        ContractMergePreviewRequest request,
        CancellationToken ct)
    {
        var ids = (request.ContractIds ?? Array.Empty<Guid>())
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToArray();
        if (ids.Length < 2)
            return new(ContractMergePreviewResult.Invalid, Error: "Select at least two contracts.");

        var contracts = new List<(ContractView Contract, ContractActivity? Activity)>();
        foreach (var id in ids)
        {
            var contract = await store.GetForUserAsync(userId, fullWorthSpaceId, id, ct);
            if (contract is null) return new(ContractMergePreviewResult.NotFound);
            var activity = await store.GetActivityForUserAsync(userId, fullWorthSpaceId, id, ct);
            contracts.Add((contract, activity));
        }

        if (contracts.Select(x => x.Contract.Currency).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            return new(ContractMergePreviewResult.Invalid, Error: "Contracts with different currencies cannot be merged.");

        var canonical = contracts
            .OrderByDescending(x => x.Activity?.LastPayment ?? DateOnly.MinValue)
            .ThenByDescending(x => x.Contract.IsActive)
            .ThenByDescending(x => x.Contract.UpdatedAt)
            .ThenByDescending(x => x.Contract.CreatedAt)
            .ThenBy(x => x.Contract.Id)
            .First();

        var sourceIds = contracts
            .Where(x => x.Contract.Id != canonical.Contract.Id)
            .Select(x => x.Contract.Id)
            .OrderBy(x => x)
            .ToArray();

        var allPayments = contracts
            .SelectMany(x => x.Activity?.Payments ?? Array.Empty<ContractPayment>())
            .GroupBy(payment => payment.Id)
            .Select(group => group
                .OrderByDescending(payment => payment.Date ?? DateOnly.MinValue)
                .First())
            .OrderByDescending(payment => payment.Date ?? DateOnly.MinValue)
            .ThenBy(payment => payment.Id)
            .ToArray();
        var payments = allPayments.Take(120).ToArray();

        var accountIds = contracts
            .Select(x => x.Contract.AccountId)
            .Where(x => x.HasValue)
            .Select(x => x!.Value)
            .Distinct()
            .OrderBy(x => x)
            .ToArray();

        var aliases = contracts
            .SelectMany(x => new[] { x.Contract.Name, x.Contract.ProviderName })
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var warnings = BuildWarnings(contracts);
        var fields = BuildCanonicalFields(canonical.Contract);

        var previewContracts = contracts
            .Select(x => new ContractMergePreviewContract(
                x.Contract.Id,
                x.Contract.Name,
                x.Contract.ProviderName,
                x.Contract.AccountId,
                x.Contract.CategoryId,
                x.Contract.Amount,
                x.Contract.Currency,
                x.Contract.BillingCycle,
                x.Contract.Interval,
                x.Contract.AutoDetected,
                x.Contract.IsActive,
                x.Contract.CreatedAt,
                x.Contract.UpdatedAt,
                x.Activity?.LastPayment,
                x.Activity?.MatchedCount ?? 0))
            .OrderBy(x => x.Id == canonical.Contract.Id ? 0 : 1)
            .ThenBy(x => x.Id)
            .ToArray();

        return new(
            ContractMergePreviewResult.Success,
            new ContractMergePreview(
                canonical.Contract.Id,
                sourceIds,
                previewContracts,
                fields,
                payments,
                allPayments.Length,
                accountIds,
                aliases,
                warnings,
                ExecutionEnabled: false));
    }

    private static IReadOnlyList<ContractMergePreviewField> BuildCanonicalFields(ContractView contract) =>
    [
        new("name", contract.Name, contract.Id, "canonical-contract"),
        new("providerName", contract.ProviderName, contract.Id, "canonical-contract"),
        new("accountId", contract.AccountId?.ToString("D"), contract.Id, "latest-payment-canonical"),
        new("categoryId", contract.CategoryId?.ToString("D"), contract.Id, "canonical-contract"),
        new("amount", contract.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture), contract.Id, "canonical-contract"),
        new("currency", contract.Currency, contract.Id, "required-equal"),
        new("billingCycle", contract.BillingCycle, contract.Id, "canonical-contract"),
        new("interval", contract.Interval.ToString(System.Globalization.CultureInfo.InvariantCulture), contract.Id, "canonical-contract")
    ];

    private static IReadOnlyList<string> BuildWarnings(
        IReadOnlyList<(ContractView Contract, ContractActivity? Activity)> contracts)
    {
        var warnings = new List<string>();

        var identities = contracts
            .Select(x => ContractIdentity.Normalize(x.Contract.ProviderName ?? x.Contract.Name))
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (identities.Length > 1) warnings.Add("provider-identity-differs");

        var cycles = contracts
            .Select(x => $"{x.Contract.BillingCycle}:{Math.Max(1, x.Contract.Interval)}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (cycles.Length > 1) warnings.Add("billing-cycle-differs");

        var amounts = contracts.Select(x => Math.Abs(x.Contract.Amount)).Where(x => x > 0m).ToArray();
        if (amounts.Length > 1)
        {
            var min = amounts.Min();
            var max = amounts.Max();
            if (max > 0m && (max - min) / max > .20m) warnings.Add("amount-differs");
        }

        var histories = contracts
            .Where(x => x.Activity?.LastPayment is not null)
            .OrderBy(x => x.Activity!.LastPayment)
            .ToArray();
        if (histories.Length == 0) warnings.Add("no-payment-history");

        return warnings;
    }
}
