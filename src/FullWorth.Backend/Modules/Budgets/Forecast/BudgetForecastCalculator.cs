namespace FullWorth.Backend.Modules.Budgets.Forecast;

/// <summary>A short, stable classification of where a budget is heading by cycle end.</summary>
public enum BudgetTrend
{
    /// <summary>Not enough signal to project (no spend yet and no history).</summary>
    NoData,

    /// <summary>Projected end spend is within tolerance of the budget.</summary>
    OnTrack,

    /// <summary>Projected end spend exceeds the budget beyond tolerance.</summary>
    TrendingOver,

    /// <summary>Projected end spend falls short of the budget beyond tolerance.</summary>
    TrendingUnder,
}

/// <summary>Inputs for a cycle-end forecast. Days are counted in whole calendar days.</summary>
/// <param name="BudgetAmount">The effective budget for the cycle (base + any carry-over).</param>
/// <param name="Spent">Spend accrued so far this cycle (positive).</param>
/// <param name="TotalDays">Length of the cycle in days (must be ≥ 1).</param>
/// <param name="ElapsedDays">Days elapsed in the cycle (clamped to [0, TotalDays]).</param>
/// <param name="HistoricalDailyAverage">Optional average daily spend for this budget/category from prior cycles.</param>
public readonly record struct BudgetForecastInput(
    decimal BudgetAmount,
    decimal Spent,
    int TotalDays,
    int ElapsedDays,
    decimal? HistoricalDailyAverage);

/// <summary>A deterministic, explainable cycle-end forecast.</summary>
public readonly record struct BudgetForecast(
    decimal BudgetAmount,
    decimal Spent,
    decimal Remaining,
    decimal ProjectedEndSpend,
    decimal ProjectedOverUnder,
    BudgetTrend Trend,
    string TrendReason);

/// <summary>
/// Projects cycle-end spend from the current pace, blended with historical daily behavior. The blend
/// is weighted by how far the cycle has progressed: early on the projection leans on history, and it
/// shifts toward the observed pace as the cycle fills in. Pure and deterministic — no ambient clock,
/// no I/O — so the same inputs always yield the same forecast.
/// </summary>
public static class BudgetForecastCalculator
{
    /// <summary>Projected end spend within ±5% of the budget counts as on track.</summary>
    private const decimal OnTrackTolerance = 0.05m;

    public static BudgetForecast Project(BudgetForecastInput input)
    {
        if (input.TotalDays < 1)
            throw new ArgumentOutOfRangeException(nameof(input), input.TotalDays, "Cycle length must be at least one day.");

        var totalDays = input.TotalDays;
        var elapsed = Math.Clamp(input.ElapsedDays, 0, totalDays);
        var remainingDays = totalDays - elapsed;
        var remaining = Round(input.BudgetAmount - input.Spent);

        decimal projectedEnd;
        BudgetTrend trend;
        string reason;

        if (elapsed == 0 && input.HistoricalDailyAverage is null)
        {
            // Nothing observed yet and nothing to lean on.
            projectedEnd = input.Spent;
            trend = BudgetTrend.NoData;
            reason = "no spend yet and no historical average";
        }
        else
        {
            decimal effectiveDailyRate;
            if (elapsed == 0)
            {
                // The cycle just started: lean entirely on history.
                effectiveDailyRate = input.HistoricalDailyAverage!.Value;
            }
            else
            {
                var currentDailyPace = input.Spent / elapsed;
                if (input.HistoricalDailyAverage is decimal historical)
                {
                    var elapsedFraction = (decimal)elapsed / totalDays;
                    effectiveDailyRate = elapsedFraction * currentDailyPace + (1m - elapsedFraction) * historical;
                }
                else
                {
                    effectiveDailyRate = currentDailyPace;
                }
            }

            projectedEnd = input.Spent + effectiveDailyRate * remainingDays;
            var overUnder = projectedEnd - input.BudgetAmount;
            var tolerance = Math.Abs(input.BudgetAmount) * OnTrackTolerance;

            if (overUnder > tolerance)
            {
                trend = BudgetTrend.TrendingOver;
                reason = "current pace exceeds the budget";
            }
            else if (overUnder < -tolerance)
            {
                trend = BudgetTrend.TrendingUnder;
                reason = "current pace stays under the budget";
            }
            else
            {
                trend = BudgetTrend.OnTrack;
                reason = "projected spend is within tolerance of the budget";
            }
        }

        projectedEnd = Round(projectedEnd);
        return new BudgetForecast(
            Round(input.BudgetAmount),
            Round(input.Spent),
            remaining,
            projectedEnd,
            Round(projectedEnd - input.BudgetAmount),
            trend,
            reason);
    }

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
