using FullWorth.Backend.Modules.Budgets.Forecast;

namespace FullWorth.Backend.Tests.Budgets.Forecast;

public sealed class BudgetForecastCalculatorTests
{
    [Fact]
    public void NoSpendAndNoHistory_IsNoData()
    {
        var forecast = BudgetForecastCalculator.Project(new BudgetForecastInput(
            BudgetAmount: 200m, Spent: 0m, TotalDays: 30, ElapsedDays: 0, HistoricalDailyAverage: null));

        Assert.Equal(BudgetTrend.NoData, forecast.Trend);
        Assert.Equal(0m, forecast.ProjectedEndSpend);
        Assert.Equal(200m, forecast.Remaining);
    }

    [Fact]
    public void HalfwayAtSteadyPace_IsOnTrack()
    {
        // 100 spent over 15 of 30 days → pace 6.67/day → projected 200 = budget.
        var forecast = BudgetForecastCalculator.Project(new BudgetForecastInput(
            BudgetAmount: 200m, Spent: 100m, TotalDays: 30, ElapsedDays: 15, HistoricalDailyAverage: null));

        Assert.Equal(BudgetTrend.OnTrack, forecast.Trend);
        Assert.Equal(200m, forecast.ProjectedEndSpend);
        Assert.Equal(0m, forecast.ProjectedOverUnder);
    }

    [Fact]
    public void FastPace_TrendsOver()
    {
        // 150 spent over 15 of 30 days → pace 10/day → projected 300.
        var forecast = BudgetForecastCalculator.Project(new BudgetForecastInput(
            BudgetAmount: 200m, Spent: 150m, TotalDays: 30, ElapsedDays: 15, HistoricalDailyAverage: null));

        Assert.Equal(BudgetTrend.TrendingOver, forecast.Trend);
        Assert.Equal(300m, forecast.ProjectedEndSpend);
        Assert.Equal(100m, forecast.ProjectedOverUnder);
    }

    [Fact]
    public void SlowPace_TrendsUnder()
    {
        // 50 spent over 15 of 30 days → pace 3.33/day → projected 100.
        var forecast = BudgetForecastCalculator.Project(new BudgetForecastInput(
            BudgetAmount: 200m, Spent: 50m, TotalDays: 30, ElapsedDays: 15, HistoricalDailyAverage: null));

        Assert.Equal(BudgetTrend.TrendingUnder, forecast.Trend);
        Assert.Equal(100m, forecast.ProjectedEndSpend);
        Assert.Equal(-100m, forecast.ProjectedOverUnder);
    }

    [Fact]
    public void CycleStart_LeansEntirelyOnHistory()
    {
        // Day 0 with a 5/day historical average → projected 5 * 30 = 150.
        var forecast = BudgetForecastCalculator.Project(new BudgetForecastInput(
            BudgetAmount: 200m, Spent: 0m, TotalDays: 30, ElapsedDays: 0, HistoricalDailyAverage: 5m));

        Assert.Equal(150m, forecast.ProjectedEndSpend);
        Assert.Equal(BudgetTrend.TrendingUnder, forecast.Trend);
    }

    [Fact]
    public void MidCycle_BlendsPaceWithHistory()
    {
        // 10 days of 30 elapsed (fraction 1/3), spent 60 → pace 6/day; history 9/day.
        // effective = 1/3*6 + 2/3*9 = 8/day → projected = 60 + 8*20 = 220.
        var forecast = BudgetForecastCalculator.Project(new BudgetForecastInput(
            BudgetAmount: 200m, Spent: 60m, TotalDays: 30, ElapsedDays: 10, HistoricalDailyAverage: 9m));

        Assert.Equal(220m, forecast.ProjectedEndSpend, 2);
        Assert.Equal(BudgetTrend.TrendingOver, forecast.Trend);
    }

    [Fact]
    public void ElapsedDays_AreClampedToCycleLength()
    {
        // Elapsed beyond the cycle is clamped to TotalDays → no remaining days, projection == spent.
        // 195 of a 200 budget is within the ±5% tolerance, so the finished cycle reads on track.
        var forecast = BudgetForecastCalculator.Project(new BudgetForecastInput(
            BudgetAmount: 200m, Spent: 195m, TotalDays: 30, ElapsedDays: 45, HistoricalDailyAverage: null));

        Assert.Equal(195m, forecast.ProjectedEndSpend);
        Assert.Equal(5m, forecast.Remaining);
        Assert.Equal(BudgetTrend.OnTrack, forecast.Trend);
    }

    [Fact]
    public void ZeroLengthCycle_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => BudgetForecastCalculator.Project(
            new BudgetForecastInput(200m, 0m, TotalDays: 0, ElapsedDays: 0, HistoricalDailyAverage: null)));
}
