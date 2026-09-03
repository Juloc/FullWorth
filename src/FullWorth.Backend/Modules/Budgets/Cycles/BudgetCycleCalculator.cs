namespace FullWorth.Backend.Modules.Budgets.Cycles;

/// <summary>
/// The kind of recurring window a budget is measured over.
/// </summary>
public enum BudgetCycleType
{
    /// <summary>A calendar month: 1st … last day of the month.</summary>
    CalendarMonth,

    /// <summary>A monthly salary/pay cycle anchored on a day-of-month (e.g. paid on the 25th).</summary>
    PayCycle,

    /// <summary>A fixed-length window of N days anchored at a start date (e.g. weekly, bi-weekly).</summary>
    Custom,
}

/// <summary>
/// A single budget window with an inclusive start and inclusive end. All boundaries are
/// <see cref="DateOnly"/> so the calculation is timezone- and DST-agnostic by construction:
/// a budget "day" is a calendar day, not an instant, so clock changes never split or double a day.
/// </summary>
public readonly record struct BudgetCyclePeriod(DateOnly Start, DateOnly End)
{
    /// <summary>The exclusive end (day after <see cref="End"/>); convenient for range queries.</summary>
    public DateOnly EndExclusive => End.AddDays(1);

    /// <summary>Number of calendar days in the window (always ≥ 1).</summary>
    public int LengthInDays => EndExclusive.DayNumber - Start.DayNumber;

    /// <summary>True when <paramref name="date"/> falls within [Start, End] inclusive.</summary>
    public bool Contains(DateOnly date) => date >= Start && date <= End;
}

/// <summary>
/// Describes how a budget's recurring window is derived. Construct via the factory methods so
/// invariants (valid anchor day, positive custom length) are enforced up front.
/// </summary>
public readonly record struct BudgetCycleDefinition
{
    private BudgetCycleDefinition(BudgetCycleType type, int anchorDay, DateOnly anchorStart, int customLengthDays)
    {
        Type = type;
        AnchorDay = anchorDay;
        AnchorStart = anchorStart;
        CustomLengthDays = customLengthDays;
    }

    public BudgetCycleType Type { get; }

    /// <summary>Day-of-month (1–31) the pay cycle rolls over on. Only used for <see cref="BudgetCycleType.PayCycle"/>.</summary>
    public int AnchorDay { get; }

    /// <summary>The reference start date. Only used for <see cref="BudgetCycleType.Custom"/>.</summary>
    public DateOnly AnchorStart { get; }

    /// <summary>Window length in days. Only used for <see cref="BudgetCycleType.Custom"/>.</summary>
    public int CustomLengthDays { get; }

    /// <summary>A window that always spans one whole calendar month.</summary>
    public static BudgetCycleDefinition CalendarMonth() =>
        new(BudgetCycleType.CalendarMonth, 1, default, 0);

    /// <summary>
    /// A monthly window that rolls over on <paramref name="anchorDay"/> (1–31). If a month is
    /// shorter than the anchor day (e.g. the 31st in February) the anchor is clamped to the last
    /// day of that month, so every month still gets exactly one window.
    /// </summary>
    public static BudgetCycleDefinition PayCycle(int anchorDay)
    {
        if (anchorDay is < 1 or > 31)
            throw new ArgumentOutOfRangeException(nameof(anchorDay), anchorDay, "Pay-cycle anchor day must be between 1 and 31.");
        return new(BudgetCycleType.PayCycle, anchorDay, default, 0);
    }

    /// <summary>
    /// A fixed-length window of <paramref name="lengthDays"/> days, aligned to
    /// <paramref name="anchorStart"/>. Windows tile forwards and backwards from the anchor, so any
    /// reference date maps deterministically to exactly one window.
    /// </summary>
    public static BudgetCycleDefinition Custom(DateOnly anchorStart, int lengthDays)
    {
        if (lengthDays < 1)
            throw new ArgumentOutOfRangeException(nameof(lengthDays), lengthDays, "Custom cycle length must be at least 1 day.");
        return new(BudgetCycleType.Custom, 1, anchorStart, lengthDays);
    }
}

/// <summary>
/// Resolves budget windows for a given reference date. Pure and deterministic: no ambient time,
/// no I/O — callers pass the reference date in, which keeps the math trivially testable and keeps
/// forecasts/carry-over reproducible.
/// </summary>
public static class BudgetCycleCalculator
{
    /// <summary>The window that contains <paramref name="reference"/>.</summary>
    public static BudgetCyclePeriod CurrentPeriod(BudgetCycleDefinition definition, DateOnly reference) =>
        definition.Type switch
        {
            BudgetCycleType.CalendarMonth => CalendarMonth(reference),
            BudgetCycleType.PayCycle => PayCycle(definition.AnchorDay, reference),
            BudgetCycleType.Custom => Custom(definition.AnchorStart, definition.CustomLengthDays, reference),
            _ => throw new ArgumentOutOfRangeException(nameof(definition), definition.Type, "Unknown budget cycle type."),
        };

    /// <summary>The window immediately after the one containing <paramref name="reference"/>.</summary>
    public static BudgetCyclePeriod NextPeriod(BudgetCycleDefinition definition, DateOnly reference) =>
        CurrentPeriod(definition, CurrentPeriod(definition, reference).EndExclusive);

    /// <summary>The window immediately before the one containing <paramref name="reference"/>.</summary>
    public static BudgetCyclePeriod PreviousPeriod(BudgetCycleDefinition definition, DateOnly reference) =>
        CurrentPeriod(definition, CurrentPeriod(definition, reference).Start.AddDays(-1));

    private static BudgetCyclePeriod CalendarMonth(DateOnly reference)
    {
        var start = new DateOnly(reference.Year, reference.Month, 1);
        return new BudgetCyclePeriod(start, start.AddMonths(1).AddDays(-1));
    }

    private static BudgetCyclePeriod PayCycle(int anchorDay, DateOnly reference)
    {
        var thisMonthAnchor = AnchorInMonth(reference.Year, reference.Month, anchorDay);
        DateOnly start;
        if (reference >= thisMonthAnchor)
        {
            start = thisMonthAnchor;
        }
        else
        {
            var previousMonth = new DateOnly(reference.Year, reference.Month, 1).AddMonths(-1);
            start = AnchorInMonth(previousMonth.Year, previousMonth.Month, anchorDay);
        }

        var nextMonth = new DateOnly(start.Year, start.Month, 1).AddMonths(1);
        var endExclusive = AnchorInMonth(nextMonth.Year, nextMonth.Month, anchorDay);
        return new BudgetCyclePeriod(start, endExclusive.AddDays(-1));
    }

    private static BudgetCyclePeriod Custom(DateOnly anchorStart, int lengthDays, DateOnly reference)
    {
        var offset = reference.DayNumber - anchorStart.DayNumber;
        var index = FloorDiv(offset, lengthDays);
        var start = anchorStart.AddDays(index * lengthDays);
        return new BudgetCyclePeriod(start, start.AddDays(lengthDays - 1));
    }

    /// <summary>The anchor day within a month, clamped to the month's real length.</summary>
    private static DateOnly AnchorInMonth(int year, int month, int anchorDay) =>
        new(year, month, Math.Min(anchorDay, DateTime.DaysInMonth(year, month)));

    /// <summary>Integer division that rounds toward negative infinity (so windows tile correctly before the anchor).</summary>
    private static int FloorDiv(int dividend, int divisor) =>
        dividend >= 0 || dividend % divisor == 0 ? dividend / divisor : dividend / divisor - 1;
}

/// <summary>
/// Maps a budget's stored <c>Period</c> string (plus optional start/end dates) to a
/// <see cref="BudgetCycleDefinition"/>. Shared by every consumer that needs a budget's current cycle
/// window (single-budget status, space-wide budget status, forecast, carry-over) so "what counts as
/// this budget's period" is defined in exactly one place.
/// </summary>
public static class BudgetCycleResolver
{
    /// <summary>Unknown/monthly falls back to a calendar month; "custom" needs both dates, otherwise also falls back.</summary>
    public static BudgetCycleDefinition Resolve(string period, DateOnly? startDate, DateOnly? endDate)
    {
        // A known Monday, used only when a weekly/bi-weekly budget has no explicit start anchor.
        var weekAnchor = startDate ?? new DateOnly(2024, 1, 1);
        return period switch
        {
            "weekly" => BudgetCycleDefinition.Custom(weekAnchor, 7),
            "biweekly" or "fortnightly" => BudgetCycleDefinition.Custom(weekAnchor, 14),
            "paycycle" or "pay-cycle" or "salary" => BudgetCycleDefinition.PayCycle(startDate?.Day ?? 1),
            "custom" when startDate is { } start && endDate is { } end && end >= start
                => BudgetCycleDefinition.Custom(start, end.DayNumber - start.DayNumber + 1),
            _ => BudgetCycleDefinition.CalendarMonth(),
        };
    }
}
