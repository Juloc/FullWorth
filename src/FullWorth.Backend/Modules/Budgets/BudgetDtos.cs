using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Budgets;

public sealed record BudgetView(
    Guid Id,
    Guid FullWorthSpaceId,
    string Name,
    Guid? CategoryId,
    decimal Amount,
    string Currency,
    string Period,
    bool CarryOver,
    bool IsActive,
    DateOnly? StartDate,
    DateOnly? EndDate,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public bool CarryOverOverspend { get; init; }
}

/// <summary>Budget-vs-actual for the budget's current cycle window, plus a cycle-end forecast (§12)
/// and the transactions that make up the spend (for the detail view).</summary>
public sealed record BudgetPeriodStatus(
    Guid BudgetId,
    string Name,
    Guid? CategoryId,
    string Currency,
    string Period,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    decimal BudgetAmount,
    decimal Spent,
    decimal Remaining,
    decimal PercentUsed,
    decimal ProjectedEndSpend,
    decimal ProjectedOverUnder,
    string Trend,
    bool PartialAccess,
    IReadOnlyList<BudgetContributionRow> Contributing)
{
    public decimal BaseBudgetAmount { get; init; }
    public decimal CarryIn { get; init; }
    public bool CarryOver { get; init; }
    public bool CarryOverOverspend { get; init; }
}

/// <summary>A single expense that counts toward a budget's cycle spend, for the detail list.</summary>
public sealed record BudgetContributionRow(Guid Id, DateOnly? BookingDate, string? Counterparty, decimal Amount, string Currency, string? Category);

/// <summary>Space-level budget usage signal for the post-sync threshold notifications.</summary>
public sealed record BudgetSignal(Guid BudgetId, string Name, decimal PercentUsed, DateOnly PeriodStart);

public enum BudgetAccessLevel
{
    None,
    Read,
    Write
}

public enum BudgetMutationResult
{
    Success,
    NotFound,
    Forbidden,
    Invalid
}

public sealed record BudgetMutationOutcome(BudgetMutationResult Result, BudgetView? Budget = null, string? Error = null);

public sealed record BudgetWrite(string Name, Guid? CategoryId, decimal Amount, string Currency, string Period, bool CarryOver, bool IsActive, DateOnly? StartDate, DateOnly? EndDate)
{
    // Nullable preserves compatibility with older clients: omitted means the old full carry-over behavior.
    public bool? CarryOverOverspend { get; init; }
}
