using FullWorth.Backend.Modules.Intelligence.Context;
using FullWorth.Backend.Modules.Intelligence.Signals;

namespace FullWorth.Backend.Tests.Intelligence;

public sealed class FinancialSignalDetectorTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Spending_detector_ignores_small_noise_and_flags_meaningful_increases()
    {
        var context = Context(
            outgoing: 900m,
            previousOutgoing: 600m,
            categories:
            [
                new FinancialCategorySnapshot(Guid.NewGuid(), "Dining", 180m, 100m, 80m, .2m, 0m, null, 0m, 0m),
                new FinancialCategorySnapshot(Guid.NewGuid(), "Tiny", 21m, 19m, 2m, .02m, 0m, null, 0m, 0m)
            ],
            merchants:
            [
                new FinancialMerchantSnapshot("REWE", 160m, 100m, 60m, 0m, null),
                new FinancialMerchantSnapshot("Coffee", 24m, 20m, 4m, 0m, null)
            ]);

        var signals = new SpendingShiftSignalDetector().Detect(context, Now);

        Assert.Contains(signals, x => x.SubjectType == "cashflow" && x.SubjectId == "outgoing");
        Assert.Contains(signals, x => x.SubjectType == "category");
        Assert.Contains(signals, x => x.SubjectType == "merchant");
        Assert.DoesNotContain(signals, x => x.EvidenceJson.Contains("Tiny", StringComparison.Ordinal));
        Assert.DoesNotContain(signals, x => x.EvidenceJson.Contains("Coffee", StringComparison.Ordinal));
        Assert.All(signals, x => Assert.Equal("detector:spending-shift", x.Source));
    }

    [Fact]
    public void Budget_detector_uses_existing_projection_and_skips_partial_access()
    {
        var visible = new FinancialBudgetSnapshot(
            Guid.NewGuid(), "Food", null, "EUR",
            400m, 360m, 40m, 90m, 470m, -70m, false);
        var partial = new FinancialBudgetSnapshot(
            Guid.NewGuid(), "Shared", null, "EUR",
            400m, 450m, -50m, 112.5m, 500m, -100m, true);

        var signals = new BudgetDriftSignalDetector().Detect(
            Context(budgets: [visible, partial]), Now);

        var signal = Assert.Single(signals);
        Assert.Equal(visible.BudgetId.ToString("N"), signal.SubjectId);
        Assert.Equal(70m, signal.ImpactAmount);
        Assert.Equal("detector:budget-drift", signal.Source);
    }

    [Theory]
    [InlineData(600, 900, false)]
    [InlineData(600, 350, true)]
    public void Savings_detector_distinguishes_improvement_and_deterioration(
        decimal previous,
        decimal current,
        bool worsened)
    {
        var context = Context(
            averageSavings: current,
            previousAverageSavings: previous);

        var signal = Assert.Single(new SavingsChangeSignalDetector().Detect(context, Now));

        Assert.Equal("detector:savings-change", signal.Source);
        Assert.Equal(
            worsened ? FinancialSignalSeverities.Attention : FinancialSignalSeverities.Info,
            signal.Severity);
        Assert.Equal(worsened ? "insights.savings.decreased" : "insights.savings.increased", signal.TitleKey);
    }

    [Fact]
    public void Data_quality_and_classification_detectors_are_separate_sources()
    {
        var context = Context(
            complete: false,
            recentCount: 20,
            uncategorizedCount: 7);

        var data = Assert.Single(new DataQualitySignalDetector().Detect(context, Now));
        var classification = Assert.Single(new ClassificationQualitySignalDetector().Detect(context, Now));

        Assert.Equal("detector:data-quality", data.Source);
        Assert.Equal("detector:classification-quality", classification.Source);
        Assert.NotEqual(data.SemanticKey, classification.SemanticKey);
    }

    [Fact]
    public void Classification_detector_requires_meaningful_sample()
    {
        var context = Context(recentCount: 4, uncategorizedCount: 3);

        Assert.Empty(new ClassificationQualitySignalDetector().Detect(context, Now));
    }

    private static FinancialContextSnapshot Context(
        decimal outgoing = 500m,
        decimal previousOutgoing = 500m,
        decimal? averageSavings = 500m,
        decimal? previousAverageSavings = 500m,
        bool complete = true,
        int recentCount = 10,
        int uncategorizedCount = 0,
        IReadOnlyList<FinancialCategorySnapshot>? categories = null,
        IReadOnlyList<FinancialMerchantSnapshot>? merchants = null,
        IReadOnlyList<FinancialBudgetSnapshot>? budgets = null)
    {
        var userId = Guid.NewGuid();
        var spaceId = Guid.NewGuid();
        return new FinancialContextSnapshot(
            userId,
            spaceId,
            "EUR",
            Now,
            new FinancialContextPeriod(
                new DateOnly(2026, 9, 1),
                new DateOnly(2026, 9, 7),
                new DateOnly(2026, 8, 25),
                new DateOnly(2026, 8, 31)),
            new FinancialCashFlowSnapshot(
                3_000m,
                outgoing,
                3_000m - outgoing,
                3_000m,
                previousOutgoing,
                3_000m - previousOutgoing),
            new FinancialWealthSnapshot(
                50_000m,
                5_000m,
                10_000m,
                averageSavings,
                previousAverageSavings),
            categories ?? [],
            merchants ?? [],
            budgets ?? [],
            [],
            [],
            [],
            new FinancialReviewSnapshot(0m, 0m, 0m, 0m, 0m, 0m, null, 0),
            new FinancialDataQualitySnapshot(
                complete,
                recentCount,
                uncategorizedCount,
                FinancialContextSnapshotService.SourceVersion));
    }
}
