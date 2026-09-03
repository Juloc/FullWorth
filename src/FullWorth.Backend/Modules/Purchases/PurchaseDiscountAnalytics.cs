using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Fx;
using FullWorth.Backend.Security;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Purchases;

public sealed class PurchaseDiscountAnalyticsPurchaseRow
{
    public Guid Id { get; set; }
    public string Merchant { get; set; } = string.Empty;
    public decimal DiscountAmount { get; set; }
    public string Currency { get; set; } = "EUR";
    public DateOnly? PurchaseDate { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class PurchaseDiscountAnalyticsTypeRow
{
    public Guid PurchaseId { get; set; }
    public string Type { get; set; } = "other";
    public decimal Amount { get; set; }
}

public sealed class PurchaseDiscountAnalyticsProductRow
{
    public Guid PurchaseId { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal Amount { get; set; }
}

public sealed class PurchaseDiscountAnalyticsCategoryRow
{
    public Guid PurchaseId { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal Amount { get; set; }
}

public sealed record PurchaseDiscountAnalyticsBreakdown(string Name, decimal Amount);

public sealed record PurchaseDiscountAnalyticsView(
    DateOnly? From,
    DateOnly? To,
    string BaseCurrency,
    bool Incomplete,
    int PurchaseCount,
    int PurchasesWithDiscount,
    decimal TotalDiscountAmount,
    decimal ItemDiscountAmount,
    decimal BasketOrUnallocatedDiscountAmount,
    decimal DiscountLineAmount,
    IReadOnlyList<PurchaseDiscountAnalyticsBreakdown> ByMerchant,
    IReadOnlyList<PurchaseDiscountAnalyticsBreakdown> ByType,
    IReadOnlyList<PurchaseDiscountAnalyticsBreakdown> ByProduct,
    IReadOnlyList<PurchaseDiscountAnalyticsBreakdown> ByCategory);

public sealed class PurchaseDiscountAnalyticsService(
    FullWorthDbContext db,
    PurchaseAuthorizationStore authorization)
{
    public async Task<PurchaseDiscountAnalyticsView?> GetAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        DateOnly? from,
        DateOnly? to,
        CancellationToken ct)
    {
        if (!await authorization.IsFullWorthSpaceMemberAsync(userId, fullWorthSpaceId, ct)) return null;
        if (from.HasValue && to.HasValue && from.Value > to.Value)
            throw new ArgumentException("from must be before or equal to to.");

        var baseCurrency = await db.FullWorthSpaces.AsNoTracking()
            .Where(space => space.Id == fullWorthSpaceId)
            .Select(space => space.BaseCurrency)
            .SingleAsync(ct);
        baseCurrency = FxSnapshot.Normalize(baseCurrency);

        // Reuse the authoritative purchase visibility rules. This keeps linked purchases scoped to the
        // user's accessible accounts instead of accidentally turning analytics into a FullWorth-Space leak.
        var visible = await authorization.ListForUserAsync(userId, fullWorthSpaceId, null, null, from, to, ct);
        var ids = visible.Select(x => x.Id).Distinct().ToArray();
        if (ids.Length == 0)
            return new(from, to, baseCurrency, false, 0, 0, 0m, 0m, 0m, 0m, [], [], [], []);

        var purchases = await db.Database.SqlQueryRaw<PurchaseDiscountAnalyticsPurchaseRow>(
            "SELECT \"Id\", \"Merchant\", \"DiscountAmount\", \"Currency\", \"PurchaseDate\", \"CreatedAt\" FROM \"Purchases\" WHERE \"Id\" = ANY ({0})",
            ids).ToListAsync(ct);
        var typeRows = await db.Database.SqlQueryRaw<PurchaseDiscountAnalyticsTypeRow>(
            "SELECT \"PurchaseId\", \"Type\", \"Amount\" FROM \"PurchaseDiscounts\" WHERE \"PurchaseId\" = ANY ({0})",
            ids).ToListAsync(ct);
        var productRows = await db.Database.SqlQueryRaw<PurchaseDiscountAnalyticsProductRow>(
            "SELECT \"PurchaseId\", COALESCE(NULLIF(BTRIM(\"Name\"), ''), 'Unbekannt') AS \"Name\", \"DiscountAmount\" AS \"Amount\" FROM \"PurchaseItems\" WHERE \"PurchaseId\" = ANY ({0}) AND \"DiscountAmount\" > 0",
            ids).ToListAsync(ct);
        var categoryRows = await db.Database.SqlQueryRaw<PurchaseDiscountAnalyticsCategoryRow>(
            "SELECT i.\"PurchaseId\", COALESCE(c.\"Name\", 'Nicht kategorisiert') AS \"Name\", i.\"DiscountAmount\" AS \"Amount\" FROM \"PurchaseItems\" i LEFT JOIN \"Categories\" c ON c.\"Id\" = i.\"CategoryId\" WHERE i.\"PurchaseId\" = ANY ({0}) AND i.\"DiscountAmount\" > 0",
            ids).ToListAsync(ct);

        var context = purchases.ToDictionary(
            purchase => purchase.Id,
            purchase => new PurchaseCurrencyContext(
                FxSnapshot.Normalize(purchase.Currency),
                purchase.PurchaseDate ?? DateOnly.FromDateTime(purchase.CreatedAt.UtcDateTime)));
        var minDate = context.Values.Min(value => value.Date);
        var maxDate = context.Values.Max(value => value.Date);
        var snapshot = await new CurrencyConverter(db).PrepareAsync(baseCurrency, minDate, maxDate, ct);
        var fx = new FxAccumulator(snapshot);

        decimal Convert(Guid purchaseId, decimal amount)
        {
            if (amount <= 0m) return 0m;
            if (!context.TryGetValue(purchaseId, out var value)) return 0m;
            return fx.Convert(amount, value.Currency, value.Date) ?? 0m;
        }

        var convertedPurchases = purchases.Select(purchase =>
        {
            var originalDiscount = Math.Max(0m, purchase.DiscountAmount);
            return new
            {
                purchase.Id,
                Merchant = string.IsNullOrWhiteSpace(purchase.Merchant) ? "Unbekannt" : purchase.Merchant.Trim(),
                OriginalDiscount = originalDiscount,
                Amount = Convert(purchase.Id, originalDiscount)
            };
        }).ToList();
        var convertedTypes = typeRows.Where(row => row.Amount > 0m)
            .Select(row => new { row.Type, Amount = Convert(row.PurchaseId, row.Amount) }).ToList();
        var convertedProducts = productRows.Where(row => row.Amount > 0m)
            .Select(row => new { row.Name, Amount = Convert(row.PurchaseId, row.Amount) }).ToList();
        var convertedCategories = categoryRows.Where(row => row.Amount > 0m)
            .Select(row => new { row.Name, Amount = Convert(row.PurchaseId, row.Amount) }).ToList();

        var total = convertedPurchases.Sum(x => x.Amount);
        var itemTotal = convertedProducts.Sum(x => x.Amount);
        var lineTotal = convertedTypes.Sum(x => x.Amount);
        return new(
            from,
            to,
            baseCurrency,
            fx.Incomplete,
            purchases.Count,
            convertedPurchases.Count(x => x.OriginalDiscount > 0m),
            total,
            itemTotal,
            Math.Max(0m, total - itemTotal),
            lineTotal,
            convertedPurchases.Where(x => x.OriginalDiscount > 0m)
                .GroupBy(x => x.Merchant, StringComparer.OrdinalIgnoreCase)
                .Select(group => new PurchaseDiscountAnalyticsBreakdown(group.Key, group.Sum(x => x.Amount)))
                .OrderByDescending(x => x.Amount).ThenBy(x => x.Name).Take(20).ToList(),
            convertedTypes
                .GroupBy(x => x.Type, StringComparer.OrdinalIgnoreCase)
                .Select(group => new PurchaseDiscountAnalyticsBreakdown(group.Key, group.Sum(x => x.Amount)))
                .OrderByDescending(x => x.Amount).ThenBy(x => x.Name).ToList(),
            convertedProducts
                .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .Select(group => new PurchaseDiscountAnalyticsBreakdown(group.Key, group.Sum(x => x.Amount)))
                .OrderByDescending(x => x.Amount).ThenBy(x => x.Name).Take(30).ToList(),
            convertedCategories
                .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .Select(group => new PurchaseDiscountAnalyticsBreakdown(group.Key, group.Sum(x => x.Amount)))
                .OrderByDescending(x => x.Amount).ThenBy(x => x.Name).Take(30).ToList());
    }

    private sealed record PurchaseCurrencyContext(string Currency, DateOnly Date);
}

public static class PurchaseDiscountAnalyticsEndpoints
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/discount-analytics", GetAsync);
    }

    private static async Task<IResult> GetAsync(
        Guid fullWorthSpaceId,
        DateOnly? from,
        DateOnly? to,
        CurrentUserContext currentUser,
        FullWorthDbContext db,
        PurchaseAuthorizationStore authorization,
        CancellationToken ct)
    {
        try
        {
            var service = new PurchaseDiscountAnalyticsService(db, authorization);
            var result = await service.GetAsync(currentUser.RequireUserId(), fullWorthSpaceId, from, to, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }
}
