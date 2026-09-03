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

public sealed record CategoryAnalyticsResult(int Year, int Month, string Currency, List<CategoryAnalyticsItem> Categories, bool Incomplete);

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
            Guid fullWorthSpaceId, int? year, int? month, string? currency,
            CurrentUserContext currentUser, CategoryAnalyticsService service, CancellationToken ct) =>
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            var result = await service.CategorySpendForUserAsync(
                currentUser.RequireUserId(), fullWorthSpaceId, year ?? today.Year, month ?? today.Month, currency ?? "EUR", ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        }).WithTags("Analytics");

        return app;
    }
}
