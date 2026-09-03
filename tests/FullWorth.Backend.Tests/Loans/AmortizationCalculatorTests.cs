using FullWorth.Backend.Modules.Loans.Amortization;

namespace FullWorth.Backend.Tests.Loans;

public sealed class AmortizationCalculatorTests
{
    [Fact]
    public void CalculateCreatesFixedPaymentScheduleAndAdjustsTheFinalPayment()
    {
        var schedule = AmortizationCalculator.Calculate(new(
            CurrentBalance: 1_000m,
            NominalInterestRate: 0m,
            PaymentAmount: 300m,
            PaymentFrequency: "monthly",
            FirstPaymentDate: new(2026, 2, 1)));

        Assert.Equal(4, schedule.Periods.Count);
        Assert.Equal(new AmortizationPeriod(1, new(2026, 2, 1), 300m, 0m, 300m, 700m), schedule.Periods[0]);
        Assert.Equal(new AmortizationPeriod(4, new(2026, 5, 1), 100m, 0m, 100m, 0m), schedule.Periods[^1]);
        Assert.Equal(new DateOnly(2026, 5, 1), schedule.EstimatedPayoffDate);
        Assert.Equal(0m, schedule.TotalExpectedInterest);
    }

    [Fact]
    public void CalculateUsesKnownNominalRateForInterestAndPrincipalSplit()
    {
        var schedule = AmortizationCalculator.Calculate(new(
            CurrentBalance: 1_000m,
            NominalInterestRate: 12m,
            PaymentAmount: 340m,
            PaymentFrequency: "monthly",
            FirstPaymentDate: new(2026, 2, 1)));

        Assert.Equal(new AmortizationPeriod(1, new(2026, 2, 1), 340m, 10m, 330m, 670m), schedule.Periods[0]);
        Assert.Equal(new AmortizationPeriod(2, new(2026, 3, 1), 340m, 6.70m, 333.30m, 336.70m), schedule.Periods[1]);
        Assert.Equal(new AmortizationPeriod(3, new(2026, 4, 1), 340m, 3.37m, 336.63m, 0.07m), schedule.Periods[2]);
        Assert.Equal(new AmortizationPeriod(4, new(2026, 5, 1), 0.07m, 0m, 0.07m, 0m), schedule.Periods[^1]);
        Assert.Equal(20.07m, schedule.TotalExpectedInterest);
    }

    [Fact]
    public void CalculateRoundsAwayFromZeroAndNeverLeavesAResidualBalance()
    {
        var schedule = AmortizationCalculator.Calculate(new(
            CurrentBalance: 100m,
            NominalInterestRate: 10m,
            PaymentAmount: 50m,
            PaymentFrequency: "monthly",
            FirstPaymentDate: new(2026, 2, 1)));

        Assert.Equal(new AmortizationPeriod(1, new(2026, 2, 1), 50m, 0.83m, 49.17m, 50.83m), schedule.Periods[0]);
        Assert.Equal(new AmortizationPeriod(3, new(2026, 4, 1), 1.26m, 0.01m, 1.25m, 0m), schedule.Periods[^1]);
        Assert.Equal(1.26m, schedule.TotalExpectedInterest);
    }
}
