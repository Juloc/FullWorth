using System.Globalization;
using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Budgets;
using FullWorth.Backend.Modules.Fx;
using FullWorth.Backend.Modules.Portfolio;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Coach;

public sealed class CoachContextBuilder(
    FullWorthDbContext db,
    SpendingReviewService reviews,
    CurrencyConverter fx,
    BudgetStore budgetStore,
    WealthOverviewService wealth)
{
    private static readonly HashSet<string> AvoidableNegativeReasons = new(StringComparer.Ordinal)
    {
        "impulse", "too_expensive", "unused", "duplicate", "subscription_regret",
        "convenience_cost", "avoidable_fee", "poor_value"
    };

    public async Task<CoachContext> BuildAsync(Guid userId, Guid fullWorthSpaceId, DateOnly? requestedFrom, DateOnly? requestedTo, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var from = requestedFrom ?? new DateOnly(today.Year, today.Month, 1);
        var to = requestedTo ?? today;
        if (to < from) throw new ArgumentException("The end date must not be before the start date.");
        if (to.DayNumber - from.DayNumber > 3660) throw new ArgumentException("Coach date range is too large.");

        var days = to.DayNumber - from.DayNumber + 1;
        var comparisonTo = from.AddDays(-1);
        var comparisonFrom = comparisonTo.AddDays(-(days - 1));
        var currency = await db.FullWorthSpaces.AsNoTracking()
            .Where(x => x.Id == fullWorthSpaceId && db.FullWorthSpaceMembers.Any(m => m.FullWorthSpaceId == x.Id && m.UserId == userId))
            .Select(x => x.BaseCurrency)
            .SingleOrDefaultAsync(ct);
        if (currency is null) throw new KeyNotFoundException("FullWorth Space not found.");
        currency = FxSnapshot.Normalize(currency);

        var accessible = reviews.AccessibleTransactions(userId, fullWorthSpaceId).Where(x => !x.IsIgnored && !x.IsTransfer);
        var currentRows = await LoadRows(accessible.Where(x => x.BookingDate >= from && x.BookingDate <= to), fullWorthSpaceId, to, ct);
        var previousRows = await LoadRows(accessible.Where(x => x.BookingDate >= comparisonFrom && x.BookingDate <= comparisonTo), fullWorthSpaceId, comparisonTo, ct);
        var periodAccumulator = new FxAccumulator(await fx.PrepareAsync(currency, comparisonFrom, to, ct));
        ConvertRows(currentRows, periodAccumulator);
        ConvertRows(previousRows, periodAccumulator);

        await AttachReviewsAsync(currentRows, userId, fullWorthSpaceId, ct);

        var income = currentRows.Where(x => x.Amount > 0m).Sum(x => x.Amount);
        var outgoing = currentRows.Where(x => x.Amount < 0m).Sum(x => -x.Amount);
        var previousIncome = previousRows.Where(x => x.Amount > 0m).Sum(x => x.Amount);
        var previousOutgoing = previousRows.Where(x => x.Amount < 0m).Sum(x => -x.Amount);
        var reviewSummary = await reviews.GetSummaryAsync(userId, fullWorthSpaceId, from, to, ct);
        var reviewCategories = reviewSummary.Categories.ToDictionary(x => x.Key, StringComparer.OrdinalIgnoreCase);
        var reviewMerchants = reviewSummary.Merchants.ToDictionary(x => x.Key, StringComparer.OrdinalIgnoreCase);

        var budgetFacts = await LoadBudgetsAsync(userId, fullWorthSpaceId, to > today ? today : to, ct);
        var budgetOverageByCategory = budgetFacts
            .Where(x => x.CategoryId.HasValue && string.Equals(x.Currency, currency, StringComparison.OrdinalIgnoreCase))
            .GroupBy(x => x.CategoryId!.Value)
            .ToDictionary(x => x.Key, x => x.Sum(y => y.Overage));

        var previousCategories = previousRows.Where(x => x.Amount < 0m)
            .GroupBy(x => x.CategoryId?.ToString() ?? "uncategorized")
            .ToDictionary(x => x.Key, x => x.Sum(y => -y.Amount), StringComparer.OrdinalIgnoreCase);
        var categoryFacts = currentRows.Where(x => x.Amount < 0m)
            .GroupBy(x => new { x.CategoryId, x.CategoryName })
            .Select(group =>
            {
                var amount = group.Sum(x => -x.Amount);
                var key = group.Key.CategoryId?.ToString() ?? "uncategorized";
                previousCategories.TryGetValue(key, out var previousAmount);
                reviewCategories.TryGetValue(key, out var review);
                var avoidableNegative = group
                    .Where(x => x.Sentiment == SpendingSentiment.Negative && x.Reasons.Any(AvoidableNegativeReasons.Contains))
                    .Sum(x => -x.Amount);
                var budgetOverage = group.Key.CategoryId.HasValue && budgetOverageByCategory.TryGetValue(group.Key.CategoryId.Value, out var overage)
                    ? overage
                    : 0m;
                return new CoachCategoryFact(group.Key.CategoryId, group.Key.CategoryName, amount, previousAmount, amount - previousAmount,
                    outgoing > 0m ? amount / outgoing : 0m, review?.ReviewCoverage ?? 0m, review?.WorthItScore,
                    review?.NegativeAmount ?? 0m, review?.PositiveAmount ?? 0m)
                {
                    AvoidableNegativeReviewedAmount = avoidableNegative,
                    BudgetOverage = budgetOverage
                };
            })
            .OrderByDescending(x => x.Amount).Take(10).ToList();

        var previousMerchants = previousRows.Where(x => x.Amount < 0m)
            .GroupBy(x => NormalizeMerchantKey(x.MerchantName), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Sum(y => -y.Amount), StringComparer.OrdinalIgnoreCase);
        var merchantFacts = currentRows.Where(x => x.Amount < 0m)
            .GroupBy(x => NormalizeMerchantKey(x.MerchantName), StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var amount = group.Sum(x => -x.Amount);
                previousMerchants.TryGetValue(group.Key, out var previousAmount);
                reviewMerchants.TryGetValue(group.Key, out var review);
                return new CoachMerchantFact(group.First().MerchantName, amount, previousAmount, amount - previousAmount,
                    review?.ReviewCoverage ?? 0m, review?.WorthItScore, review?.NegativeAmount ?? 0m, review?.PositiveAmount ?? 0m);
            })
            .OrderByDescending(x => x.Amount).Take(10).ToList();

        var wealthOutcome = await wealth.GetOverviewForUserAsync(userId, fullWorthSpaceId, currency, ct);
        var wealthOverview = wealthOutcome.Status == WealthRequestStatus.Success ? wealthOutcome.Overview : null;
        decimal? currentNetWorth = wealthOverview is null ? null : wealthOverview.NetWorth;
        decimal? liquidAccountBalance = wealthOverview?.Accounts.IsComplete == true ? wealthOverview.Accounts.Amount : null;
        decimal? totalDebt = wealthOverview is not null && wealthOverview.Loans.IsComplete && wealthOverview.OtherLiabilities.IsComplete
            ? wealthOverview.TotalLiabilities
            : null;

        var savingsStart = today.AddDays(-90);
        var savingsRows = await LoadRows(accessible.Where(x => x.BookingDate >= savingsStart && x.BookingDate <= today), fullWorthSpaceId, today, ct);
        var savingsAccumulator = new FxAccumulator(await fx.PrepareAsync(currency, savingsStart, today, ct));
        ConvertRows(savingsRows, savingsAccumulator);
        var savingsTotal = savingsRows.Sum(x => x.Amount);
        decimal? averageMonthlySavings = savingsAccumulator.Incomplete ? null : savingsTotal / 3m;
        var incomplete = periodAccumulator.Incomplete || reviewSummary.Incomplete || savingsAccumulator.Incomplete || wealthOverview?.IsComplete == false;

        var positiveExamples = BuildExamples(currentRows, SpendingSentiment.Positive);
        var negativeExamples = BuildExamples(currentRows, SpendingSentiment.Negative);

        var facts = new List<CoachFact>
        {
            new("cashflow:income", $"Income {from:yyyy-MM-dd}–{to:yyyy-MM-dd}", FormatMoney(income, currency)),
            new("cashflow:outgoing", $"Spending {from:yyyy-MM-dd}–{to:yyyy-MM-dd}", FormatMoney(outgoing, currency)),
            new("cashflow:net", "Net cash flow", FormatMoney(income - outgoing, currency)),
            new("reviews:coverage", "Reviewed spending coverage", reviewSummary.ReviewCoverage.ToString("P0", CultureInfo.InvariantCulture))
        };
        if (averageMonthlySavings.HasValue)
            facts.Add(new("savings:monthly-average", "90-day average monthly cash surplus", FormatMoney(averageMonthlySavings.Value, currency)));
        if (currentNetWorth.HasValue) facts.Add(new("networth:current", "Current net worth", FormatMoney(currentNetWorth.Value, currency)));
        if (liquidAccountBalance.HasValue) facts.Add(new("wealth:liquid-accounts", "Visible liquid account balance", FormatMoney(liquidAccountBalance.Value, currency)));
        if (totalDebt.HasValue) facts.Add(new("wealth:debt", "Total recorded debt", FormatMoney(totalDebt.Value, currency)));
        if (incomplete) facts.Add(new("data:incomplete", "Data completeness", "Some financial components are incomplete or a required historical FX rate is missing."));
        foreach (var category in categoryFacts.Take(8))
        {
            var key = category.CategoryId?.ToString() ?? "uncategorized";
            facts.Add(new($"category:{key}:current", category.Name, FormatMoney(category.Amount, currency)));
            if (category.ReviewCoverage > 0m)
                facts.Add(new($"category:{key}:worth", $"{category.Name} review", $"score {category.WorthItScore:0.00}, coverage {category.ReviewCoverage:P0}"));
        }
        foreach (var merchant in merchantFacts.Take(8))
            facts.Add(new($"merchant:{NormalizeMerchantKey(merchant.Name)}:current", merchant.Name, FormatMoney(merchant.Amount, currency)));
        foreach (var budget in budgetFacts.Take(8))
            facts.Add(new($"budget:{budget.BudgetId}:status", $"Budget {budget.Name}", $"{FormatMoney(budget.Spent, budget.Currency)} / {FormatMoney(budget.Target, budget.Currency)}, remaining {FormatMoney(budget.Remaining, budget.Currency)}"));
        foreach (var example in positiveExamples.Concat(negativeExamples))
            facts.Add(new($"review:{example.TransactionId}", example.Label, $"{example.Sentiment}: {FormatMoney(example.Amount, currency)}"));

        return new CoachContext(from, to, comparisonFrom, comparisonTo, currency, incomplete, income, outgoing, income - outgoing,
            previousIncome, previousOutgoing, previousIncome - previousOutgoing, currentNetWorth, averageMonthlySavings,
            categoryFacts, merchantFacts, reviewSummary, facts)
        {
            LiquidAccountBalance = liquidAccountBalance,
            TotalDebt = totalDebt,
            Budgets = budgetFacts,
            PositiveExamples = positiveExamples,
            NegativeExamples = negativeExamples
        };
    }

    private async Task<List<CoachBudgetFact>> LoadBudgetsAsync(Guid userId, Guid fullWorthSpaceId, DateOnly asOf, CancellationToken ct)
    {
        var visible = await budgetStore.ListForUserAsync(userId, fullWorthSpaceId, ct);
        var result = new List<CoachBudgetFact>();
        foreach (var budget in visible.Where(x => x.IsActive && (!x.StartDate.HasValue || x.StartDate <= asOf) && (!x.EndDate.HasValue || x.EndDate >= asOf)).Take(20))
        {
            var status = await budgetStore.GetStatusForUserAsync(userId, fullWorthSpaceId, budget.Id, asOf, ct);
            if (status is null) continue;
            result.Add(new CoachBudgetFact(status.BudgetId, status.Name, status.CategoryId, FxSnapshot.Normalize(status.Currency),
                status.BudgetAmount, status.Spent, status.Remaining, status.PercentUsed,
                status.ProjectedEndSpend, status.ProjectedOverUnder, status.PartialAccess));
        }
        return result.OrderByDescending(x => x.PercentUsed).Take(10).ToList();
    }

    private async Task AttachReviewsAsync(List<TransactionFactRow> rows, Guid userId, Guid fullWorthSpaceId, CancellationToken ct)
    {
        var ids = rows.Where(x => x.Amount < 0m).Select(x => x.TransactionId).ToArray();
        if (ids.Length == 0) return;
        var reviewRows = await db.Set<SpendingReview>().AsNoTracking()
            .Where(x => x.FullWorthSpaceId == fullWorthSpaceId && x.UserId == userId && ids.Contains(x.TransactionId))
            .Select(x => new { x.TransactionId, x.Sentiment, x.ReasonsJson })
            .ToListAsync(ct);
        var byId = reviewRows.ToDictionary(x => x.TransactionId);
        foreach (var row in rows)
        {
            if (!byId.TryGetValue(row.TransactionId, out var review)) continue;
            row.Sentiment = review.Sentiment;
            row.Reasons = SpendingReviewService.DeserializeReasons(review.ReasonsJson);
        }
    }

    private static IReadOnlyList<CoachReviewExample> BuildExamples(IReadOnlyList<TransactionFactRow> rows, SpendingSentiment sentiment) =>
        rows.Where(x => x.Amount < 0m && x.Sentiment == sentiment)
            .OrderByDescending(x => -x.Amount)
            .Take(5)
            .Select(x => new CoachReviewExample(x.TransactionId, sentiment, x.MerchantName, -x.Amount, x.Reasons))
            .ToList();

    private async Task<List<TransactionFactRow>> LoadRows(
        IQueryable<FullWorth.Backend.Modules.Transactions.FinanceTransaction> query,
        Guid fullWorthSpaceId,
        DateOnly fallbackDate,
        CancellationToken ct) =>
        await query.Select(x => new TransactionFactRow
        {
            TransactionId = x.Id,
            Amount = x.Amount,
            Currency = x.Currency,
            Date = x.BookingDate ?? x.ValueDate ?? fallbackDate,
            CategoryId = x.CategoryId,
            CategoryName = x.CategoryId.HasValue
                ? db.Categories.Where(c => c.Id == x.CategoryId.Value && c.FullWorthSpaceId == fullWorthSpaceId).Select(c => c.Name).FirstOrDefault() ?? "Uncategorized"
                : "Uncategorized",
            MerchantName = x.NormalizedCounterparty ?? x.Counterparty ?? x.Description ?? "Unknown"
        }).ToListAsync(ct);

    private static void ConvertRows(List<TransactionFactRow> rows, FxAccumulator accumulator)
    {
        for (var index = rows.Count - 1; index >= 0; index--)
        {
            var row = rows[index];
            var converted = accumulator.Convert(row.Amount, row.Currency, row.Date);
            if (!converted.HasValue)
            {
                rows.RemoveAt(index);
                continue;
            }
            row.Amount = converted.Value;
        }
    }

    internal static string FormatMoney(decimal value, string currency) => $"{value:N2} {currency}";
    internal static string NormalizeMerchantKey(string value) => value.Trim().ToUpperInvariant();

    private sealed class TransactionFactRow
    {
        public Guid TransactionId { get; init; }
        public decimal Amount { get; set; }
        public string Currency { get; init; } = "EUR";
        public DateOnly Date { get; init; }
        public Guid? CategoryId { get; init; }
        public string CategoryName { get; init; } = "Uncategorized";
        public string MerchantName { get; init; } = "Unknown";
        public SpendingSentiment? Sentiment { get; set; }
        public IReadOnlyList<string> Reasons { get; set; } = [];
    }
}
