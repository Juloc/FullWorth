using FullWorth.Backend.Modules.Contracts;
using Xunit;

namespace FullWorth.Backend.Tests.Contracts;

// Pure cadence math shared by contract detection and the detail view (UI_UX_SPEC §13 / §30).
public sealed class ContractCycleTests
{
    [Theory]
    [InlineData("monthly", 1, 12)]
    [InlineData("monthly", 3, 4)]
    [InlineData("quarterly", 1, 4)]
    [InlineData("yearly", 1, 1)]
    [InlineData("weekly", 1, 52)]
    [InlineData("daily", 1, 365)]
    public void PeriodsPerYearMatchesCadence(string cycle, int interval, int expected)
        => Assert.Equal(expected, ContractCycle.PeriodsPerYear(cycle, interval));

    [Fact]
    public void NextAdvancesByCadence()
    {
        var d = new DateOnly(2026, 1, 31);
        Assert.Equal(new DateOnly(2026, 2, 28), ContractCycle.Next(d, "monthly", 1)); // clamps to short month
        Assert.Equal(new DateOnly(2026, 2, 7), ContractCycle.Next(new DateOnly(2026, 1, 31), "weekly", 1));
        Assert.Equal(new DateOnly(2027, 1, 31), ContractCycle.Next(d, "yearly", 1));
        Assert.Equal(new DateOnly(2026, 4, 30), ContractCycle.Next(new DateOnly(2026, 1, 30), "quarterly", 1));
        Assert.Equal(new DateOnly(2026, 2, 1), ContractCycle.Next(new DateOnly(2026, 1, 1), "daily", 31));
    }

    [Fact]
    public void NextOnOrAfterReturnsStartWhenAlreadyFuture()
    {
        var start = new DateOnly(2030, 3, 15);
        Assert.Equal(start, ContractCycle.NextOnOrAfter(start, "monthly", 1, new DateOnly(2026, 8, 28)));
    }

    [Fact]
    public void NextOnOrAfterJumpsStaleDailyPastTodayInOneStep()
    {
        // A daily contract ~3 years stale must yield a date on/after today, not a past date.
        var start = new DateOnly(2023, 1, 1);
        var today = new DateOnly(2026, 8, 28);
        var next = ContractCycle.NextOnOrAfter(start, "daily", 1, today);
        Assert.True(next >= today);
        Assert.True(next < today.AddDays(1)); // for daily the first on/after is exactly today
        Assert.Equal(today, next);
    }

    [Fact]
    public void NextOnOrAfterAlignsToCadenceBoundaries()
    {
        // Weekly starting on a Thursday: the first occurrence on/after a later date stays on Thursdays.
        var start = new DateOnly(2026, 1, 1);           // Thursday
        var next = ContractCycle.NextOnOrAfter(start, "weekly", 2, new DateOnly(2026, 8, 28));
        Assert.True(next >= new DateOnly(2026, 8, 28));
        Assert.Equal(0, (next.DayNumber - start.DayNumber) % 14); // whole number of 2-week periods
    }

    [Fact]
    public void NextOnOrAfterStepsMonthlyWithoutOverflow()
    {
        var start = new DateOnly(2000, 1, 15);
        var today = new DateOnly(2026, 8, 28);
        var next = ContractCycle.NextOnOrAfter(start, "monthly", 1, today);
        Assert.Equal(new DateOnly(2026, 9, 15), next);
    }
}
