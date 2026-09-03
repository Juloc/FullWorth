using System.Text.Json;
using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Fx;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Coach;

public enum SpendingReviewWriteResult
{
    Saved,
    NotFound,
    Invalid
}

public sealed class SpendingReviewValidationException(string message) : ArgumentException(message);

public sealed class SpendingReviewService(FullWorthDbContext db, CurrencyConverter fx)
{
    private DbSet<SpendingReview> Reviews => db.Set<SpendingReview>();

    private static readonly IReadOnlyDictionary<SpendingSentiment, HashSet<string>> ReasonsBySentiment =
        new Dictionary<SpendingSentiment, HashSet<string>>
        {
            [SpendingSentiment.Positive] = new(StringComparer.Ordinal)
            {
                "necessary", "good_value", "quality_of_life", "experience", "health_or_wellbeing",
                "gift_or_relationship", "long_term_value"
            },
            [SpendingSentiment.Neutral] = new(StringComparer.Ordinal)
            {
                "routine", "expected", "mixed", "unsure"
            },
            [SpendingSentiment.Negative] = new(StringComparer.Ordinal)
            {
                "impulse", "too_expensive", "unused", "duplicate", "subscription_regret",
                "convenience_cost", "avoidable_fee", "poor_value"
            }
        };

    public static IReadOnlyDictionary<SpendingSentiment, IReadOnlyCollection<string>> ReasonCatalog =>
        ReasonsBySentiment.ToDictionary(x => x.Key, x => (IReadOnlyCollection<string>)x.Value);

    public async Task<SpendingReviewDto?> GetAsync(Guid userId, Guid fullWorthSpaceId, Guid transactionId, CancellationToken ct)
    {
        if (!await CanAccessTransactionAsync(userId, fullWorthSpaceId, transactionId, ct)) return null;
        var review = await Reviews.AsNoTracking().SingleOrDefaultAsync(x =>
            x.FullWorthSpaceId == fullWorthSpaceId && x.UserId == userId && x.TransactionId == transactionId, ct);
        return review is null ? null : ToDto(review);
    }

    public async Task<(SpendingReviewWriteResult Result, SpendingReviewDto? Review, string? Error)> UpsertAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        Guid transactionId,
        UpsertSpendingReviewRequest request,
        CancellationToken ct)
    {
        if (!Enum.IsDefined(request.Sentiment))
            return (SpendingReviewWriteResult.Invalid, null, "Invalid sentiment.");

        var reasons = NormalizeAndValidateReasons(request.Sentiment, request.Reasons);
        var note = NormalizeNote(request.Note);
        if (!await CanAccessTransactionAsync(userId, fullWorthSpaceId, transactionId, ct))
            return (SpendingReviewWriteResult.NotFound, null, null);

        if (request.PurchaseId is { } purchaseId && !await db.Purchases.AsNoTracking().AnyAsync(p =>
                p.Id == purchaseId &&
                p.FullWorthSpaceId == fullWorthSpaceId &&
                (p.Visibility != "private" || p.CreatedByUserId == userId) &&
                (p.TransactionId == transactionId || p.PaymentLinks.Any(link => link.TransactionId == transactionId)), ct))
            return (SpendingReviewWriteResult.Invalid, null, "Purchase must belong to this transaction and FullWorth Space.");

        var now = DateTimeOffset.UtcNow;
        var review = await Reviews.SingleOrDefaultAsync(x =>
            x.FullWorthSpaceId == fullWorthSpaceId && x.UserId == userId && x.TransactionId == transactionId, ct);
        if (review is null)
        {
            review = new SpendingReview
            {
                FullWorthSpaceId = fullWorthSpaceId,
                UserId = userId,
                TransactionId = transactionId,
                CreatedAt = now
            };
            Reviews.Add(review);
        }

        review.PurchaseId = request.PurchaseId;
        review.Sentiment = request.Sentiment;
        review.ReasonsJson = JsonSerializer.Serialize(reasons);
        review.Note = note;
        review.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        return (SpendingReviewWriteResult.Saved, ToDto(review), null);
    }

    public async Task<bool> DeleteAsync(Guid userId, Guid fullWorthSpaceId, Guid transactionId, CancellationToken ct)
    {
        if (!await CanAccessTransactionAsync(userId, fullWorthSpaceId, transactionId, ct)) return false;
        var review = await Reviews.SingleOrDefaultAsync(x =>
            x.FullWorthSpaceId == fullWorthSpaceId && x.UserId == userId && x.TransactionId == transactionId, ct);
        if (review is null) return false;
        Reviews.Remove(review);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<object>> RecentAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        int limit,
        SpendingSentiment? sentiment,
        CancellationToken ct)
    {
        limit = Math.Clamp(limit, 1, 100);
        var query = Reviews.AsNoTracking().Where(x =>
            x.FullWorthSpaceId == fullWorthSpaceId && x.UserId == userId &&
            db.Transactions.Any(t => t.Id == x.TransactionId && db.Accounts.Any(a =>
                a.Id == t.AccountId && a.FullWorthSpaceId == fullWorthSpaceId &&
                db.FullWorthSpaceMembers.Any(m => m.FullWorthSpaceId == fullWorthSpaceId && m.UserId == userId) &&
                a.Owners.Any(o => o.UserId == userId))));
        if (sentiment.HasValue) query = query.Where(x => x.Sentiment == sentiment.Value);

        var rows = await query.OrderByDescending(x => x.UpdatedAt).Take(limit).Select(x => new
        {
            x.Id,
            x.TransactionId,
            x.Sentiment,
            x.ReasonsJson,
            x.Note,
            x.UpdatedAt,
            Transaction = db.Transactions.Where(t => t.Id == x.TransactionId).Select(t => new
            {
                t.BookingDate,
                t.Amount,
                t.Currency,
                Merchant = t.Counterparty ?? t.Description ?? "Unknown",
                Category = db.Categories.Where(c => c.Id == t.CategoryId).Select(c => c.Name).FirstOrDefault()
            }).First()
        }).ToListAsync(ct);

        return rows.Select(x => (object)new
        {
            x.Id,
            x.TransactionId,
            sentiment = x.Sentiment,
            reasons = DeserializeReasons(x.ReasonsJson),
            x.Note,
            x.UpdatedAt,
            transaction = x.Transaction
        }).ToList();
    }

    public async Task<SpendingReviewSummaryDto> GetSummaryAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        DateOnly? from,
        DateOnly? to,
        CancellationToken ct)
    {
        var (periodFrom, periodTo) = NormalizePeriod(from, to);
        var currency = await db.FullWorthSpaces.AsNoTracking()
            .Where(x => x.Id == fullWorthSpaceId && db.FullWorthSpaceMembers.Any(m => m.FullWorthSpaceId == x.Id && m.UserId == userId))
            .Select(x => x.BaseCurrency)
            .SingleOrDefaultAsync(ct);
        if (currency is null) throw new KeyNotFoundException("FullWorth Space not found.");

        var transactions = await AccessibleOutgoingTransactions(userId, fullWorthSpaceId)
            .Where(t => t.BookingDate >= periodFrom && t.BookingDate <= periodTo)
            .Select(transaction => new AnalyticsRow
            {
                TransactionId = transaction.Id,
                Amount = -transaction.Amount,
                Currency = transaction.Currency,
                Date = transaction.BookingDate ?? transaction.ValueDate ?? periodFrom,
                CategoryKey = transaction.CategoryId.HasValue ? transaction.CategoryId.Value.ToString() : "uncategorized",
                CategoryLabel = transaction.CategoryId.HasValue
                    ? db.Categories.Where(c => c.Id == transaction.CategoryId.Value && c.FullWorthSpaceId == fullWorthSpaceId).Select(c => c.Name).FirstOrDefault() ?? "Uncategorized"
                    : "Uncategorized",
                MerchantLabel = transaction.NormalizedCounterparty ?? transaction.Counterparty ?? transaction.Description ?? "Unknown"
            })
            .ToListAsync(ct);

        var fxAccumulator = new FxAccumulator(await fx.PrepareAsync(currency, periodFrom, periodTo, ct));
        for (var index = transactions.Count - 1; index >= 0; index--)
        {
            var row = transactions[index];
            var converted = fxAccumulator.Convert(row.Amount, row.Currency, row.Date);
            if (!converted.HasValue)
            {
                transactions.RemoveAt(index);
                continue;
            }
            row.Amount = converted.Value;
        }

        var transactionIds = transactions.Select(x => x.TransactionId).ToArray();
        var reviewRows = transactionIds.Length == 0
            ? []
            : await Reviews.AsNoTracking()
                .Where(x => x.FullWorthSpaceId == fullWorthSpaceId && x.UserId == userId && transactionIds.Contains(x.TransactionId))
                .Select(x => new ReviewRow(x.TransactionId, x.Sentiment, x.ReasonsJson))
                .ToListAsync(ct);
        var byTransaction = reviewRows.ToDictionary(x => x.TransactionId);
        foreach (var row in transactions)
        {
            if (!byTransaction.TryGetValue(row.TransactionId, out var review)) continue;
            row.Sentiment = review.Sentiment;
            row.ReasonsJson = review.ReasonsJson;
        }

        var totalOutgoing = transactions.Sum(x => x.Amount);
        var reviewedRows = transactions.Where(x => x.Sentiment.HasValue).ToList();
        var reviewed = reviewedRows.Sum(x => x.Amount);
        var positive = reviewedRows.Where(x => x.Sentiment == SpendingSentiment.Positive).Sum(x => x.Amount);
        var neutral = reviewedRows.Where(x => x.Sentiment == SpendingSentiment.Neutral).Sum(x => x.Amount);
        var negative = reviewedRows.Where(x => x.Sentiment == SpendingSentiment.Negative).Sum(x => x.Amount);

        var categories = BuildGroups(transactions, x => x.CategoryKey, x => x.CategoryLabel)
            .OrderByDescending(x => x.TotalOutgoingAmount).ToList();
        var merchants = BuildGroups(transactions, x => NormalizeMerchantKey(x.MerchantLabel), x => x.MerchantLabel)
            .OrderByDescending(x => x.TotalOutgoingAmount).ToList();

        var highSpendPositive = categories
            .Where(IsRepresentative)
            .Where(x => x.WorthItScore is >= 0.25m)
            .OrderByDescending(x => x.TotalOutgoingAmount)
            .Take(5)
            .ToList();
        var negativeOpportunities = categories
            .Where(IsRepresentative)
            .Where(x => x.NegativeAmount > 0m)
            .OrderByDescending(OpportunityScore)
            .Take(5)
            .ToList();

        var reasonAmounts = reviewedRows
            .SelectMany(row => DeserializeReasons(row.ReasonsJson).Select(reason => new { Sentiment = row.Sentiment!.Value, row.Amount, Reason = reason }))
            .GroupBy(x => new { x.Sentiment, x.Reason })
            .Select(g => new { g.Key.Sentiment, g.Key.Reason, Amount = g.Sum(x => x.Amount), Count = g.Count() })
            .ToList();

        return new SpendingReviewSummaryDto(
            periodFrom,
            periodTo,
            currency,
            fxAccumulator.Incomplete,
            totalOutgoing,
            reviewed,
            SafeRatio(reviewed, totalOutgoing),
            positive,
            neutral,
            negative,
            reviewed > 0m ? (positive - negative) / reviewed : null,
            reviewedRows.Count,
            categories,
            merchants,
            highSpendPositive,
            negativeOpportunities,
            reasonAmounts.Where(x => x.Sentiment == SpendingSentiment.Positive)
                .OrderByDescending(x => x.Amount).Select(x => new SpendingReasonAmountDto(x.Reason, x.Amount, x.Count)).ToList(),
            reasonAmounts.Where(x => x.Sentiment == SpendingSentiment.Negative)
                .OrderByDescending(x => x.Amount).Select(x => new SpendingReasonAmountDto(x.Reason, x.Amount, x.Count)).ToList());
    }

    private IQueryable<FullWorth.Backend.Modules.Transactions.FinanceTransaction> AccessibleOutgoingTransactions(Guid userId, Guid fullWorthSpaceId) =>
        db.Transactions.AsNoTracking().Where(t =>
            t.Amount < 0m && !t.IsIgnored && !t.IsTransfer &&
            db.Accounts.Any(a =>
                a.Id == t.AccountId && a.FullWorthSpaceId == fullWorthSpaceId &&
                db.FullWorthSpaceMembers.Any(m => m.FullWorthSpaceId == fullWorthSpaceId && m.UserId == userId) &&
                a.Owners.Any(o => o.UserId == userId)));

    internal IQueryable<FullWorth.Backend.Modules.Transactions.FinanceTransaction> AccessibleTransactions(Guid userId, Guid fullWorthSpaceId) =>
        db.Transactions.AsNoTracking().Where(t =>
            db.Accounts.Any(a =>
                a.Id == t.AccountId && a.FullWorthSpaceId == fullWorthSpaceId &&
                db.FullWorthSpaceMembers.Any(m => m.FullWorthSpaceId == fullWorthSpaceId && m.UserId == userId) &&
                a.Owners.Any(o => o.UserId == userId)));

    private Task<bool> CanAccessTransactionAsync(Guid userId, Guid fullWorthSpaceId, Guid transactionId, CancellationToken ct) =>
        AccessibleTransactions(userId, fullWorthSpaceId).AnyAsync(x => x.Id == transactionId, ct);

    private static IReadOnlyList<string> NormalizeAndValidateReasons(SpendingSentiment sentiment, IReadOnlyList<string>? reasons)
    {
        var normalized = (reasons ?? [])
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (normalized.Count > 5) throw new SpendingReviewValidationException("At most five reasons are allowed.");
        if (!ReasonsBySentiment.TryGetValue(sentiment, out var allowed) || normalized.Any(x => !allowed.Contains(x)))
            throw new SpendingReviewValidationException("One or more reasons do not match the selected sentiment.");
        return normalized;
    }

    private static string? NormalizeNote(string? note)
    {
        if (string.IsNullOrWhiteSpace(note)) return null;
        var normalized = note.Trim();
        if (normalized.Length > 500) throw new SpendingReviewValidationException("Note must not exceed 500 characters.");
        return normalized;
    }

    private static SpendingReviewDto ToDto(SpendingReview review) => new(
        review.Id,
        review.TransactionId,
        review.PurchaseId,
        review.Sentiment,
        DeserializeReasons(review.ReasonsJson),
        review.Note,
        review.CreatedAt,
        review.UpdatedAt);

    internal static IReadOnlyList<string> DeserializeReasons(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<string[]>(json) ?? []; }
        catch (JsonException) { return []; }
    }

    private static (DateOnly From, DateOnly To) NormalizePeriod(DateOnly? from, DateOnly? to)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var start = from ?? new DateOnly(today.Year, today.Month, 1);
        var end = to ?? today;
        if (end < start) throw new SpendingReviewValidationException("The end date must not be before the start date.");
        if (end.DayNumber - start.DayNumber > 3660) throw new SpendingReviewValidationException("Review summary range is too large.");
        return (start, end);
    }

    private static IReadOnlyList<WorthItGroupDto> BuildGroups(
        IReadOnlyList<AnalyticsRow> rows,
        Func<AnalyticsRow, string> key,
        Func<AnalyticsRow, string> label)
    {
        return rows.GroupBy(key, StringComparer.OrdinalIgnoreCase).Select(group =>
        {
            var total = group.Sum(x => x.Amount);
            var reviewedRows = group.Where(x => x.Sentiment.HasValue).ToList();
            var reviewed = reviewedRows.Sum(x => x.Amount);
            var positive = reviewedRows.Where(x => x.Sentiment == SpendingSentiment.Positive).Sum(x => x.Amount);
            var neutral = reviewedRows.Where(x => x.Sentiment == SpendingSentiment.Neutral).Sum(x => x.Amount);
            var negative = reviewedRows.Where(x => x.Sentiment == SpendingSentiment.Negative).Sum(x => x.Amount);
            return new WorthItGroupDto(
                group.Key,
                label(group.First()),
                total,
                reviewed,
                positive,
                neutral,
                negative,
                SafeRatio(reviewed, total),
                reviewed > 0m ? (positive - negative) / reviewed : null,
                reviewedRows.Count);
        }).ToList();
    }

    internal static bool IsRepresentative(WorthItGroupDto group) =>
        group.ReviewedTransactions >= 2 && (group.ReviewCoverage >= 0.15m || group.ReviewedAmount >= 25m);

    internal static decimal OpportunityScore(WorthItGroupDto group) =>
        group.NegativeAmount * (1m + SafeRatio(group.NegativeAmount, group.ReviewedAmount)) - group.PositiveAmount * 0.35m;

    private static decimal SafeRatio(decimal numerator, decimal denominator) => denominator <= 0m ? 0m : numerator / denominator;
    private static string NormalizeMerchantKey(string value) => value.Trim().ToUpperInvariant();

    private sealed record ReviewRow(Guid TransactionId, SpendingSentiment Sentiment, string ReasonsJson);

    private sealed class AnalyticsRow
    {
        public Guid TransactionId { get; init; }
        public decimal Amount { get; set; }
        public string Currency { get; init; } = "EUR";
        public DateOnly Date { get; init; }
        public SpendingSentiment? Sentiment { get; set; }
        public string ReasonsJson { get; set; } = "[]";
        public string CategoryKey { get; init; } = string.Empty;
        public string CategoryLabel { get; init; } = string.Empty;
        public string MerchantLabel { get; init; } = string.Empty;
    }
}
