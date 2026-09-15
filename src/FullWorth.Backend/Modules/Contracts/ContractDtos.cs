namespace FullWorth.Backend.Modules.Contracts;

public sealed record ContractView(
    Guid Id,
    Guid FullWorthSpaceId,
    string Name,
    string? ProviderName,
    string Kind,
    Guid? CategoryId,
    Guid? AccountId,
    decimal Amount,
    string Currency,
    string BillingCycle,
    int Interval,
    DateOnly? StartDate,
    DateOnly? EndDate,
    DateOnly? NextDueDate,
    bool AutoDetected,
    bool IsActive,
    string? Notes,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    decimal MonthlyEquivalent = 0,
    decimal AnnualizedAmount = 0,
    /// <summary>See the entity: whether this contract is subtracted from what is available.</summary>
    bool CountsAsFixedCost = true);

public enum ContractAccessLevel
{
    None,
    Read,
    Write
}

public enum ContractMutationResult
{
    Success,
    NotFound,
    Forbidden,
    Invalid
}

public sealed record ContractMutationOutcome(ContractMutationResult Result, ContractView? Contract = null, string? Error = null);

public sealed record ContractMergeRequest(IReadOnlyList<Guid> SourceContractIds);

public sealed record ContractMergeSourceView(
    Guid Id,
    string Name,
    string? ProviderName,
    Guid? AccountId,
    decimal Amount,
    string Currency,
    string BillingCycle,
    DateOnly? NextDueDate);

// CountsAsFixedCost is nullable on purpose: a client that does not send it must leave the stored value
// alone rather than reset it to the default, which is what a plain bool would do on every save.
public sealed record ContractWrite(string Name, string? ProviderName, string Kind, Guid? CategoryId, Guid? AccountId, decimal Amount, string Currency, string BillingCycle, int Interval, DateOnly? StartDate, DateOnly? EndDate, DateOnly? NextDueDate, bool IsActive, string? Notes, bool? CountsAsFixedCost = null);

public sealed record ContractPayment(Guid Id, DateOnly? Date, decimal Amount, string Currency);
public sealed record ContractActivity(
    Guid ContractId,
    string ValueMode,
    decimal ExpectedAmount,
    string Currency,
    decimal AnnualizedAmount,
    DateOnly? NextExpected,
    DateOnly? LastPayment,
    int MatchedCount,
    decimal? AverageAmount,
    IReadOnlyList<ContractPayment> Payments);
