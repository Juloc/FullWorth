using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Portfolio;

public sealed record AssetView(
    Guid Id,
    Guid FullWorthSpaceId,
    string Name,
    string Kind,
    decimal CurrentValue,
    string Currency,
    DateOnly? ValuedAt,
    DateTimeOffset ValueRecordedAt,
    decimal? AnnualGrowthRate,
    bool IncludeInNetWorth,
    string? Notes,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record LiabilityView(
    Guid Id,
    Guid FullWorthSpaceId,
    string Name,
    string Kind,
    decimal CurrentBalance,
    string Currency,
    decimal? InterestRate,
    decimal? RegularPayment,
    string PaymentCycle,
    DateOnly? NextDueDate,
    DateOnly? EndDate,
    bool IncludeInNetWorth,
    string? Notes,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record NetWorthSnapshotView(
    Guid Id,
    Guid FullWorthSpaceId,
    DateOnly Date,
    string Currency,
    decimal Accounts,
    decimal Assets,
    decimal Liabilities,
    decimal NetWorth,
    DateTimeOffset CreatedAt);

public enum PortfolioMutationResult
{
    Success,
    NotFound,
    Forbidden,
    Invalid
}

public sealed record AssetMutationOutcome(PortfolioMutationResult Result, AssetView? Asset = null, string? Error = null);

public sealed record LiabilityMutationOutcome(PortfolioMutationResult Result, LiabilityView? Liability = null, string? Error = null);

public sealed record AssetWrite(string Name, string Kind, decimal CurrentValue, string Currency, DateOnly? ValuedAt, decimal? AnnualGrowthRate, bool IncludeInNetWorth, string? Notes);

public sealed record LiabilityWrite(string Name, string Kind, decimal CurrentBalance, string Currency, decimal? InterestRate, decimal? RegularPayment, string PaymentCycle, DateOnly? NextDueDate, DateOnly? EndDate, bool IncludeInNetWorth, string? Notes);
