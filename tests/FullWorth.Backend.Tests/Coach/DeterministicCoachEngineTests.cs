using FullWorth.Backend.Modules.Coach;

namespace FullWorth.Backend.Tests.Coach;

public sealed class DeterministicCoachEngineTests
{
    [Fact]
    public void ReduceIntentPrefersNegativelyReviewedSpendOverHigherPositiveSpend()
    {
        var diningId = Guid.NewGuid();
        var shoppingId = Guid.NewGuid();
        var context = Context(
            categories:
            [
                new CoachCategoryFact(diningId, "Dining", 300m, 200m, 100m, .6m, .8m, .9m, 0m, 270m),
                new CoachCategoryFact(shoppingId, "Online shopping", 120m, 50m, 70m, .24m, .75m, -.7m, 84m, 0m)
            ],
            reviews: Summary(
                highPositive: [new WorthItGroupDto(diningId.ToString(), "Dining", 300m, 240m, 216m, 24m, 0m, .8m, .9m, 3)],
                negative: [new WorthItGroupDto(shoppingId.ToString(), "Online shopping", 120m, 90m, 0m, 6m, 84m, .75m, -.9333333333333333333333333333m, 3)]));

        var answer = new DeterministicCoachEngine().Answer("Was könnte ich reduzieren?", context);

        Assert.Contains("Online shopping", answer.Text);
        Assert.Contains("Dining", answer.Text);
        Assert.Contains("positiv", answer.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TargetProjectionDoesNotInventDateWithoutPositiveSavings()
    {
        var context = Context(currentNetWorth: 50_000m, monthlySavings: 0m);
        var scenarios = new DeterministicCoachEngine().BuildTargetScenarios(context);

        Assert.All(scenarios, scenario =>
        {
            if (scenario.Target > 50_000m)
            {
                Assert.Null(scenario.EstimatedDate);
                Assert.Null(scenario.Months);
            }
        });
    }

    [Fact]
    public void TargetProjectionUsesZeroReturnByDefault()
    {
        var context = Context(currentNetWorth: 50_000m, monthlySavings: 1_000m);
        var first = new DeterministicCoachEngine().BuildTargetScenarios(context).First(x => x.Target == 100_000m);

        Assert.Equal(50, first.Months);
        Assert.Null(first.AssumedAnnualReturn);
    }

    private static CoachContext Context(
        IReadOnlyList<CoachCategoryFact>? categories = null,
        SpendingReviewSummaryDto? reviews = null,
        decimal? currentNetWorth = 10_000m,
        decimal? monthlySavings = 500m)
    {
        var from = new DateOnly(2026, 9, 1);
        var to = new DateOnly(2026, 9, 30);
        return new CoachContext(
            from,
            to,
            new DateOnly(2026, 8, 2),
            new DateOnly(2026, 8, 31),
            "EUR",
            false,
            3_000m,
            500m,
            2_500m,
            3_000m,
            400m,
            2_600m,
            currentNetWorth,
            monthlySavings,
            categories ?? [],
            [],
            reviews ?? Summary(),
            [
                new CoachFact("cashflow:income", "Income", "3,000 EUR"),
                new CoachFact("cashflow:outgoing", "Spending", "500 EUR"),
                new CoachFact("cashflow:net", "Net cash flow", "2,500 EUR"),
                new CoachFact("reviews:coverage", "Reviewed spending coverage", "0%"),
                new CoachFact("networth:current", "Current net worth", $"{currentNetWorth ?? 0} EUR"),
                new CoachFact("savings:monthly-average", "90-day average monthly cash surplus", $"{monthlySavings ?? 0} EUR")
            ]);
    }

    private static SpendingReviewSummaryDto Summary(
        IReadOnlyList<WorthItGroupDto>? highPositive = null,
        IReadOnlyList<WorthItGroupDto>? negative = null) =>
        new(
            new DateOnly(2026, 9, 1),
            new DateOnly(2026, 9, 30),
            "EUR",
            false,
            0m,
            0m,
            0m,
            0m,
            0m,
            0m,
            null,
            0,
            [],
            [],
            highPositive ?? [],
            negative ?? [],
            [],
            []);
}
