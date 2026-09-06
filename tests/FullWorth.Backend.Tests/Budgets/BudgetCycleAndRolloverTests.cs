using FullWorth.Backend.Modules.Budgets.CarryOver;
using FullWorth.Backend.Modules.Budgets.Cycles;

namespace FullWorth.Backend.Tests.Budgets;

public sealed class BudgetCycleAndRolloverTests
{
    [Fact]
    public void CalendarPeriodsResolveToExpectedWindows()
    {
        var reference = new DateOnly(2026, 9, 6);

        Assert.Equal(
            new BudgetCyclePeriod(reference, reference),
            BudgetCycleCalculator.CurrentPeriod(BudgetCycleDefinition.CalendarDay(), reference));

        Assert.Equal(
            new BudgetCyclePeriod(new DateOnly(2026, 7, 1), new DateOnly(2026, 9, 30)),
            BudgetCycleCalculator.CurrentPeriod(BudgetCycleDefinition.CalendarQuarter(), reference));

        Assert.Equal(
            new BudgetCyclePeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31)),
            BudgetCycleCalculator.CurrentPeriod(BudgetCycleDefinition.CalendarYear(), reference));
    }

    [Theory]
    [InlineData("daily", 1)]
    [InlineData("weekly", 7)]
    [InlineData("biweekly", 14)]
    public void ResolverSupportsShortBudgetCycles(string period, int days)
    {
        var reference = new DateOnly(2026, 9, 6);
        var cycle = BudgetCycleResolver.Resolve(period, null, null);
        var window = BudgetCycleCalculator.CurrentPeriod(cycle, reference);

        Assert.Equal(days, window.LengthInDays);
        Assert.True(window.Contains(reference));
    }

    [Fact]
    public void PositiveOnlyCarriesSavingsButResetsOverspend()
    {
        var carry = BudgetCarryOverCalculator.CarriedIn(
            CarryOverMode.PositiveOnly,
            100m,
            [80m, 140m]);

        Assert.Equal(0m, carry);
        Assert.Equal(100m, BudgetCarryOverCalculator.EffectiveBudget(
            CarryOverMode.PositiveOnly,
            100m,
            [80m, 140m]));
    }

    [Fact]
    public void FullCarryOverDeductsOverspendFromNextCycle()
    {
        var carry = BudgetCarryOverCalculator.CarriedIn(
            CarryOverMode.Enabled,
            100m,
            [80m, 140m]);

        Assert.Equal(-20m, carry);
        Assert.Equal(80m, BudgetCarryOverCalculator.EffectiveBudget(
            CarryOverMode.Enabled,
            100m,
            [80m, 140m]));
    }

    [Fact]
    public void PositiveOnlyCanGrowAcrossCycles()
    {
        var carry = BudgetCarryOverCalculator.CarriedIn(
            CarryOverMode.PositiveOnly,
            100m,
            [80m, 90m]);

        Assert.Equal(30m, carry);
    }
}
