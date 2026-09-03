using FullWorth.Backend.Modules.Budgets.Cycles;

namespace FullWorth.Backend.Tests.Budgets.Cycles;

public sealed class BudgetCycleCalculatorTests
{
    // ---- Calendar month -------------------------------------------------

    [Fact]
    public void CalendarMonth_SpansWholeMonth()
    {
        var def = BudgetCycleDefinition.CalendarMonth();
        var period = BudgetCycleCalculator.CurrentPeriod(def, new DateOnly(2026, 8, 25));

        Assert.Equal(new DateOnly(2026, 8, 1), period.Start);
        Assert.Equal(new DateOnly(2026, 8, 31), period.End);
        Assert.Equal(31, period.LengthInDays);
    }

    [Theory]
    [InlineData(2024, 2, 29)] // leap year
    [InlineData(2026, 2, 28)] // non-leap year
    public void CalendarMonth_HandlesVaryingMonthLengths(int year, int month, int expectedLastDay)
    {
        var def = BudgetCycleDefinition.CalendarMonth();
        var period = BudgetCycleCalculator.CurrentPeriod(def, new DateOnly(year, month, 10));

        Assert.Equal(new DateOnly(year, month, 1), period.Start);
        Assert.Equal(new DateOnly(year, month, expectedLastDay), period.End);
    }

    [Fact]
    public void CalendarMonth_NextAndPreviousAreAdjacentMonths()
    {
        var def = BudgetCycleDefinition.CalendarMonth();
        var reference = new DateOnly(2026, 8, 25);

        var next = BudgetCycleCalculator.NextPeriod(def, reference);
        var previous = BudgetCycleCalculator.PreviousPeriod(def, reference);

        Assert.Equal(new DateOnly(2026, 9, 1), next.Start);
        Assert.Equal(new DateOnly(2026, 9, 30), next.End);
        Assert.Equal(new DateOnly(2026, 7, 1), previous.Start);
        Assert.Equal(new DateOnly(2026, 7, 31), previous.End);
    }

    // ---- Pay cycle ------------------------------------------------------

    [Fact]
    public void PayCycle_StartsOnAnchorDay_WhenReferenceIsOnOrAfterAnchor()
    {
        var def = BudgetCycleDefinition.PayCycle(25);

        var onAnchor = BudgetCycleCalculator.CurrentPeriod(def, new DateOnly(2026, 8, 25));
        Assert.Equal(new DateOnly(2026, 8, 25), onAnchor.Start);
        Assert.Equal(new DateOnly(2026, 9, 24), onAnchor.End);

        var afterAnchor = BudgetCycleCalculator.CurrentPeriod(def, new DateOnly(2026, 8, 26));
        Assert.Equal(new DateOnly(2026, 8, 25), afterAnchor.Start);
    }

    [Fact]
    public void PayCycle_RollsBack_WhenReferenceIsBeforeAnchor()
    {
        var def = BudgetCycleDefinition.PayCycle(25);
        var period = BudgetCycleCalculator.CurrentPeriod(def, new DateOnly(2026, 8, 24));

        Assert.Equal(new DateOnly(2026, 7, 25), period.Start);
        Assert.Equal(new DateOnly(2026, 8, 24), period.End);
    }

    [Fact]
    public void PayCycle_ClampsAnchorToShortMonth()
    {
        var def = BudgetCycleDefinition.PayCycle(31);

        // Mid-February: this month's clamped anchor is Feb 28 (2026 is non-leap); reference is before
        // it, so the window rolls back to the previous anchor (Jan 31) and ends the day before Feb 28.
        var period = BudgetCycleCalculator.CurrentPeriod(def, new DateOnly(2026, 2, 15));
        Assert.Equal(new DateOnly(2026, 1, 31), period.Start);
        Assert.Equal(new DateOnly(2026, 2, 27), period.End);

        // On the clamped anchor itself the window starts there and runs to the day before Mar 31.
        var onClampedAnchor = BudgetCycleCalculator.CurrentPeriod(def, new DateOnly(2026, 2, 28));
        Assert.Equal(new DateOnly(2026, 2, 28), onClampedAnchor.Start);
        Assert.Equal(new DateOnly(2026, 3, 30), onClampedAnchor.End);
    }

    // ---- Custom fixed-length -------------------------------------------

    [Theory]
    [InlineData(2026, 1, 1, 2026, 1, 1, 2026, 1, 14)]   // on the anchor
    [InlineData(2026, 1, 14, 2026, 1, 1, 2026, 1, 14)]  // last day of first window
    [InlineData(2026, 1, 15, 2026, 1, 15, 2026, 1, 28)] // start of second window
    [InlineData(2025, 12, 31, 2025, 12, 18, 2025, 12, 31)] // one day before anchor → previous window
    [InlineData(2025, 12, 18, 2025, 12, 18, 2025, 12, 31)] // exact previous-window boundary
    [InlineData(2025, 12, 17, 2025, 12, 4, 2025, 12, 17)]  // two windows before the anchor
    public void Custom_TilesWindowsAroundAnchor(
        int refY, int refM, int refD,
        int startY, int startM, int startD,
        int endY, int endM, int endD)
    {
        var def = BudgetCycleDefinition.Custom(new DateOnly(2026, 1, 1), lengthDays: 14);
        var period = BudgetCycleCalculator.CurrentPeriod(def, new DateOnly(refY, refM, refD));

        Assert.Equal(new DateOnly(startY, startM, startD), period.Start);
        Assert.Equal(new DateOnly(endY, endM, endD), period.End);
        Assert.Equal(14, period.LengthInDays);
    }

    // ---- Cross-cutting invariants --------------------------------------

    public static readonly TheoryData<BudgetCycleDefinition> AllDefinitions = new()
    {
        BudgetCycleDefinition.CalendarMonth(),
        BudgetCycleDefinition.PayCycle(25),
        BudgetCycleDefinition.PayCycle(31),
        BudgetCycleDefinition.Custom(new DateOnly(2026, 1, 1), 14),
        BudgetCycleDefinition.Custom(new DateOnly(2026, 1, 1), 7),
    };

    [Theory]
    [MemberData(nameof(AllDefinitions))]
    public void Periods_AreContiguousAndContainTheirReference(BudgetCycleDefinition def)
    {
        var reference = new DateOnly(2026, 8, 25);
        var current = BudgetCycleCalculator.CurrentPeriod(def, reference);
        var next = BudgetCycleCalculator.NextPeriod(def, reference);
        var previous = BudgetCycleCalculator.PreviousPeriod(def, reference);

        Assert.True(current.Contains(reference));
        Assert.True(current.LengthInDays >= 1);
        // No gaps and no overlaps between adjacent windows.
        Assert.Equal(current.EndExclusive, next.Start);
        Assert.Equal(current.Start, previous.EndExclusive);
    }

    // ---- Validation -----------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(32)]
    public void PayCycle_RejectsOutOfRangeAnchorDay(int anchorDay) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => BudgetCycleDefinition.PayCycle(anchorDay));

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void Custom_RejectsNonPositiveLength(int lengthDays) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => BudgetCycleDefinition.Custom(new DateOnly(2026, 1, 1), lengthDays));
}
