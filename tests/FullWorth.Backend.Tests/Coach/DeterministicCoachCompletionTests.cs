using FullWorth.Backend.Modules.Coach;

namespace FullWorth.Backend.Tests.Coach;

public sealed class DeterministicCoachCompletionTests
{
    [Fact]
    public void ReductionUsesBudgetOverageAndAvoidableReviewSignal()
    {
        var categoryId = Guid.NewGuid();
        var context = Context(
            categories:
            [
                new CoachCategoryFact(categoryId, "Shopping", 250m, 180m, 70m, .5m, .8m, -.5m, 100m, 20m)
                {
                    AvoidableNegativeReviewedAmount = 80m,
                    BudgetOverage = 50m
                },
                new CoachCategoryFact(Guid.NewGuid(), "Dining", 300m, 250m, 50m, .6m, .9m, .8m, 0m, 250m)
            ],
            budgets: [new CoachBudgetFact(Guid.NewGuid(), "Shopping", categoryId, "EUR", 200m, 250m, -50m, 125m, 260m, -60m, false)]);

        var answer = new DeterministicCoachEngine().Answer("Was könnte ich reduzieren?", context);

        Assert.Contains("Shopping", answer.Text);
        Assert.Contains("Budget", answer.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("vermeid", answer.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FinancialIndependenceDoesNotInventWithdrawalRateWithoutTarget()
    {
        var context = Context(currentNetWorth: 75_000m, monthlySavings: 1_000m);
        var answer = new DeterministicCoachEngine().Answer("Wann bin ich finanziell unabhängig?", context);

        Assert.Contains("Ziel", answer.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Entnahme", answer.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("4%", answer.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FinancialIndependenceUsesExplicitTargetDeterministically()
    {
        var context = Context(currentNetWorth: 50_000m, monthlySavings: 1_000m);
        var answer = new DeterministicCoachEngine().Answer("Wann bin ich mit 100000 Euro finanziell unabhängig?", context);

        Assert.Contains("100.000", answer.Text.Replace(',', '.'), StringComparison.OrdinalIgnoreCase);
        // The deterministic projection prints the estimated date as yyyy-MM; assert a concrete
        // calendar year/month is present rather than a specific decade (which drifts with "today").
        Assert.Matches(@"20\d\d-\d\d", answer.Text);
    }

    [Fact]
    public void AffordabilityMentionsLiquidityAndTightestBudget()
    {
        var budget = new CoachBudgetFact(Guid.NewGuid(), "Freizeit", null, "EUR", 300m, 280m, 20m, 93.33m, 310m, -10m, false);
        var context = Context(monthlySavings: 500m, liquid: 2_000m, debt: 1_500m, budgets: [budget]);

        var answer = new DeterministicCoachEngine().Answer("Kann ich mir 1000 Euro leisten?", context);

        Assert.Contains("2.000", answer.Text.Replace(',', '.'), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Freizeit", answer.Text);
        Assert.Contains("20", answer.Text);
    }

    private static CoachContext Context(
        IReadOnlyList<CoachCategoryFact>? categories = null,
        IReadOnlyList<CoachBudgetFact>? budgets = null,
        decimal? currentNetWorth = 10_000m,
        decimal? monthlySavings = 500m,
        decimal? liquid = null,
        decimal? debt = null)
    {
        var from = new DateOnly(2026, 9, 1);
        var to = new DateOnly(2026, 9, 30);
        return new CoachContext(
            from, to, new DateOnly(2026, 8, 2), new DateOnly(2026, 8, 31), "EUR", false,
            3_000m, 500m, 2_500m, 3_000m, 400m, 2_600m, currentNetWorth, monthlySavings,
            categories ?? [], [], Summary(),
            [
                new CoachFact("cashflow:income", "Income", "3000 EUR"),
                new CoachFact("cashflow:outgoing", "Spending", "500 EUR"),
                new CoachFact("cashflow:net", "Net", "2500 EUR"),
                new CoachFact("reviews:coverage", "Coverage", "0%"),
                new CoachFact("networth:current", "Net worth", $"{currentNetWorth ?? 0} EUR"),
                new CoachFact("savings:monthly-average", "Savings", $"{monthlySavings ?? 0} EUR"),
                new CoachFact("wealth:liquid-accounts", "Liquidity", $"{liquid ?? 0} EUR"),
                new CoachFact("wealth:debt", "Debt", $"{debt ?? 0} EUR")
            ])
        {
            Budgets = budgets ?? [],
            LiquidAccountBalance = liquid,
            TotalDebt = debt
        };
    }

    private static SpendingReviewSummaryDto Summary() => new(
        new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30), "EUR", false,
        0m, 0m, 0m, 0m, 0m, 0m, null, 0, [], [], [], [], [], []);
}
