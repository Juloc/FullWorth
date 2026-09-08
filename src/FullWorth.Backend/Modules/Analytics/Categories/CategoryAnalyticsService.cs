using FullWorth.Backend.Data;
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

public sealed record CategoryAnalyticsResult(int Year, int Month, string Currency, List<CategoryAnalyticsItem> Categories, bool Incomplete, DateOnly? From = null, DateOnly? To = null, string Granularity = "month");

/// <summary>
/// Category spend analytics for a month: current vs previous period, trailing 3/6/12-month averages,
/// absolute/percentage trend, and a hierarchical roll-up so every category reports its own spend plus
/// all descendants'. Spend is allocated via <see cref="ExpenseAllocationBuilder"/>, so confirmed
/// purchase item splits are used without double counting the parent transaction.
/// </summary>
public sealed class CategoryAnalyticsService(FullWorthDbContext db, FullWorth.Backend.Modules.Fx.CurrencyConverter fx)
{
    // Uncategorized spend is keyed by Guid.Empty internally (a null Guid? cannot be a dictionary key)
    // and surfaced as CategoryId = null at the DTO boundary.
    private static readonly Guid Uncategorized = Guid.Empty;

    public async Task<CategoryAnalyticsResult?> CategorySpendForUserAsync(
        Guid userId, Guid fullWorthSpaceId, int year, int month, string currency, CancellationToken ct)
    {
        if (!await db.FullWorthSpaceMembers.AsNoTracking().AnyAsync(m => m.FullWorthSpaceId == fullWorthSpaceId && m.UserId == userId, ct))
            return null;

        currency = NormalizeCurrency(currency);
        var currentStart = new DateOnly(year, month, 1);
        var currentEnd = currentStart.AddMonths(1).AddDays(-1);
        var windowStart = currentStart.AddMonths(-12);
        var currentKey = MonthKey(year, month);

        // Booked, non-ignored, non-transfer expenses the caller can see, across the trailing window.
        // §18: include foreign currencies and convert each transaction's spend to the base currency at
        // its booking-date rate; a missing rate marks the result incomplete and drops that allocation.
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
                    account.Owners.Any(owner => owner.UserId == userId)))
            .Select(transaction => new { transaction.Id, Date = transaction.BookingDate!.Value, transaction.Amount, transaction.Currency, transaction.CategoryId })
            .ToListAsync(ct);

        var monthByTransaction = rows.ToDictionary(row => row.Id, row => MonthKey(row.Date.Year, row.Date.Month));
        // The builder returns base-currency allocations (foreign spend + linked refunds converted at their
        // own value dates) and flags incomplete when a rate was missing.
        var (allocations, incomplete) = await new ExpenseAllocationBuilder(db).BuildAsync(
            fullWorthSpaceId,
            rows.Select(row => new ExpenseTx(row.Id, row.Amount, row.CategoryId, row.Currency, row.Date)).ToList(),
            fx, currency, ct);

        // monthly[categoryKey][monthKey] = spend (in base currency); Guid.Empty = uncategorized.
        var monthly = new Dictionary<Guid, Dictionary<int, decimal>>();
        var itemBreakdownCurrent = new HashSet<Guid>();
        foreach (var allocation in allocations)
        {
            if (!monthByTransaction.TryGetValue(allocation.TransactionId, out var monthKey)) continue;
            var key = allocation.CategoryId ?? Uncategorized;
            if (!monthly.TryGetValue(key, out var buckets))
                monthly[key] = buckets = new Dictionary<int, decimal>();
            buckets[monthKey] = buckets.GetValueOrDefault(monthKey) + allocation.Amount;
            if (monthKey == currentKey && allocation.FromPurchaseItem)
                itemBreakdownCurrent.Add(key);
        }

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

        var items = new List<CategoryAnalyticsItem>();
        foreach (var category in categories)
        {
            var subtree = Subtree(category.Id, childrenByParent);
            var item = BuildItem(category.Id, nameById[category.Id], parentById[category.Id], subtree, monthly, itemBreakdownCurrent, currentKey);
            if (item is not null) items.Add(item);
        }

        // Uncategorized spend has no place in the tree; surface it as its own row (CategoryId = null).
        var uncategorized = BuildItem(null, "Uncategorized", null, [Uncategorized], monthly, itemBreakdownCurrent, currentKey);
        if (uncategorized is not null) items.Add(uncategorized);

        items = items.OrderByDescending(item => item.Current).ThenBy(item => item.Name).ToList();
        return new CategoryAnalyticsResult(year, month, currency, items, incomplete);
    }

    /// <summary>
    /// Arbitrary-window category analytics used by the consumer analysis cycles. The comparison is the
    /// immediately preceding window with exactly the same number of days. Account/group scope is applied
    /// inside the authorization query so chart totals reconcile with transaction drill-downs.
    /// </summary>
    public async Task<CategoryAnalyticsResult?> CategorySpendForRangeForUserAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        DateOnly from,
        DateOnly to,
        string granularity,
        string currency,
        Guid? accountId,
        Guid? accountGroupId,
        CancellationToken ct)
    {
        if (!await db.FullWorthSpaceMembers.AsNoTracking().AnyAsync(
                member => member.FullWorthSpaceId == fullWorthSpaceId && member.UserId == userId, ct))
            return null;

        if (to < from) (from, to) = (to, from);
        currency = NormalizeCurrency(currency);
        granularity = NormalizeGranularity(granularity);

        var windows = Enumerable.Range(0, 13)
            .Select(offset => ShiftWindow(from, to, granularity, offset))
            .ToArray();
        var historyStart = windows[^1].From;

        var rows = await db.Transactions.AsNoTracking()
            .Where(transaction =>
                transaction.Amount < 0 &&
                !transaction.IsIgnored &&
                !transaction.IsTransfer &&
                transaction.Status != "PDNG" &&
                transaction.BookingDate != null &&
                transaction.BookingDate >= historyStart &&
                transaction.BookingDate <= to &&
                db.Accounts.Any(account =>
                    account.Id == transaction.AccountId &&
                    account.FullWorthSpaceId == fullWorthSpaceId &&
                    (!accountId.HasValue || account.Id == accountId.Value) &&
                    (!accountGroupId.HasValue || account.GroupId == accountGroupId.Value) &&
                    account.Owners.Any(owner => owner.UserId == userId)))
            .Select(transaction => new
            {
                transaction.Id,
                Date = transaction.BookingDate!.Value,
                transaction.Amount,
                transaction.Currency,
                transaction.CategoryId
            })
            .ToListAsync(ct);

        var dateByTransaction = rows.ToDictionary(row => row.Id, row => row.Date);
        var (allocations, incomplete) = await new ExpenseAllocationBuilder(db).BuildAsync(
            fullWorthSpaceId,
            rows.Select(row => new ExpenseTx(row.Id, row.Amount, row.CategoryId, row.Currency, row.Date)).ToList(),
            fx, currency, ct);

        // bucket[0] is the selected active window; bucket[1..12] are completed predecessor windows.
        // Average3/6/12 therefore never include a running current bucket.
        var spendByWindow = Enumerable.Range(0, 13)
            .Select(_ => new Dictionary<Guid, decimal>())
            .ToArray();
        var itemBreakdownCurrent = new HashSet<Guid>();

        foreach (var allocation in allocations)
        {
            if (!dateByTransaction.TryGetValue(allocation.TransactionId, out var date)) continue;
            var index = Array.FindIndex(windows, window => date >= window.From && date <= window.To);
            if (index < 0) continue;

            var key = allocation.CategoryId ?? Uncategorized;
            var target = spendByWindow[index];
            target[key] = target.GetValueOrDefault(key) + allocation.Amount;
            if (index == 0 && allocation.FromPurchaseItem)
                itemBreakdownCurrent.Add(key);
        }

        var categories = await db.Categories.AsNoTracking()
            .Where(category => category.FullWorthSpaceId == fullWorthSpaceId)
            .Select(category => new { category.Id, category.ParentId, category.Name })
            .ToListAsync(ct);
        var parentById = categories.ToDictionary(category => category.Id, category => category.ParentId);
        var childrenByParent = categories
            .Where(category => category.ParentId.HasValue)
            .GroupBy(category => category.ParentId!.Value)
            .ToDictionary(group => group.Key, group => group.Select(category => category.Id).ToList());

        decimal Rollup(IReadOnlyCollection<Guid> subtree, Dictionary<Guid, decimal> source) =>
            subtree.Sum(categoryId => source.GetValueOrDefault(categoryId));

        decimal Average(IReadOnlyCollection<Guid> subtree, int periods)
        {
            var total = 0m;
            for (var index = 1; index <= periods; index++)
                total += Rollup(subtree, spendByWindow[index]);
            return total / periods;
        }

        CategoryAnalyticsItem? BuildRangeItem(Guid? categoryId, string name, Guid? parentId, IReadOnlyCollection<Guid> subtree)
        {
            var currentSpend = Rollup(subtree, spendByWindow[0]);
            var previousSpend = Rollup(subtree, spendByWindow[1]);
            var average3 = Average(subtree, 3);
            var average6 = Average(subtree, 6);
            var average12 = Average(subtree, 12);

            if (currentSpend == 0m && previousSpend == 0m && average3 == 0m && average6 == 0m && average12 == 0m)
                return null;

            var trend = currentSpend - previousSpend;
            var trendPercent = previousSpend == 0m
                ? (currentSpend == 0m ? 0m : 100m)
                : trend / previousSpend * 100m;

            return new CategoryAnalyticsItem(
                categoryId,
                name,
                parentId,
                Round(currentSpend),
                Round(previousSpend),
                Round(average3),
                Round(average6),
                Round(average12),
                Round(trend),
                Round(trendPercent),
                subtree.Any(itemBreakdownCurrent.Contains));
        }

        var items = new List<CategoryAnalyticsItem>();
        foreach (var category in categories)
        {
            var subtree = Subtree(category.Id, childrenByParent);
            var item = BuildRangeItem(category.Id, category.Name, parentById[category.Id], subtree);
            if (item is not null) items.Add(item);
        }

        var uncategorized = BuildRangeItem(null, "Uncategorized", null, [Uncategorized]);
        if (uncategorized is not null) items.Add(uncategorized);

        items = items.OrderByDescending(item => item.Current).ThenBy(item => item.Name).ToList();
        return new CategoryAnalyticsResult(from.Year, from.Month, currency, items, incomplete, from, to, granularity);
    }

    private static (DateOnly From, DateOnly To) ShiftWindow(DateOnly from, DateOnly to, string granularity, int offset)
    {
        if (offset == 0) return (from, to);

        var exactMonth = granularity == "month" &&
            from.Day == 1 &&
            to == from.AddMonths(1).AddDays(-1);
        var exactQuarter = granularity == "quarter" &&
            from.Day == 1 &&
            (from.Month - 1) % 3 == 0 &&
            to == from.AddMonths(3).AddDays(-1);
        var exactYear = granularity == "year" &&
            from.Month == 1 && from.Day == 1 &&
            to == from.AddYears(1).AddDays(-1);
        var exactWeek = granularity == "week" &&
            to.DayNumber - from.DayNumber == 6;

        if (exactWeek)
            return (from.AddDays(-7 * offset), to.AddDays(-7 * offset));
        if (exactMonth)
            return (from.AddMonths(-offset), to.AddMonths(-offset));
        if (exactQuarter)
            return (from.AddMonths(-3 * offset), to.AddMonths(-3 * offset));
        if (exactYear)
            return (from.AddYears(-offset), to.AddYears(-offset));

        // For genuinely arbitrary ranges keep the existing equal-length-window contract.
        var days = Math.Max(1, to.DayNumber - from.DayNumber + 1);
        var shiftedTo = from.AddDays(-(days * (offset - 1)) - 1);
        var shiftedFrom = shiftedTo.AddDays(-(days - 1));
        return (shiftedFrom, shiftedTo);
    }

    private static string NormalizeGranularity(string? granularity)
    {
        var normalized = (granularity ?? "month").Trim().ToLowerInvariant();
        return normalized is "week" or "month" or "quarter" or "year" ? normalized : "month";
    }

    private static CategoryAnalyticsItem? BuildItem(
        Guid? categoryId, string name, Guid? parentId, IReadOnlyCollection<Guid> subtree,
        Dictionary<Guid, Dictionary<int, decimal>> monthly, HashSet<Guid> itemBreakdownCurrent, int currentKey)
    {
        decimal SpendAt(int monthKey) => subtree.Sum(cat =>
            monthly.TryGetValue(cat, out var buckets) ? buckets.GetValueOrDefault(monthKey) : 0m);

        decimal Average(int months)
        {
            var total = 0m;
            for (var offset = 1; offset <= months; offset++) total += SpendAt(currentKey - offset);
            return total / months;
        }

        var current = SpendAt(currentKey);
        var previous = SpendAt(currentKey - 1);
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

    private static int MonthKey(int year, int month) => year * 12 + (month - 1);

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    private static string NormalizeCurrency(string currency)
    {
        var normalized = string.IsNullOrWhiteSpace(currency) ? "EUR" : currency.Trim().ToUpperInvariant();
        return normalized.Length == 3 && normalized.All(character => character is >= 'A' and <= 'Z') ? normalized : "EUR";
    }
}

public static class CategoryAnalyticsEndpoints
{
    public static IEndpointRouteBuilder MapCategoryAnalyticsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/analytics/categories", async (
            Guid fullWorthSpaceId,
            int? year,
            int? month,
            DateOnly? from,
            DateOnly? to,
            string? granularity,
            string? currency,
            Guid? accountId,
            Guid? accountGroupId,
            CurrentUserContext currentUser,
            CategoryAnalyticsService service,
            CancellationToken ct) =>
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            CategoryAnalyticsResult? result;
            if (from.HasValue || to.HasValue)
            {
                var resolvedTo = to ?? today;
                var resolvedFrom = from ?? resolvedTo.AddMonths(-1).AddDays(1);
                result = await service.CategorySpendForRangeForUserAsync(
                    currentUser.RequireUserId(), fullWorthSpaceId, resolvedFrom, resolvedTo,
                    granularity ?? "month", currency ?? "EUR", accountId, accountGroupId, ct);
            }
            else
            {
                result = await service.CategorySpendForUserAsync(
                    currentUser.RequireUserId(), fullWorthSpaceId, year ?? today.Year, month ?? today.Month, currency ?? "EUR", ct);
            }
            return result is null ? Results.NotFound() : Results.Ok(result);
        }).WithTags("Analytics");

        return app;
    }
}
