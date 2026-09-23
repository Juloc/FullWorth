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

    /// <summary>Ab wann der Uebertrag gerechnet wird (#115). NULL heisst so weit zurueck wie moeglich.</summary>
    public string? CarryOverStart { get; init; }

    /// <summary>Das selbst gewaehlte Startdatum, wenn <see cref="CarryOverStart"/> "from-date" ist.</summary>
    public DateOnly? CarryOverFrom { get; init; }
}

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

    /// <summary>Ab wann der Uebertrag gerechnet wird (#115). Weggelassen heisst so weit zurueck wie moeglich.</summary>
    public string? CarryOverStart { get; init; }

    public DateOnly? CarryOverFrom { get; init; }
}
