using FullWorth.Backend.Modules.Parity;

namespace FullWorth.Backend.Tests.Portfolio;

public sealed class InvestmentPerformanceMathTests
{
    [Fact]
    public void TwrIgnoresExternalDepositAtPeriodBoundary()
    {
        var result = InvestmentPerformanceMath.TimeWeightedReturn(
        [
            new TwrSubperiod(100m, 0m, 110m),
            new TwrSubperiod(110m, 100m, 231m)
        ]);

        Assert.NotNull(result);
        Assert.Equal(0.21m, decimal.Round(result.Value, 8));
    }

    [Fact]
    public void TwrReturnsUnavailableWithoutPositiveCapitalBase()
    {
        var result = InvestmentPerformanceMath.TimeWeightedReturn(
        [
            new TwrSubperiod(0m, 0m, 10m)
        ]);

        Assert.Null(result);
    }

    [Fact]
    public void XirrMatchesSimpleAnnualReturn()
    {
        var result = InvestmentPerformanceMath.Xirr(
        [
            new DatedCashFlow(new DateOnly(2025, 1, 1), -1000m),
            new DatedCashFlow(new DateOnly(2026, 1, 1), 1100m)
        ]);

        Assert.NotNull(result);
        Assert.InRange(result.Value, 0.0999m, 0.1001m);
    }

    [Fact]
    public void XirrHandlesIrregularDates()
    {
        var result = InvestmentPerformanceMath.Xirr(
        [
            new DatedCashFlow(new DateOnly(2025, 1, 1), -1000m),
            new DatedCashFlow(new DateOnly(2025, 7, 1), -500m),
            new DatedCashFlow(new DateOnly(2026, 1, 1), 1700m)
        ]);

        Assert.NotNull(result);
        Assert.InRange(result.Value, 0.14m, 0.17m);
    }

    [Fact]
    public void XirrReturnsUnavailableForKnownMultipleRootPattern()
    {
        // -100 + 230/(1+r) - 132/(1+r)^2 has two positive roots (10% and 20%).
        var result = InvestmentPerformanceMath.Xirr(
        [
            new DatedCashFlow(new DateOnly(2024, 1, 1), -100m),
            new DatedCashFlow(new DateOnly(2025, 1, 1), 230m),
            new DatedCashFlow(new DateOnly(2026, 1, 1), -132m)
        ]);

        Assert.Null(result);
    }

    [Fact]
    public void XirrRequiresPositiveAndNegativeCashFlows()
    {
        Assert.Null(InvestmentPerformanceMath.Xirr(
        [
            new DatedCashFlow(new DateOnly(2025, 1, 1), 100m),
            new DatedCashFlow(new DateOnly(2026, 1, 1), 110m)
        ]));
    }
}
