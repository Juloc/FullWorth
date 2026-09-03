using FullWorth.Backend.Data;
using FullWorth.Backend.Security;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Analytics.Merchants;

public sealed record MerchantCategorySlice(Guid? CategoryId, string Name, decimal Amount);

public sealed record MerchantAnalyticsItem(
    string Merchant,
    decimal CurrentSpend,
    int CurrentCount,
    decimal CurrentAverage,
    decimal PreviousSpend,
    decimal TrendAbsolute,
    decimal TrendPercent,
    List<MerchantCategorySlice> Categories);

public sealed record MerchantAnalyticsResult(int Year, int Month, string Currency, int Top, List<MerchantAnalyticsItem> Merchants, bool Incomplete);

/// <summary>
/// Merchant spend analytics for a month, keyed by normalized counterparty: spend, visit/transaction
/// count, average transaction, current vs previous period, and — for the top merchants — a category
/// distribution via <see cref="ExpenseAllocationBuilder"/> (confirmed purchase item splits, no double
/// counting). Merchant spend itself is transaction-level, so counts and totals never double count.
/// </summary>
public sealed class MerchantAnalyticsService(FullWorthDbContext db, FullWorth.Backend.Modules.Fx.CurrencyConverter fx)
{
    public async Task<MerchantAnalyticsResult?> MerchantSpendForUserAsync(
        Guid userId, Guid fullWorthSpaceId, int year, int month, string currency, int top, CancellationToken ct)
    {
        if (!await db.FullWorthSpaceMembers.AsNoTracking().AnyAsync(m => m.FullWorthSpaceId == fullWorthSpaceId && m.UserId == userId, ct))
            return null;

        currency = NormalizeCurrency(currency);
        top = Math.Clamp(top, 1, 100);
        var currentStart = new DateOnly(year, month, 1);
        var currentEnd = currentStart.AddMonths(1).AddDays(-1);
        var previousStart = currentStart.AddMonths(-1);
        var previousEnd = currentStart.AddDays(-1);

        var current = await ExpensesAsync(userId, fullWorthSpaceId, currentStart, currentEnd, ct);
        var previous = await ExpensesAsync(userId, fullWorthSpaceId, previousStart, previousEnd, ct);

        // One FX snapshot spans both windows; every merchant's spend is converted to the base currency at
        // each transaction's booking-date rate. A row with no rate is dropped (from spend AND count) and
        // marks the result incomplete — never assumed 1:1.
        var acc = new FullWorth.Backend.Modules.Fx.FxAccumulator(await fx.PrepareAsync(currency, previousStart, currentEnd, ct));
        decimal? BaseSpend(ExpenseRow row) => acc.Convert(Math.Abs(row.Amount), row.Currency, row.Date);

        var previousByMerchant = new Dictionary<string, decimal>();
        foreach (var row in previous)
        {
            var converted = BaseSpend(row);
            if (!converted.HasValue) continue;
            var key = MerchantKey(row.Normalized, row.Counterparty);
            previousByMerchant[key] = previousByMerchant.GetValueOrDefault(key) + converted.Value;
        }

        // Base spend per convertible current row; unconvertible rows are excluded from spend and count.
        var currentBaseById = new Dictionary<Guid, decimal>();
        foreach (var row in current)
        {
            var converted = BaseSpend(row);
            if (converted.HasValue) currentBaseById[row.Id] = converted.Value;
        }
        var currentByMerchant = current.Where(row => currentBaseById.ContainsKey(row.Id))
            .GroupBy(row => MerchantKey(row.Normalized, row.Counterparty))
            .Select(group => new
            {
                Merchant = group.Key,
                Spend = group.Sum(row => currentBaseById[row.Id]),
                Count = group.Count(),
                TxIds = group.Select(row => row.Id).ToList()
            })
            .OrderByDescending(merchant => merchant.Spend)
            .ThenBy(merchant => merchant.Merchant)
            .Take(top)
            .ToList();

        // Category distribution only for the merchants we actually return, to bound the work.
        var topTxIds = currentByMerchant.SelectMany(merchant => merchant.TxIds).ToHashSet();
        var merchantByTx = current.Where(row => topTxIds.Contains(row.Id))
            .ToDictionary(row => row.Id, row => MerchantKey(row.Normalized, row.Counterparty));
        // The builder returns base-currency allocations (foreign spend + linked refunds converted at their
        // own value dates) and flags incomplete when a rate was missing.
        var (allocations, distributionIncomplete) = await new ExpenseAllocationBuilder(db).BuildAsync(
            fullWorthSpaceId,
            current.Where(row => topTxIds.Contains(row.Id)).Select(row => new ExpenseTx(row.Id, row.Amount, row.CategoryId, row.Currency, row.Date)).ToList(),
            fx, currency, ct);
        var categoryNames = await db.Categories.AsNoTracking()
            .Where(category => category.FullWorthSpaceId == fullWorthSpaceId)
            .ToDictionaryAsync(category => category.Id, category => category.Name, ct);

        // merchant -> categoryId -> amount in base currency (Guid.Empty = uncategorized).
        var distribution = new Dictionary<string, Dictionary<Guid, decimal>>();
        foreach (var allocation in allocations)
        {
            if (!merchantByTx.TryGetValue(allocation.TransactionId, out var merchant)) continue;
            var categoryKey = allocation.CategoryId ?? Guid.Empty;
            if (!distribution.TryGetValue(merchant, out var slices))
                distribution[merchant] = slices = new Dictionary<Guid, decimal>();
            slices[categoryKey] = slices.GetValueOrDefault(categoryKey) + allocation.Amount;
        }

        var items = currentByMerchant.Select(merchant =>
        {
            var previousSpend = previousByMerchant.GetValueOrDefault(merchant.Merchant);
            var trendAbsolute = merchant.Spend - previousSpend;
            var trendPercent = previousSpend == 0m
                ? (merchant.Spend == 0m ? 0m : 100m)
                : trendAbsolute / previousSpend * 100m;
            var categories = distribution.TryGetValue(merchant.Merchant, out var slices)
                ? slices.Select(slice => new MerchantCategorySlice(
                        slice.Key == Guid.Empty ? null : slice.Key,
                        slice.Key != Guid.Empty && categoryNames.TryGetValue(slice.Key, out var name) ? name : "Uncategorized",
                        Round(slice.Value)))
                    .OrderByDescending(slice => slice.Amount).ToList()
                : [];
            return new MerchantAnalyticsItem(
                merchant.Merchant,
                Round(merchant.Spend),
                merchant.Count,
                Round(merchant.Count == 0 ? 0m : merchant.Spend / merchant.Count),
                Round(previousSpend),
                Round(trendAbsolute),
                Round(trendPercent),
                categories);
        }).ToList();

        return new MerchantAnalyticsResult(year, month, currency, top, items, acc.Incomplete || distributionIncomplete);
    }

    // §18: no base-currency filter — foreign rows are carried with their currency + booking date so the
    // caller can convert each to the base currency at its value-date rate.
    private async Task<List<ExpenseRow>> ExpensesAsync(
        Guid userId, Guid fullWorthSpaceId, DateOnly from, DateOnly to, CancellationToken ct) =>
        await db.Transactions.AsNoTracking()
            .Where(transaction =>
                transaction.Amount < 0 &&
                !transaction.IsIgnored &&
                !transaction.IsTransfer &&
                transaction.Status != "PDNG" &&
                transaction.BookingDate != null &&
                transaction.BookingDate >= from &&
                transaction.BookingDate <= to &&
                db.Accounts.Any(account =>
                    account.Id == transaction.AccountId &&
                    account.FullWorthSpaceId == fullWorthSpaceId &&
                    account.Owners.Any(owner => owner.UserId == userId)))
            .Select(transaction => new ExpenseRow(transaction.Id, transaction.Amount, transaction.CategoryId, transaction.NormalizedCounterparty, transaction.Counterparty, transaction.Currency, transaction.BookingDate!.Value))
            .ToListAsync(ct);

    private static string MerchantKey(string? normalized, string? counterparty) =>
        !string.IsNullOrWhiteSpace(normalized) ? normalized.Trim()
        : !string.IsNullOrWhiteSpace(counterparty) ? counterparty.Trim()
        : "Unknown";

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    private static string NormalizeCurrency(string currency)
    {
        var normalized = string.IsNullOrWhiteSpace(currency) ? "EUR" : currency.Trim().ToUpperInvariant();
        return normalized.Length == 3 && normalized.All(character => character is >= 'A' and <= 'Z') ? normalized : "EUR";
    }

    private sealed record ExpenseRow(Guid Id, decimal Amount, Guid? CategoryId, string? Normalized, string? Counterparty, string Currency, DateOnly Date);
}

public static class MerchantAnalyticsEndpoints
{
    public static IEndpointRouteBuilder MapMerchantAnalyticsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/analytics/merchants", async (
            Guid fullWorthSpaceId, int? year, int? month, string? currency, int? top,
            CurrentUserContext currentUser, MerchantAnalyticsService service, CancellationToken ct) =>
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            var result = await service.MerchantSpendForUserAsync(
                currentUser.RequireUserId(), fullWorthSpaceId, year ?? today.Year, month ?? today.Month, currency ?? "EUR", top ?? 10, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        }).WithTags("Analytics");

        return app;
    }
}
