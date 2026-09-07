using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FullWorth.Backend.Modules.Intelligence.Context;

namespace FullWorth.Backend.Modules.Intelligence.Signals;

public interface IFinancialSignalDetector
{
    string Source { get; }
    IReadOnlyList<DetectedFinancialSignal> Detect(FinancialContextSnapshot context, DateTimeOffset now);
}

public static class FinancialSignalRanker
{
    public static decimal Score(string severity, decimal confidence, decimal? impactAmount)
    {
        var severityScore = severity switch
        {
            FinancialSignalSeverities.High => 80m,
            FinancialSignalSeverities.Attention => 50m,
            _ => 20m
        };
        var confidenceScore = Math.Clamp(confidence, 0m, 1m) * 10m;
        var impactScore = impactAmount.HasValue
            ? Math.Min(30m, decimal.Abs(impactAmount.Value) / 10m)
            : 0m;
        return Math.Round(severityScore + confidenceScore + impactScore, 2, MidpointRounding.AwayFromZero);
    }
}

public sealed class SpendingShiftSignalDetector : IFinancialSignalDetector
{
    public string Source => "detector:spending-shift";

    public IReadOnlyList<DetectedFinancialSignal> Detect(FinancialContextSnapshot context, DateTimeOffset now)
    {
        var result = new List<DetectedFinancialSignal>();
        var periodKey = PeriodKey(context.Period);
        var currency = context.BaseCurrency;

        AddTotal(result, context, periodKey, currency, now);

        foreach (var category in context.Categories
                     .Where(x => IsMeaningfulIncrease(x.Amount, x.PreviousAmount, 20m, .25m))
                     .OrderByDescending(x => x.Delta)
                     .Take(3))
        {
            var subject = category.CategoryId?.ToString("N") ?? "uncategorized";
            result.Add(Create(
                context,
                "spending-shift",
                "category",
                subject,
                $"spending-shift:category:{subject}:{periodKey}",
                FinancialSignalSeverities.Attention,
                .95m,
                category.Delta,
                currency,
                "insights.spending.categoryIncreased",
                new
                {
                    category = category.Name,
                    current = category.Amount,
                    previous = category.PreviousAmount,
                    delta = category.Delta,
                    percent = Percent(category.Amount, category.PreviousAmount),
                    context.Period.From,
                    context.Period.To
                },
                now));
        }

        foreach (var merchant in context.Merchants
                     .Where(x => IsMeaningfulIncrease(x.Amount, x.PreviousAmount, 20m, .25m))
                     .OrderByDescending(x => x.Delta)
                     .Take(3))
        {
            var subject = StableKey(merchant.Name);
            result.Add(Create(
                context,
                "spending-shift",
                "merchant",
                subject,
                $"spending-shift:merchant:{subject}:{periodKey}",
                FinancialSignalSeverities.Attention,
                .9m,
                merchant.Delta,
                currency,
                "insights.spending.merchantIncreased",
                new
                {
                    merchant = merchant.Name,
                    current = merchant.Amount,
                    previous = merchant.PreviousAmount,
                    delta = merchant.Delta,
                    percent = Percent(merchant.Amount, merchant.PreviousAmount),
                    context.Period.From,
                    context.Period.To
                },
                now));
        }

        return result;
    }

    private static void AddTotal(
        List<DetectedFinancialSignal> result,
        FinancialContextSnapshot context,
        string periodKey,
        string currency,
        DateTimeOffset now)
    {
        var current = context.CashFlow.Outgoing;
        var previous = context.CashFlow.PreviousOutgoing;
        var delta = current - previous;
        if (previous < 50m || decimal.Abs(delta) < 50m || decimal.Abs(delta) / previous < .25m) return;

        var increased = delta > 0m;
        result.Add(Create(
            context,
            "spending-shift",
            "cashflow",
            "outgoing",
            $"spending-shift:outgoing:{periodKey}",
            increased ? FinancialSignalSeverities.Attention : FinancialSignalSeverities.Info,
            1m,
            decimal.Abs(delta),
            currency,
            increased ? "insights.spending.totalIncreased" : "insights.spending.totalDecreased",
            new
            {
                current,
                previous,
                delta,
                percent = Percent(current, previous),
                context.Period.From,
                context.Period.To
            },
            now));
    }

    private static bool IsMeaningfulIncrease(decimal current, decimal previous, decimal minimumDelta, decimal minimumRatio) =>
        previous >= 20m && current - previous >= minimumDelta && (current - previous) / previous >= minimumRatio;

    internal static decimal? Percent(decimal current, decimal previous) =>
        previous == 0m ? null : Math.Round((current - previous) / previous * 100m, 1, MidpointRounding.AwayFromZero);

    internal static string PeriodKey(FinancialContextPeriod period) =>
        period.From.Year == period.To.Year && period.From.Month == period.To.Month
            ? $"{period.To:yyyy-MM}"
            : $"{period.From:yyyyMMdd}-{period.To:yyyyMMdd}";

    internal static string StableKey(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim().ToUpperInvariant()));
        return Convert.ToHexString(bytes.AsSpan(0, 8)).ToLowerInvariant();
    }

    internal static DetectedFinancialSignal Create(
        FinancialContextSnapshot context,
        string type,
        string subjectType,
        string subjectId,
        string semanticKey,
        string severity,
        decimal confidence,
        decimal? impactAmount,
        string? impactCurrency,
        string titleKey,
        object evidence,
        DateTimeOffset now)
    {
        var evidenceJson = JsonSerializer.Serialize(evidence);
        var payloadJson = JsonSerializer.Serialize(new
        {
            period = new { context.Period.From, context.Period.To },
            subjectType,
            subjectId
        });
        return new DetectedFinancialSignal(
            context.FullWorthSpaceId,
            context.UserId,
            type,
            subjectType,
            subjectId,
            semanticKey,
            $"detector:{type}",
            severity,
            confidence,
            impactAmount,
            impactCurrency,
            titleKey,
            payloadJson,
            evidenceJson,
            FinancialSignalRanker.Score(severity, confidence, impactAmount),
            now,
            now.AddDays(35));
    }
}

public sealed class BudgetDriftSignalDetector : IFinancialSignalDetector
{
    public string Source => "detector:budget-drift";

    public IReadOnlyList<DetectedFinancialSignal> Detect(FinancialContextSnapshot context, DateTimeOffset now)
    {
        var periodKey = SpendingShiftSignalDetector.PeriodKey(context.Period);
        var result = new List<DetectedFinancialSignal>();

        foreach (var budget in context.Budgets.Where(x => !x.PartialAccess))
        {
            var over = Math.Max(0m, -budget.Remaining);
            var projectedOver = Math.Max(0m, -budget.ProjectedOverUnder);
            if (over <= 0m && projectedOver < 10m) continue;

            var actualOver = over > 0m;
            var impact = actualOver ? over : projectedOver;
            var severity = actualOver && impact >= 100m
                ? FinancialSignalSeverities.High
                : FinancialSignalSeverities.Attention;
            result.Add(SpendingShiftSignalDetector.Create(
                context,
                "budget-drift",
                "budget",
                budget.BudgetId.ToString("N"),
                $"budget-drift:{budget.BudgetId:N}:{periodKey}",
                severity,
                1m,
                impact,
                budget.Currency,
                actualOver ? "insights.budget.over" : "insights.budget.projectedOver",
                new
                {
                    budget = budget.Name,
                    target = budget.Target,
                    spent = budget.Spent,
                    remaining = budget.Remaining,
                    percentUsed = budget.PercentUsed,
                    projectedEndSpend = budget.ProjectedEndSpend,
                    projectedOverUnder = budget.ProjectedOverUnder
                },
                now));
        }

        return result;
    }
}

public sealed class SavingsChangeSignalDetector : IFinancialSignalDetector
{
    public string Source => "detector:savings-change";

    public IReadOnlyList<DetectedFinancialSignal> Detect(FinancialContextSnapshot context, DateTimeOffset now)
    {
        var current = context.Wealth.AverageMonthlySavings;
        var previous = context.Wealth.PreviousAverageMonthlySavings;
        if (!current.HasValue || !previous.HasValue) return [];

        var delta = current.Value - previous.Value;
        var absDelta = decimal.Abs(delta);
        var relativeEnough = decimal.Abs(previous.Value) >= 100m
            ? absDelta / decimal.Abs(previous.Value) >= .20m
            : absDelta >= 150m;
        if (absDelta < 75m || !relativeEnough) return [];

        var worsened = delta < 0m;
        var severity = worsened ? FinancialSignalSeverities.Attention : FinancialSignalSeverities.Info;
        return
        [
            SpendingShiftSignalDetector.Create(
                context,
                "savings-change",
                "cashflow",
                "90-day-surplus",
                $"savings-change:90-day:{context.AsOf:yyyy-MM}",
                severity,
                .95m,
                absDelta,
                context.BaseCurrency,
                worsened ? "insights.savings.decreased" : "insights.savings.increased",
                new
                {
                    currentAverageMonthlySavings = current.Value,
                    previousAverageMonthlySavings = previous.Value,
                    delta,
                    percent = SpendingShiftSignalDetector.Percent(current.Value, previous.Value)
                },
                now)
        ];
    }
}

public sealed class DataQualitySignalDetector : IFinancialSignalDetector
{
    public string Source => "detector:data-quality";

    public IReadOnlyList<DetectedFinancialSignal> Detect(FinancialContextSnapshot context, DateTimeOffset now)
    {
        if (context.DataQuality.IsComplete) return [];
        return
        [
            SpendingShiftSignalDetector.Create(
                context,
                "data-quality",
                "financial-context",
                context.FullWorthSpaceId.ToString("N"),
                "data-quality:incomplete",
                FinancialSignalSeverities.Info,
                1m,
                null,
                null,
                "insights.data.incomplete",
                new { context.DataQuality.SourceVersion },
                now)
        ];
    }
}

public sealed class ClassificationQualitySignalDetector : IFinancialSignalDetector
{
    public string Source => "detector:classification-quality";

    public IReadOnlyList<DetectedFinancialSignal> Detect(FinancialContextSnapshot context, DateTimeOffset now)
    {
        var recent = context.DataQuality.RecentTransactionCount;
        var uncategorized = context.DataQuality.UncategorizedRecentTransactionCount;
        if (recent < 5 || uncategorized < 3 || (decimal)uncategorized / recent < .15m) return [];

        var ratio = (decimal)uncategorized / recent;
        var severity = ratio >= .30m ? FinancialSignalSeverities.Attention : FinancialSignalSeverities.Info;
        return
        [
            SpendingShiftSignalDetector.Create(
                context,
                "classification-quality",
                "recent-transactions",
                "uncategorized",
                $"classification-quality:uncategorized:{context.AsOf:yyyy-MM}",
                severity,
                .9m,
                null,
                null,
                "insights.data.uncategorizedRecent",
                new
                {
                    recentTransactionCount = recent,
                    uncategorizedRecentTransactionCount = uncategorized,
                    share = Math.Round(ratio * 100m, 1, MidpointRounding.AwayFromZero)
                },
                now)
        ];
    }
}

public sealed class FinancialSignalDetectionService(
    IEnumerable<IFinancialSignalDetector> detectors,
    FinancialSignalStore store)
{
    public async Task<int> DetectAndPersistAsync(
        FinancialContextSnapshot context,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var count = 0;
        foreach (var detector in detectors)
        {
            var signals = detector.Detect(context, now)
                .Where(x => string.Equals(x.Source, detector.Source, StringComparison.Ordinal))
                .GroupBy(x => x.SemanticKey, StringComparer.Ordinal)
                .Select(x => x.OrderByDescending(y => y.RankScore).First())
                .ToList();

            foreach (var signal in signals)
            {
                await store.UpsertAsync(signal, ct);
                count++;
            }

            await store.ResolveMissingBySourceAsync(
                context.UserId,
                context.FullWorthSpaceId,
                detector.Source,
                signals.Select(x => x.SemanticKey).ToHashSet(StringComparer.Ordinal),
                now,
                ct);
        }

        return count;
    }
}
