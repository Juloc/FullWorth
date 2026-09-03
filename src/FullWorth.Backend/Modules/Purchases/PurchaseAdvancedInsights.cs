using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.Fx;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Purchases;

public static class PurchaseAdvancedInsightsEndpoints
{
    public static IEndpointRouteBuilder MapPurchaseAdvancedInsightsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/purchase-analytics").WithTags("Purchases");
        group.MapGet("/personal-inflation", async (
            Guid fullWorthSpaceId,
            DateOnly? from,
            DateOnly? to,
            FullWorth.Backend.Security.CurrentUserContext user,
            FullWorthDbContext db,
            CurrencyConverter currencyConverter,
            CancellationToken ct) =>
        {
            var value = await PersonalInflationAsync(db, currencyConverter, user.RequireUserId(), fullWorthSpaceId, from, to, ct);
            return value is null ? Results.NotFound() : Results.Ok(value);
        });
        group.MapGet("/basket-trend", async (
            Guid fullWorthSpaceId,
            DateOnly? from,
            DateOnly? to,
            FullWorth.Backend.Security.CurrentUserContext user,
            FullWorthDbContext db,
            CurrencyConverter currencyConverter,
            CancellationToken ct) =>
        {
            var value = await BasketTrendAsync(db, currencyConverter, user.RequireUserId(), fullWorthSpaceId, from, to, ct);
            return value is null ? Results.NotFound() : Results.Ok(value);
        });
        group.MapGet("/restock-forecast", async (
            Guid fullWorthSpaceId,
            int? horizonDays,
            FullWorth.Backend.Security.CurrentUserContext user,
            FullWorthDbContext db,
            CancellationToken ct) =>
        {
            var value = await RestockForecastAsync(db, user.RequireUserId(), fullWorthSpaceId, horizonDays ?? 90, ct);
            return value is null ? Results.NotFound() : Results.Ok(value);
        });
        return app;
    }

    private static async Task<object?> PersonalInflationAsync(
        FullWorthDbContext db,
        CurrencyConverter currencyConverter,
        Guid userId,
        Guid fullWorthSpaceId,
        DateOnly? from,
        DateOnly? to,
        CancellationToken ct)
    {
        var baseCurrency = await BaseCurrencyAsync(db, userId, fullWorthSpaceId, ct);
        if (baseCurrency is null) return null;
        var end = to ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var start = from ?? end.AddYears(-1);
        if (start > end) (start, end) = (end, start);

        var rows = await VisibleItems(db, userId, fullWorthSpaceId)
            .Where(x => x.ProductId.HasValue && x.Purchase.ReviewState == "confirmed" && x.Purchase.PurchaseDate >= start && x.Purchase.PurchaseDate <= end)
            .OrderBy(x => x.Purchase.PurchaseDate).ThenBy(x => x.CreatedAt)
            .Select(x => new
            {
                ProductId = x.ProductId!.Value,
                ProductName = x.Product!.CanonicalName,
                x.Product.Brand,
                Date = x.Purchase.PurchaseDate!.Value,
                x.Purchase.Merchant,
                x.Currency,
                x.Quantity,
                x.TotalPrice,
                x.UnitPrice,
                x.BaseUnitPrice,
                x.PackageUnit,
                x.QuantityUnit
            })
            .ToListAsync(ct);

        var snapshot = await currencyConverter.PrepareAsync(baseCurrency, start, end, ct);
        var acc = new FxAccumulator(snapshot);
        var products = new List<InflationProduct>();
        decimal weightedChange = 0m;
        decimal totalWeight = 0m;

        foreach (var group in rows.GroupBy(x => x.ProductId))
        {
            var ordered = group.OrderBy(x => x.Date).ToList();
            if (ordered.Count < 2) continue;
            var first = ordered[0];
            var last = ordered[^1];
            var firstMeasure = PriceMeasure(first.BaseUnitPrice, first.UnitPrice, first.TotalPrice, first.Quantity, first.PackageUnit ?? first.QuantityUnit);
            var lastMeasure = PriceMeasure(last.BaseUnitPrice, last.UnitPrice, last.TotalPrice, last.Quantity, last.PackageUnit ?? last.QuantityUnit);
            if (firstMeasure is null || lastMeasure is null || firstMeasure.Value.Unit != lastMeasure.Value.Unit) continue;
            var firstBase = acc.Convert(firstMeasure.Value.Price, first.Currency, first.Date);
            var lastBase = acc.Convert(lastMeasure.Value.Price, last.Currency, last.Date);
            if (!firstBase.HasValue || !lastBase.HasValue || firstBase.Value <= 0m) continue;
            var change = Math.Round((lastBase.Value / firstBase.Value - 1m) * 100m, 2, MidpointRounding.AwayFromZero);

            decimal weight = 0m;
            foreach (var row in ordered)
            {
                var converted = acc.Convert(Math.Abs(row.TotalPrice), row.Currency, row.Date);
                if (converted.HasValue) weight += converted.Value;
            }
            if (weight <= 0m) weight = 1m;
            weightedChange += change * weight;
            totalWeight += weight;
            products.Add(new InflationProduct(
                group.Key,
                last.ProductName,
                last.Brand,
                first.Date,
                last.Date,
                first.Merchant,
                last.Merchant,
                Math.Round(firstBase.Value, 4, MidpointRounding.AwayFromZero),
                Math.Round(lastBase.Value, 4, MidpointRounding.AwayFromZero),
                firstMeasure.Value.Unit,
                change,
                Math.Round(weight, 2, MidpointRounding.AwayFromZero)));
        }

        var personal = totalWeight > 0m
            ? Math.Round(weightedChange / totalWeight, 2, MidpointRounding.AwayFromZero)
            : (decimal?)null;
        return new
        {
            from = start,
            to = end,
            currency = baseCurrency,
            personalInflationPercent = personal,
            trackedProducts = products.Count,
            incompleteFx = acc.Incomplete,
            methodology = "confirmed_product_price_change_spend_weighted",
            products = products.OrderByDescending(x => Math.Abs(x.ChangePercent)).ThenByDescending(x => x.Weight).Take(100).ToList()
        };
    }

    private static async Task<object?> BasketTrendAsync(
        FullWorthDbContext db,
        CurrencyConverter currencyConverter,
        Guid userId,
        Guid fullWorthSpaceId,
        DateOnly? from,
        DateOnly? to,
        CancellationToken ct)
    {
        var baseCurrency = await BaseCurrencyAsync(db, userId, fullWorthSpaceId, ct);
        if (baseCurrency is null) return null;
        var end = to ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var start = from ?? end.AddMonths(-11).AddDays(1 - end.Day);
        if (start > end) (start, end) = (end, start);

        var rows = await VisiblePurchases(db, userId, fullWorthSpaceId)
            .Where(x => x.ReviewState == "confirmed" && x.PurchaseDate >= start && x.PurchaseDate <= end)
            .Select(x => new
            {
                x.Id,
                Date = x.PurchaseDate!.Value,
                x.TotalAmount,
                x.Currency,
                Savings = x.Discounts.Sum(d => (decimal?)d.Amount) ?? x.DiscountAmount ?? 0m
            })
            .ToListAsync(ct);
        var snapshot = await currencyConverter.PrepareAsync(baseCurrency, start, end, ct);
        var acc = new FxAccumulator(snapshot);
        var converted = new List<ConvertedBasket>();
        foreach (var row in rows)
        {
            var spend = acc.Convert(Math.Abs(row.TotalAmount), row.Currency, row.Date);
            if (!spend.HasValue) continue;
            var savings = acc.Convert(Math.Max(0m, row.Savings), row.Currency, row.Date) ?? 0m;
            converted.Add(new ConvertedBasket(row.Date, spend.Value, savings));
        }

        var months = converted.GroupBy(x => new { x.Date.Year, x.Date.Month })
            .OrderBy(x => x.Key.Year).ThenBy(x => x.Key.Month)
            .Select(group =>
            {
                var values = group.Select(x => x.Spend).OrderBy(x => x).ToList();
                var total = values.Sum();
                return new
                {
                    month = $"{group.Key.Year:D4}-{group.Key.Month:D2}",
                    purchaseCount = values.Count,
                    totalSpend = Math.Round(total, 2, MidpointRounding.AwayFromZero),
                    averageBasket = values.Count == 0 ? 0m : Math.Round(total / values.Count, 2, MidpointRounding.AwayFromZero),
                    medianBasket = Math.Round(Median(values), 2, MidpointRounding.AwayFromZero),
                    recognizedSavings = Math.Round(group.Sum(x => x.Savings), 2, MidpointRounding.AwayFromZero)
                };
            }).ToList();
        var averageChange = months.Count >= 2 && months[0].averageBasket > 0m
            ? Math.Round((months[^1].averageBasket / months[0].averageBasket - 1m) * 100m, 2, MidpointRounding.AwayFromZero)
            : (decimal?)null;
        return new
        {
            from = start,
            to = end,
            currency = baseCurrency,
            purchaseCount = converted.Count,
            averageBasketChangePercent = averageChange,
            incompleteFx = acc.Incomplete,
            months
        };
    }

    private static async Task<object?> RestockForecastAsync(
        FullWorthDbContext db,
        Guid userId,
        Guid fullWorthSpaceId,
        int horizonDays,
        CancellationToken ct)
    {
        if (!await db.FullWorthSpaceMembers.AsNoTracking().AnyAsync(x => x.UserId == userId && x.FullWorthSpaceId == fullWorthSpaceId, ct)) return null;
        horizonDays = Math.Clamp(horizonDays, 7, 730);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var since = today.AddYears(-3);
        var rows = await VisibleItems(db, userId, fullWorthSpaceId)
            .Where(x => x.ProductId.HasValue && x.Purchase.ReviewState == "confirmed" && x.Purchase.PurchaseDate >= since && x.Purchase.PurchaseDate <= today)
            .Select(x => new
            {
                ProductId = x.ProductId!.Value,
                ProductName = x.Product!.CanonicalName,
                x.Product.Brand,
                Date = x.Purchase.PurchaseDate!.Value,
                x.Quantity
            })
            .ToListAsync(ct);

        var forecasts = new List<object>();
        foreach (var group in rows.GroupBy(x => x.ProductId))
        {
            var purchases = group.GroupBy(x => x.Date)
                .OrderBy(x => x.Key)
                .Select(x => new { Date = x.Key, Quantity = x.Sum(v => v.Quantity) })
                .ToList();
            if (purchases.Count < 2) continue;
            var intervals = new List<int>();
            for (var index = 1; index < purchases.Count; index++)
            {
                var days = purchases[index].Date.DayNumber - purchases[index - 1].Date.DayNumber;
                if (days is > 0 and <= 1825) intervals.Add(days);
            }
            if (intervals.Count == 0) continue;
            var recentIntervals = intervals.TakeLast(6).Select(x => (decimal)x).ToList();
            var medianDays = Math.Max(1m, Median(recentIntervals));
            var averageDays = recentIntervals.Average();
            var variance = recentIntervals.Average(value => (value - averageDays) * (value - averageDays));
            var stddev = (decimal)Math.Sqrt((double)variance);
            var consistency = averageDays <= 0m ? 0m : Math.Clamp(1m - stddev / averageDays, 0m, 1m);
            var historyScore = Math.Min(1m, intervals.Count / 5m);
            var confidence = Math.Round(Math.Clamp(.35m + .65m * consistency * historyScore, .35m, 1m), 2, MidpointRounding.AwayFromZero);
            var last = purchases[^1];
            var expected = last.Date.AddDays((int)Math.Round(medianDays, 0, MidpointRounding.AwayFromZero));
            var daysUntil = expected.DayNumber - today.DayNumber;
            if (daysUntil > horizonDays) continue;
            var quantities = purchases.TakeLast(6).Select(x => x.Quantity).OrderBy(x => x).ToList();
            var sample = group.First();
            forecasts.Add(new
            {
                productId = group.Key,
                productName = sample.ProductName,
                sample.Brand,
                purchaseCount = purchases.Count,
                lastPurchaseDate = last.Date,
                expectedNextPurchase = expected,
                daysUntil,
                typicalIntervalDays = Math.Round(medianDays, 1, MidpointRounding.AwayFromZero),
                typicalQuantity = Math.Round(Median(quantities), 3, MidpointRounding.AwayFromZero),
                confidence,
                status = daysUntil < 0 ? "overdue" : daysUntil <= 7 ? "due_soon" : daysUntil <= 30 ? "upcoming" : "later"
            });
        }

        return new
        {
            asOf = today,
            horizonDays,
            count = forecasts.Count,
            items = forecasts.OrderBy(x => (int)x.GetType().GetProperty("daysUntil")!.GetValue(x)!).Take(100).ToList()
        };
    }

    private static (decimal Price, string Unit)? PriceMeasure(decimal? baseUnitPrice, decimal? unitPrice, decimal totalPrice, decimal quantity, string? unit)
    {
        var comparableUnit = PurchaseArticleCalculator.ComparableBaseUnit(unit);
        if (baseUnitPrice is > 0m && comparableUnit is not null) return (baseUnitPrice.Value, comparableUnit);
        var effective = unitPrice is > 0m ? unitPrice.Value : quantity > 0m ? totalPrice / quantity : totalPrice;
        return effective > 0m ? (effective, "piece") : null;
    }

    private static decimal Median(IReadOnlyList<decimal> sortedOrUnsorted)
    {
        if (sortedOrUnsorted.Count == 0) return 0m;
        var values = sortedOrUnsorted.OrderBy(x => x).ToArray();
        var middle = values.Length / 2;
        return values.Length % 2 == 1 ? values[middle] : (values[middle - 1] + values[middle]) / 2m;
    }

    private static Task<string?> BaseCurrencyAsync(FullWorthDbContext db, Guid userId, Guid fullWorthSpaceId, CancellationToken ct) =>
        db.FullWorthSpaces.AsNoTracking()
            .Where(x => x.Id == fullWorthSpaceId && db.FullWorthSpaceMembers.Any(m => m.FullWorthSpaceId == fullWorthSpaceId && m.UserId == userId))
            .Select(x => x.BaseCurrency)
            .SingleOrDefaultAsync(ct);

    private static IQueryable<Purchase> VisiblePurchases(FullWorthDbContext db, Guid userId, Guid fullWorthSpaceId) => db.Purchases.AsNoTracking().Where(purchase =>
        purchase.FullWorthSpaceId == fullWorthSpaceId && db.FullWorthSpaceMembers.Any(m => m.FullWorthSpaceId == fullWorthSpaceId && m.UserId == userId) &&
        (purchase.Visibility != "private" || purchase.CreatedByUserId == userId) &&
        (!purchase.PaymentLinks.Any() || purchase.PaymentLinks.Any(link => db.Transactions.Any(tx => tx.Id == link.TransactionId && db.Accounts.Any(account => account.Id == tx.AccountId && account.FullWorthSpaceId == fullWorthSpaceId && account.Owners.Any(owner => owner.UserId == userId))))) &&
        (purchase.TransactionId == null || db.Transactions.Any(tx => tx.Id == purchase.TransactionId && db.Accounts.Any(account => account.Id == tx.AccountId && account.FullWorthSpaceId == fullWorthSpaceId && account.Owners.Any(owner => owner.UserId == userId)))));

    private static IQueryable<PurchaseItem> VisibleItems(FullWorthDbContext db, Guid userId, Guid fullWorthSpaceId) => db.PurchaseItems.AsNoTracking().Where(item =>
        item.Purchase.FullWorthSpaceId == fullWorthSpaceId && db.FullWorthSpaceMembers.Any(m => m.FullWorthSpaceId == fullWorthSpaceId && m.UserId == userId) &&
        (item.Purchase.Visibility != "private" || item.Purchase.CreatedByUserId == userId) &&
        (!item.Purchase.PaymentLinks.Any() || item.Purchase.PaymentLinks.Any(link => db.Transactions.Any(tx => tx.Id == link.TransactionId && db.Accounts.Any(account => account.Id == tx.AccountId && account.FullWorthSpaceId == fullWorthSpaceId && account.Owners.Any(owner => owner.UserId == userId))))) &&
        (item.Purchase.TransactionId == null || db.Transactions.Any(tx => tx.Id == item.Purchase.TransactionId && db.Accounts.Any(account => account.Id == tx.AccountId && account.FullWorthSpaceId == fullWorthSpaceId && account.Owners.Any(owner => owner.UserId == userId)))));

    private sealed record InflationProduct(
        Guid ProductId,
        string ProductName,
        string? Brand,
        DateOnly FirstDate,
        DateOnly LatestDate,
        string FirstMerchant,
        string LatestMerchant,
        decimal FirstPrice,
        decimal LatestPrice,
        string Unit,
        decimal ChangePercent,
        decimal Weight);

    private sealed record ConvertedBasket(DateOnly Date, decimal Spend, decimal Savings);
}