using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.Fx;

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
            PurchaseInsightStore insights,
            CurrencyConverter currencyConverter,
            CancellationToken ct) =>
        {
            var value = await PersonalInflationAsync(insights, currencyConverter, user.RequireUserId(), fullWorthSpaceId, from, to, ct);
            return value is null ? Results.NotFound() : Results.Ok(value);
        });
        group.MapGet("/basket-trend", async (
            Guid fullWorthSpaceId,
            DateOnly? from,
            DateOnly? to,
            FullWorth.Backend.Security.CurrentUserContext user,
            PurchaseInsightStore insights,
            CurrencyConverter currencyConverter,
            CancellationToken ct) =>
        {
            var value = await BasketTrendAsync(insights, currencyConverter, user.RequireUserId(), fullWorthSpaceId, from, to, ct);
            return value is null ? Results.NotFound() : Results.Ok(value);
        });
        group.MapGet("/restock-forecast", async (
            Guid fullWorthSpaceId,
            int? horizonDays,
            FullWorth.Backend.Security.CurrentUserContext user,
            PurchaseInsightStore insights,
            CancellationToken ct) =>
        {
            var value = await RestockForecastAsync(insights, user.RequireUserId(), fullWorthSpaceId, horizonDays ?? 90, ct);
            return value is null ? Results.NotFound() : Results.Ok(value);
        });
        return app;
    }

    private static async Task<object?> PersonalInflationAsync(
        PurchaseInsightStore insights,
        CurrencyConverter currencyConverter,
        Guid userId,
        Guid fullWorthSpaceId,
        DateOnly? from,
        DateOnly? to,
        CancellationToken ct)
    {
        var baseCurrency = await insights.BaseCurrencyAsync(userId, fullWorthSpaceId, ct);
        if (baseCurrency is null) return null;
        var end = to ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var start = from ?? end.AddYears(-1);
        if (start > end) (start, end) = (end, start);

        var rows = await insights.PricedItemsAsync(userId, fullWorthSpaceId, start, end, ct);

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
        PurchaseInsightStore insights,
        CurrencyConverter currencyConverter,
        Guid userId,
        Guid fullWorthSpaceId,
        DateOnly? from,
        DateOnly? to,
        CancellationToken ct)
    {
        var baseCurrency = await insights.BaseCurrencyAsync(userId, fullWorthSpaceId, ct);
        if (baseCurrency is null) return null;
        var end = to ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var start = from ?? end.AddMonths(-11).AddDays(1 - end.Day);
        if (start > end) (start, end) = (end, start);

        var rows = await insights.BasketsAsync(userId, fullWorthSpaceId, start, end, ct);
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
        PurchaseInsightStore insights,
        Guid userId,
        Guid fullWorthSpaceId,
        int horizonDays,
        CancellationToken ct)
    {
        if (!await insights.IsMemberAsync(userId, fullWorthSpaceId, ct)) return null;
        horizonDays = Math.Clamp(horizonDays, 7, 730);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var since = today.AddYears(-3);
        var rows = await insights.RestockHistoryAsync(userId, fullWorthSpaceId, since, today, ct);

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