using FullWorth.Backend.Modules.Budgets.CarryOver;

namespace FullWorth.Backend.Tests.Budgets.CarryOver;

public sealed class BudgetCarryOverCalculatorTests
{
    [Fact]
    public void Disabled_AlwaysCarriesNothing()
    {
        var priors = new[] { 40m, 250m, 0m };
        Assert.Equal(0m, BudgetCarryOverCalculator.CarriedIn(CarryOverMode.Disabled, 200m, priors));
        Assert.Equal(200m, BudgetCarryOverCalculator.EffectiveBudget(CarryOverMode.Disabled, 200m, priors));
    }

    [Fact]
    public void Enabled_WithNoHistory_CarriesNothing()
    {
        Assert.Equal(0m, BudgetCarryOverCalculator.CarriedIn(CarryOverMode.Enabled, 200m, Array.Empty<decimal>()));
        Assert.Equal(200m, BudgetCarryOverCalculator.EffectiveBudget(CarryOverMode.Enabled, 200m, Array.Empty<decimal>()));
    }

    [Fact]
    public void Enabled_UnderspendRollsForward()
    {
        // One prior cycle spent 150 of 200 → 50 leftover carried in.
        Assert.Equal(50m, BudgetCarryOverCalculator.CarriedIn(CarryOverMode.Enabled, 200m, new[] { 150m }));
        Assert.Equal(250m, BudgetCarryOverCalculator.EffectiveBudget(CarryOverMode.Enabled, 200m, new[] { 150m }));
    }

    [Fact]
    public void Enabled_OverspendRollsForwardAsNegative()
    {
        // One prior cycle spent 260 of 200 → 60 over, carried in as a deficit.
        Assert.Equal(-60m, BudgetCarryOverCalculator.CarriedIn(CarryOverMode.Enabled, 200m, new[] { 260m }));
        Assert.Equal(140m, BudgetCarryOverCalculator.EffectiveBudget(CarryOverMode.Enabled, 200m, new[] { 260m }));
    }

    [Fact]
    public void Enabled_CompoundsAcrossMultipleCycles()
    {
        // base 200:
        //  c1: eff 200, spent 150 → carry 50
        //  c2: eff 250, spent 100 → carry 150
        //  c3: eff 350, spent 400 → carry -50
        var priors = new[] { 150m, 100m, 400m };
        Assert.Equal(-50m, BudgetCarryOverCalculator.CarriedIn(CarryOverMode.Enabled, 200m, priors));
        Assert.Equal(150m, BudgetCarryOverCalculator.EffectiveBudget(CarryOverMode.Enabled, 200m, priors));
    }

    [Fact]
    public void Enabled_ExactSpendLeavesNoCarry()
    {
        Assert.Equal(0m, BudgetCarryOverCalculator.CarriedIn(CarryOverMode.Enabled, 200m, new[] { 200m, 200m }));
    }

    [Fact]
    public void NullHistory_Throws() =>
        Assert.Throws<ArgumentNullException>(() => BudgetCarryOverCalculator.CarriedIn(CarryOverMode.Enabled, 200m, null!));
}
