using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Merchants;
using FullWorth.Backend.Security;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Analytics.Categories;

/// <summary>Per-category spend with period comparison and trailing averages (subtree-rolled).</summary>
public sealed record CategoryAnalyticsItem(
    Guid? CategoryId,
    string Name,
    Guid? ParentId,
    decimal Current,
    decimal Previous,
    decimal Average3,
    decimal Average6,
    decimal Average12,
    decimal TrendAbsolute,
    decimal TrendPercent,
    bool HasItemBreakdown);

public sealed record CategoryAnalyticsResult(
    int Year,
    int Month,
    string Currency,
    List<CategoryAnalyticsItem> Categories,
    bool Incomplete,
    DateOnly From,
    DateOnly To,
    string Granularity,
    string ComparisonMode);

/// <summary>
/// Category spend analytics for the SELECTED period (§6): current window vs the comparison window, plus
/// trailing 3/6/12-period averages stepped by the query granularity, an absolute/percentage trend, and a
/// hierarchical roll-up so every category reports its own spend plus all descendants'. The period is
/// whatever the caller asked for (a past quarter returns that quarter — never silently the current month).
/// Optional account / account-group / category / merchant scope is resolved and enforced server-side.
/// Spend is allocated via <see cref="ExpenseAllocationBuilder"/> so confirmed purchase-item splits are
/// used without double counting the parent transaction.
/// </summary>
public sealed class CategoryAnalyticsService(FullWorthDbContext db, FullWorth.Backend.Modules.Fx.CurrencyConverter fx)
{
    // Uncategorized spend is keyed by Guid.Empty internally (a null Guid? cannot be a dictionary key)
    // and surfaced as CategoryId = null at the DTO boundary.
    private static readonly Guid Uncategorized = Guid.Empty;

    public async Task<CategoryAnalyticsResult?> CategorySpendForUserAsync(Guid userId, Guid fullWorthSpaceId, AnalyticsQuery query, CancellationToken ct)
    {
        if (!await db.FullWorthSpaceMembers.AsNoTracking().AnyAsync(m => m.FullWorthSpaceId == fullWorthSpaceId && m.UserId == userId, ct))
            return null;

        var currency = query.Currency;
        var comparison = query.ComparisonWindow();
        // Load a window wide enough for the current period, the comparison period and the trailing 12.
        var windowStart = Min(query.Shifted(12).Start, comparison.Start);
        var currentEnd = query.To;

        // A category scope filter that names a missing/foreign category yields an empty (but valid) report.
        HashSet<Guid>? categoryScope = null;
        if (query.CategoryId is { } scopeCategory)
        {
            if (!await db.Categories.AsNoTracking().AnyAsync(c => c.Id == scopeCategory && c.FullWorthSpaceId == fullWorthSpaceId, ct))
                return Empty(query);
        }

        // Booked, non-ignored, non-transfer expenses the caller can see across the trailing window, with
        // optional account / account-group scope enforced against ACCESSIBLE accounts in the active space.
        var rows = await db.Transactions.AsNoTracking()
            .Where(transaction =>
                transaction.Amount < 0 &&
                !transaction.IsIgnored &&
                !transaction.IsTransfer &&
                transaction.Status != "PDNG" &&
                transaction.BookingDate != null &&
                transaction.BookingDate >= windowStart &&
                transaction.BookingDate <= currentEnd &&
                db.Accounts.Any(account =>
                    account.Id == transaction.AccountId &&
                    account.FullWorthSpaceId == fullWorthSpaceId &&
                    (!query.AccountId.HasValue || account.Id == query.AccountId.Value) &&
                    (!query.AccountGroupId.HasValue || account.GroupId == query.AccountGroupId.Value) &&
                    account.Owners.Any(owner => owner.UserId == userId)))
            .Select(transaction => new { transaction.Id, Date = transaction.BookingDate!.Value, transaction.Amount, transaction.Currency, transaction.CategoryId, transaction.NormalizedCounterparty, transaction.Counterparty })
            .ToListAsync(ct);

        // Merchant scope: keep only rows whose resolved merchant matches. Resolution reuses the shared
        // alias → merchant resolver so it matches the transaction list exactly.
        if (query.MerchantId is { } merchantId)
        {
            if (!await db.Set<Merchant>().AsNoTracking().AnyAsync(m => m.Id == merchantId && m.FullWorthSpaceId == fullWorthSpaceId, ct))
                return Empty(query);
            var resolver = await MerchantBrandResolver.ForSpaceAsync(db, fullWorthSpaceId, ct);
            rows = rows.Where(r => resolver.Resolve(r.Counterparty, r.NormalizedCounterparty).MerchantId == merchantId).ToList();
        }

        var dateByTransaction = rows.ToDictionary(row => row.Id, row => row.Date);
        // The builder returns base-currency allocations (foreign spend + linked refunds converted at their
        // own value dates) and flags incomplete when a rate was missing.
        var (allocations, incomplete) = await new ExpenseAllocationBuilder(db).BuildAsync(
            fullWorthSpaceId,
            rows.Select(row => new ExpenseTx(row.Id, row.Amount, row.CategoryId, row.Currency, row.Date)).ToList(),
            fx, currency, ct);

        var categories = await db.Categories.AsNoTracking()
            .Where(category => category.FullWorthSpaceId == fullWorthSpaceId)
            .Select(category => new { category.Id, category.ParentId, category.Name })
            .ToListAsync(ct);
        var nameById = categories.ToDictionary(category => category.Id, category => category.Name);
        var parentById = categories.ToDictionary(category => category.Id, category => category.ParentId);
        var childrenByParent = categories
            .Where(category => category.ParentId.HasValue)
            .GroupBy(category => category.ParentId!.Value)
            .ToDictionary(group => group.Key, group => group.Select(category => category.Id).ToList());

        // Restrict to the requested category subtree (or that single category) when a category scope is set.
        if (query.CategoryId is { } scoped)
        {
            categoryScope = query.IncludeCategoryDescendants
                ? [.. Subtree(scoped, childrenByParent)]
                : [scoped];
            allocations = allocations.Where(a => a.CategoryId.HasValue && categoryScope.Contains(a.CategoryId.Value)).ToList();
        }

        // Leaf spend per category (Guid.Empty = uncategorized) for each window we report on.
        Dictionary<Guid, decimal> SpendInWindow(DateOnly start, DateOnly end)
        {
            var acc = new Dictionary<Guid, decimal>();
            foreach (var allocation in allocations)
            {
                if (!dateByTransaction.TryGetValue(allocation.TransactionId, out var date)) continue;
                if (date < start || date > end) continue;
                var key = allocation.CategoryId ?? Uncategorized;
                acc[key] = acc.GetValueOrDefault(key) + allocation.Amount;
            }
            return acc;
        }

        var leafCurrent = SpendInWindow(query.From, query.To);
        var leafPrevious = SpendInWindow(comparison.Start, comparison.End);
        var leafShift = new Dictionary<Guid, decimal>[13];
        for (var offset = 1; offset <= 12; offset++)
        {
            var (s, e) = query.Shifted(offset);
            leafShift[offset] = SpendInWindow(s, e);
        }

        var itemBreakdownCurrent = new HashSet<Guid>();
        foreach (var allocation in allocations)
        {
            if (!allocation.FromPurchaseItem) continue;
            if (!dateByTransaction.TryGetValue(allocation.TransactionId, out var date)) continue;
            if (date < query.From || date > query.To) continue;
            itemBreakdownCurrent.Add(allocation.CategoryId ?? Uncategorized);
        }

        var items = new List<CategoryAnalyticsItem>();
        foreach (var category in categories)
        {
            if (categoryScope is not null && !categoryScope.Contains(category.Id)) continue;
            var subtree = Subtree(category.Id, childrenByParent);
            var item = BuildItem(category.Id, nameById[category.Id], parentById[category.Id], subtree, leafCurrent, leafPrevious, leafShift, itemBreakdownCurrent);
            if (item is not null) items.Add(item);
        }

        // Uncategorized spend has no place in the tree; surface it as its own row (CategoryId = null),
        // unless the caller scoped to a specific category.
        if (categoryScope is null)
        {
            var uncategorized = BuildItem(null, "Uncategorized", null, [Uncategorized], leafCurrent, leafPrevious, leafShift, itemBreakdownCurrent);
            if (uncategorized is not null) items.Add(uncategorized);
        }

        items = items.OrderByDescending(item => item.Current).ThenBy(item => item.Name).ToList();
        return new CategoryAnalyticsResult(query.From.Year, query.From.Month, currency, items, incomplete,
            query.From, query.To, query.NormalizedGranularity, query.NormalizedComparison);
    }

    private static CategoryAnalyticsResult Empty(AnalyticsQuery query) =>
        new(query.From.Year, query.From.Month, query.Currency, [], false,
            query.From, query.To, query.NormalizedGranularity, query.NormalizedComparison);

    private static CategoryAnalyticsItem? BuildItem(
        Guid? categoryId, string name, Guid? parentId, IReadOnlyCollection<Guid> subtree,
        Dictionary<Guid, decimal> leafCurrent, Dictionary<Guid, decimal> leafPrevious,
        Dictionary<Guid, decimal>[] leafShift, HashSet<Guid> itemBreakdownCurrent)
    {
        static decimal SubtreeSum(Dictionary<Guid, decimal> source, IReadOnlyCollection<Guid> subtree) =>
            subtree.Sum(category => source.GetValueOrDefault(category));

        decimal Average(int months)
        {
            var total = 0m;
            for (var offset = 1; offset <= months; offset++) total += SubtreeSum(leafShift[offset], subtree);
            return total / months;
        }

        var current = SubtreeSum(leafCurrent, subtree);
        var previous = SubtreeSum(leafPrevious, subtree);
        var average3 = Average(3);
        var average6 = Average(6);
        var average12 = Average(12);

        // Drop rows with no activity anywhere in the window to keep the result focused.
        if (current == 0m && previous == 0m && average12 == 0m && average6 == 0m && average3 == 0m)
            return null;

        var trendAbsolute = current - previous;
        var trendPercent = previous == 0m
            ? (current == 0m ? 0m : 100m)
            : trendAbsolute / previous * 100m;
        var hasItemBreakdown = subtree.Any(itemBreakdownCurrent.Contains);

        return new CategoryAnalyticsItem(
            categoryId, name, parentId,
            Round(current), Round(previous), Round(average3), Round(average6), Round(average12),
            Round(trendAbsolute), Round(trendPercent), hasItemBreakdown);
    }

    /// <summary>A category id plus all of its descendants (cycle-safe).</summary>
    private static List<Guid> Subtree(Guid root, Dictionary<Guid, List<Guid>> childrenByParent)
    {
        var result = new List<Guid>();
        var seen = new HashSet<Guid>();
        var stack = new Stack<Guid>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (!seen.Add(current)) continue;
            result.Add(current);
            if (childrenByParent.TryGetValue(current, out var children))
                foreach (var child in children) stack.Push(child);
        }
        return result;
    }

    private static DateOnly Min(DateOnly a, DateOnly b) => a < b ? a : b;

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}

public static class CategoryAnalyticsEndpoints
{
    public static IEndpointRouteBuilder MapCategoryAnalyticsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/analytics/categories", async (
            Guid fullWorthSpaceId, int? year, int? month, DateOnly? from, DateOnly? to, string? granularity,
            Guid? accountId, Guid? accountGroupId, Guid? categoryId, bool? includeDescendants, Guid? merchantId,
            string? comparison, string? currency,
            CurrentUserContext currentUser, CategoryAnalyticsService service, CancellationToken ct) =>
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            var query = AnalyticsQuery.Create(from, to, granularity, year, month, accountId, accountGroupId,
                categoryId, includeDescendants, merchantId, comparison, currency, today);
            var result = await service.CategorySpendForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, query, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        }).WithTags("Analytics");

        return app;
    }
}
