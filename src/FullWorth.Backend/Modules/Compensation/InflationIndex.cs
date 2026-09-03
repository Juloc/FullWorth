namespace FullWorth.Backend.Modules.Compensation;

public static class InflationIndex
{
    public const string Source = "Destatis Verbraucherpreisindex Deutschland, 2020=100";
    public const string DataAsOf = "2026-08-12";

    // Completed years use the official annual average. 2026 uses final monthly CPI values through July.
    // The provisional August 2026 value is intentionally excluded from purchasing-power calculations.
    private static readonly IReadOnlyDictionary<int, decimal> AnnualAverage = new Dictionary<int, decimal>
    {
        [2020] = 100.0m,
        [2021] = 103.1m,
        [2022] = 110.2m,
        [2023] = 116.7m,
        [2024] = 119.3m,
        [2025] = 121.9m
    };

    private static readonly IReadOnlyList<InflationPoint> Monthly2026 = new[]
    {
        new InflationPoint(new DateOnly(2026, 1, 31), 122.8m, true),
        new InflationPoint(new DateOnly(2026, 2, 28), 123.1m, true),
        new InflationPoint(new DateOnly(2026, 3, 31), 124.5m, true),
        new InflationPoint(new DateOnly(2026, 4, 30), 125.2m, true),
        new InflationPoint(new DateOnly(2026, 5, 31), 125.0m, true),
        new InflationPoint(new DateOnly(2026, 6, 30), 124.6m, true),
        new InflationPoint(new DateOnly(2026, 7, 31), 125.6m, true)
    };

    public static InflationMetadata Metadata()
    {
        var annual = AnnualAverage.Select(pair =>
            new InflationPoint(new DateOnly(pair.Key, 12, 31), pair.Value, true));
        return new InflationMetadata(Source, "2020=100", DataAsOf, annual.Concat(Monthly2026).OrderBy(p => p.Date).ToArray());
    }

    public static decimal GetIndex(DateOnly date)
    {
        if (date.Year <= 2020) return AnnualAverage[2020];
        if (date.Year <= 2025 && AnnualAverage.TryGetValue(date.Year, out var annual)) return annual;
        if (date.Year == 2026)
        {
            var point = Monthly2026.LastOrDefault(p => p.Date <= date) ?? Monthly2026[0];
            return point.Index;
        }
        return Monthly2026[^1].Index;
    }

    public static decimal AdjustForPurchasingPower(decimal amount, DateOnly from, DateOnly to)
    {
        if (amount < 0m) throw new ArgumentOutOfRangeException(nameof(amount));
        var fromIndex = GetIndex(from);
        var toIndex = GetIndex(to);
        return RoundMoney(fromIndex <= 0m ? amount : amount * toIndex / fromIndex);
    }

    public static SalaryNegotiationResult Analyze(SalaryNegotiationRequest request)
    {
        if (request.PreviousAnnualGross <= 0m) throw new ArgumentOutOfRangeException(nameof(request.PreviousAnnualGross));
        if (request.CurrentAnnualGross < 0m || request.DesiredAnnualGross < 0m) throw new ArgumentOutOfRangeException(nameof(request.CurrentAnnualGross));

        var comparisonDate = request.ComparisonDate ?? Monthly2026[^1].Date;
        if (comparisonDate < request.PreviousDate) throw new ArgumentException("Comparison date cannot precede previous salary date.");

        var oldIndex = GetIndex(request.PreviousDate);
        var currentIndex = GetIndex(comparisonDate);
        var purchasingPowerSalary = AdjustForPurchasingPower(request.PreviousAnnualGross, request.PreviousDate, comparisonDate);
        var cumulativeInflation = oldIndex <= 0m ? 0m : (currentIndex / oldIndex - 1m) * 100m;
        var currentNominal = ChangePercent(request.PreviousAnnualGross, request.CurrentAnnualGross);
        var desiredNominal = ChangePercent(request.PreviousAnnualGross, request.DesiredAnnualGross);
        var currentReal = purchasingPowerSalary <= 0m ? 0m : (request.CurrentAnnualGross / purchasingPowerSalary - 1m) * 100m;
        var desiredReal = purchasingPowerSalary <= 0m ? 0m : (request.DesiredAnnualGross / purchasingPowerSalary - 1m) * 100m;
        var suggested = purchasingPowerSalary * (1m + Math.Clamp(request.AdditionalRealAdjustmentPercent, -50m, 100m) / 100m);

        return new SalaryNegotiationResult(
            RoundMoney(request.PreviousAnnualGross),
            request.PreviousDate,
            RoundMoney(request.CurrentAnnualGross),
            RoundMoney(request.DesiredAnnualGross),
            comparisonDate,
            oldIndex,
            currentIndex,
            Math.Round(cumulativeInflation, 2),
            RoundMoney(purchasingPowerSalary),
            Math.Round(currentNominal, 2),
            Math.Round(currentReal, 2),
            Math.Round(desiredNominal, 2),
            Math.Round(desiredReal, 2),
            RoundMoney(request.DesiredAnnualGross - purchasingPowerSalary),
            RoundMoney(suggested),
            Source,
            DataAsOf);
    }

    private static decimal ChangePercent(decimal from, decimal to) => from <= 0m ? 0m : (to / from - 1m) * 100m;
    private static decimal RoundMoney(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
