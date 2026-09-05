using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Merchants;
using FullWorth.Backend.Security;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Analytics.Merchants;

public sealed record MerchantCategorySlice(Guid? CategoryId, string Name, decimal Amount);

public sealed record MerchantAnalyticsItem(
    string Merchant,
    Guid? MerchantId,
    string? BrandKey,
    string? LogoAssetPath,
    decimal CurrentSpend,
    int CurrentCount,
    decimal CurrentAverage,
    decimal PreviousSpend,
    decimal TrendAbsolute,
    decimal TrendPercent,
    List<MerchantCategorySlice> Categories);

public sealed record MerchantAnalyticsResult(
    int Year,
    int Month,
    string Currency,
    int Top,
    List<MerchantAnalyticsItem> Merchants,
    bool Incomplete,
    DateOnly From,
    DateOnly To,
    string Granularity,
    string ComparisonMode);

/// <summary>
/// Merchant spend analytics for the SELECTED period (§6), keyed by normalized counterparty: spend,
/// visit/transaction count, average transaction, current vs comparison window, and — for the top
/// merchants — a category distribution via <see cref="ExpenseAllocationBuilder"/> (confirmed purchase-item
/// splits, no double counting). The period is whatever the caller asked for (never silently the current
/// month). Optional account / account-group / category / merchant scope is resolved and enforced
/// server-side against the caller's accessible accounts. Each returned merchant also carries its resolved
/// registry id + brand so the browser does not re-run normalization.
/// </summary>
public sealed class MerchantAnalyticsService(FullWorthDbContext db, FullWorth.Backend.Modules.Fx.CurrencyConverter fx)
{
    public async Task<MerchantAnalyticsResult?> MerchantSpendForUserAsync(Guid userId, Guid fullWorthSpaceId, AnalyticsQuery query, int top, CancellationToken ct)
    {
        if (!await db.FullWorthSpaceMembers.AsNoTracking().AnyAsync(m => m.FullWorthSpaceId == fullWorthSpaceId && m.UserId == userId, ct))
            return null;

        var currency = query.Currency;
        top = Math.Clamp(top, 1, 100);
        var comparison = query.ComparisonWindow();

        // Optional category scope (transaction-level subtree): restrict to merchants where the caller
        // spent in that category. A missing/foreign category yields an empty (valid) report.
        HashSet<Guid>? categoryScope = null;
        if (query.CategoryId is { } scopeCategory)
        {
            if (!await db.Categories.AsNoTracking().AnyAsync(c => c.Id == scopeCategory && c.FullWorthSpaceId == fullWorthSpaceId, ct))
                return Empty(query, top);
            categoryScope = await CategorySubtreeAsync(fullWorthSpaceId, scopeCategory, query.IncludeCategoryDescendants, ct);
        }

        if (query.MerchantId is { } scopeMerchant &&
            !await db.Set<Merchant>().AsNoTracking().AnyAsync(m => m.Id == scopeMerchant && m.FullWorthSpaceId == fullWorthSpaceId, ct))
            return Empty(query, top);

        var resolver = await MerchantBrandResolver.ForSpaceAsync(db, fullWorthSpaceId, ct);

        var current = await ExpensesAsync(userId, fullWorthSpaceId, query, categoryScope, query.From, query.To, ct);
        var previous = await ExpensesAsync(userId, fullWorthSpaceId, query, categoryScope, comparison.Start, comparison.End, ct);

        // Merchant scope: keep only rows that resolve to the requested registry merchant.
        if (query.MerchantId is { } merchantFilter)
        {
            current = current.Where(r => resolver.Resolve(r.Counterparty, r.Normalized).MerchantId == merchantFilter).ToList();
            previous = previous.Where(r => resolver.Resolve(r.Counterparty, r.Normalized).MerchantId == merchantFilter).ToList();
        }

        // One FX snapshot spans both windows; every merchant's spend is converted to the base currency at
        // each transaction's booking-date rate. A row with no rate is dropped (from spend AND count) and
        // marks the result incomplete — never assumed 1:1.
        var snapshotStart = Min(comparison.Start, query.From);
        var snapshotEnd = query.To;
        var acc = new FullWorth.Backend.Modules.Fx.FxAccumulator(await fx.PrepareAsync(currency, snapshotStart, snapshotEnd, ct));
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
                TxIds = group.Select(row => row.Id).ToList(),
                Sample = group.First()
            })
            .OrderByDescending(merchant => merchant.Spend)
            .ThenBy(merchant => merchant.Merchant)
            .Take(top)
            .ToList();

        // Category distribution only for the merchants we actually return, to bound the work.
        var topTxIds = currentByMerchant.SelectMany(merchant => merchant.TxIds).ToHashSet();
        var merchantByTx = current.Where(row => topTxIds.Contains(row.Id))
            .ToDictionary(row => row.Id, row => MerchantKey(row.Normalized, row.Counterparty));
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
            var identity = resolver.Resolve(merchant.Sample.Counterparty, merchant.Sample.Normalized);
            return new MerchantAnalyticsItem(
                merchant.Merchant,
                identity.MerchantId,
                identity.BrandKey,
                identity.LogoAssetPath,
                Round(merchant.Spend),
                merchant.Count,
                Round(merchant.Count == 0 ? 0m : merchant.Spend / merchant.Count),
                Round(previousSpend),
                Round(trendAbsolute),
                Round(trendPercent),
                categories);
        }).ToList();

        return new MerchantAnalyticsResult(query.From.Year, query.From.Month, currency, top, items,
            acc.Incomplete || distributionIncomplete, query.From, query.To, query.NormalizedGranularity, query.NormalizedComparison);
    }

    private static MerchantAnalyticsResult Empty(AnalyticsQuery query, int top) =>
        new(query.From.Year, query.From.Month, query.Currency, top, [], false,
            query.From, query.To, query.NormalizedGranularity, query.NormalizedComparison);

    // §18: no base-currency filter — foreign rows are carried with their currency + booking date so the
    // caller can convert each to the base currency at its value-date rate. Account/group and (optional)
    // category scope are enforced against ACCESSIBLE accounts in the active space.
    private async Task<List<ExpenseRow>> ExpensesAsync(
        Guid userId, Guid fullWorthSpaceId, AnalyticsQuery query, HashSet<Guid>? categoryScope, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var rows = await db.Transactions.AsNoTracking()
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
                    (!query.AccountId.HasValue || account.Id == query.AccountId.Value) &&
                    (!query.AccountGroupId.HasValue || account.GroupId == query.AccountGroupId.Value) &&
                    account.Owners.Any(owner => owner.UserId == userId)))
            .Select(transaction => new ExpenseRow(transaction.Id, transaction.Amount, transaction.CategoryId, transaction.NormalizedCounterparty, transaction.Counterparty, transaction.Currency, transaction.BookingDate!.Value))
            .ToListAsync(ct);

        if (categoryScope is not null)
            rows = rows.Where(row => row.CategoryId.HasValue && categoryScope.Contains(row.CategoryId.Value)).ToList();
        return rows;
    }

    private async Task<HashSet<Guid>> CategorySubtreeAsync(Guid fullWorthSpaceId, Guid root, bool includeDescendants, CancellationToken ct)
    {
        if (!includeDescendants) return [root];
        var pairs = await db.Categories.AsNoTracking()
            .Where(category => category.FullWorthSpaceId == fullWorthSpaceId)
            .Select(category => new { category.Id, category.ParentId })
            .ToListAsync(ct);
        var childrenByParent = pairs.Where(p => p.ParentId.HasValue)
            .GroupBy(p => p.ParentId!.Value)
            .ToDictionary(g => g.Key, g => g.Select(p => p.Id).ToList());
        var result = new HashSet<Guid>();
        var stack = new Stack<Guid>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (!result.Add(current)) continue;
            if (childrenByParent.TryGetValue(current, out var children))
                foreach (var child in children) stack.Push(child);
        }
        return result;
    }

    private static string MerchantKey(string? normalized, string? counterparty) =>
        !string.IsNullOrWhiteSpace(normalized) ? normalized.Trim()
        : !string.IsNullOrWhiteSpace(counterparty) ? counterparty.Trim()
        : "Unknown";

    private static DateOnly Min(DateOnly a, DateOnly b) => a < b ? a : b;

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    private sealed record ExpenseRow(Guid Id, decimal Amount, Guid? CategoryId, string? Normalized, string? Counterparty, string Currency, DateOnly Date);
}

public static class MerchantAnalyticsEndpoints
{
    public static IEndpointRouteBuilder MapMerchantAnalyticsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/analytics/merchants", async (
            Guid fullWorthSpaceId, int? year, int? month, DateOnly? from, DateOnly? to, string? granularity,
            Guid? accountId, Guid? accountGroupId, Guid? categoryId, bool? includeDescendants, Guid? merchantId,
            string? comparison, string? currency, int? top,
            CurrentUserContext currentUser, MerchantAnalyticsService service, CancellationToken ct) =>
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            var query = AnalyticsQuery.Create(from, to, granularity, year, month, accountId, accountGroupId,
                categoryId, includeDescendants, merchantId, comparison, currency, today);
            var result = await service.MerchantSpendForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, query, top ?? 10, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        }).WithTags("Analytics");

        return app;
    }
}
