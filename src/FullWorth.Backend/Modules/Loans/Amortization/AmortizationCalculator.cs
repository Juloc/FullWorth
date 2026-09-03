namespace FullWorth.Backend.Modules.Loans.Amortization;

public sealed record AmortizationInput(
    decimal CurrentBalance,
    decimal NominalInterestRate,
    decimal PaymentAmount,
    string PaymentFrequency,
    DateOnly FirstPaymentDate);

public sealed record AmortizationPeriod(
    int Number,
    DateOnly PaymentDate,
    decimal PaymentAmount,
    decimal InterestAmount,
    decimal PrincipalAmount,
    decimal RemainingBalance);

public sealed record AmortizationSchedule(
    IReadOnlyList<AmortizationPeriod> Periods,
    DateOnly EstimatedPayoffDate,
    decimal TotalExpectedInterest);

public static class AmortizationCalculator
{
    public static AmortizationSchedule Calculate(AmortizationInput input)
    {
        if (input.CurrentBalance <= 0m) throw new ArgumentOutOfRangeException(nameof(input.CurrentBalance));
        if (input.NominalInterestRate < 0m) throw new ArgumentOutOfRangeException(nameof(input.NominalInterestRate));
        if (input.PaymentAmount <= 0m) throw new ArgumentOutOfRangeException(nameof(input.PaymentAmount));

        var frequency = PaymentFrequency.Parse(input.PaymentFrequency);
        var periodicRate = input.NominalInterestRate / 100m / frequency.PeriodsPerYear;
        var payment = Round(input.PaymentAmount);
        var balance = Round(input.CurrentBalance);
        var paymentDate = input.FirstPaymentDate;
        var totalInterest = 0m;
        var periods = new List<AmortizationPeriod>();

        for (var number = 1; balance > 0m; number++)
        {
            var interest = Round(balance * periodicRate);
            if (payment <= interest) throw new ArgumentException("Payment amount does not cover the periodic interest.", nameof(input.PaymentAmount));

            var scheduledPrincipal = Round(payment - interest);
            var isFinalPayment = scheduledPrincipal >= balance;
            var actualPayment = isFinalPayment ? Round(balance + interest) : payment;
            var principal = isFinalPayment ? balance : scheduledPrincipal;
            balance = isFinalPayment ? 0m : Round(balance - principal);

            periods.Add(new(number, paymentDate, actualPayment, interest, principal, balance));
            totalInterest = Round(totalInterest + interest);
            paymentDate = frequency.AddPeriod(paymentDate);

            if (number == 10_000) throw new InvalidOperationException("Loan payoff exceeds the maximum supported schedule length.");
        }

        return new(periods, periods[^1].PaymentDate, totalInterest);
    }

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    private sealed record PaymentFrequency(int PeriodsPerYear, Func<DateOnly, DateOnly> AddPeriod)
    {
        public static PaymentFrequency Parse(string frequency) => frequency.Trim().ToLowerInvariant() switch
        {
            "weekly" => new(52, date => date.AddDays(7)),
            "biweekly" or "fortnightly" => new(26, date => date.AddDays(14)),
            "monthly" => new(12, date => date.AddMonths(1)),
            "quarterly" => new(4, date => date.AddMonths(3)),
            "annually" or "yearly" => new(1, date => date.AddYears(1)),
            _ => throw new ArgumentException("Payment frequency is not supported.", nameof(frequency))
        };
    }
}
