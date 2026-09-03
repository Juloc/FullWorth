using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.Fx;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Purchases;

public sealed class PurchaseAnalyticsService(FullWorthDbContext db, CurrencyConverter currencyConverter)
{
    public async Task<object?> OverviewAsync(Guid userId, Guid fullWorthSpaceId, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, fullWorthSpaceId, ct)) return null;
        var baseCurrency = await db.FullWorthSpaces.AsNoTracking().Where(x => x.Id == fullWorthSpaceId).Select(x => x.BaseCurrency).SingleAsync(ct);
        var start = from ?? DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(-1));
        var end = to ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var purchases = await VisiblePurchases(userId, fullWorthSpaceId)
            .Where(x => x.PurchaseDate == null || (x.PurchaseDate >= start && x.PurchaseDate <= end))
            .Select(x => new
            {
                x.Id,
                x.PurchaseDate,
                x.TotalAmount,
                x.Currency,
                x.ReviewState,
                x.DiscountAmount,
                canonicalDiscountTotal = x.Discounts.Sum(d => (decimal?)d.Amount),
                itemCount = x.Items.Count(),
                documentCount = x.Documents.Count(),
                linked = x.PaymentLinks.Any() || x.TransactionId != null
            })
            .ToListAsync(ct);
        var snapshot = await currencyConverter.PrepareAsync(baseCurrency, start, end, ct);
        var acc = new FxAccumulator(snapshot);
        decimal total = 0m;
        decimal savings = 0m;
        var confirmed = purchases.Where(x => x.ReviewState == "confirmed").ToList();
        foreach (var purchase in confirmed)
        {
            var date = purchase.PurchaseDate ?? end;
            var converted = acc.Convert(Math.Abs(purchase.TotalAmount), purchase.Currency, date);
            if (converted.HasValue) total += converted.Value;
            var sourceSavings = Math.Max(0m, purchase.canonicalDiscountTotal ?? purchase.DiscountAmount ?? 0m);
            var convertedSavings = acc.Convert(sourceSavings, purchase.Currency, date);
            if (convertedSavings.HasValue) savings += convertedSavings.Value;
        }
        return new
        {
            from = start,
            to = end,
            baseCurrency,
            purchaseCount = confirmed.Count,
            allPurchaseCount = purchases.Count,
            itemCount = confirmed.Sum(x => x.itemCount),
            documentCount = confirmed.Sum(x => x.documentCount),
            totalSpend = Math.Round(total, 2, MidpointRounding.AwayFromZero),
            recognizedSavings = Math.Round(savings, 2, MidpointRounding.AwayFromZero),
            needsReview = purchases.Count(x => x.ReviewState != "confirmed"),
            unlinked = purchases.Count(x => !x.linked),
            incompleteFx = acc.Incomplete
        };
    }

    public Task<object?> ByMerchantAsync(Guid userId, Guid fullWorthSpaceId, DateOnly? from, DateOnly? to, CancellationToken ct) => GroupSpendAsync(userId, fullWorthSpaceId, from, to, "merchant", ct);
    public Task<object?> ByCategoryAsync(Guid userId, Guid fullWorthSpaceId, DateOnly? from, DateOnly? to, CancellationToken ct) => GroupSpendAsync(userId, fullWorthSpaceId, from, to, "category", ct);
    public Task<object?> ByProductAsync(Guid userId, Guid fullWorthSpaceId, DateOnly? from, DateOnly? to, CancellationToken ct) => GroupSpendAsync(userId, fullWorthSpaceId, from, to, "product", ct);
    public Task<object?> ByBrandAsync(Guid userId, Guid fullWorthSpaceId, DateOnly? from, DateOnly? to, CancellationToken ct) => GroupSpendAsync(userId, fullWorthSpaceId, from, to, "brand", ct);

    public async Task<object?> SavingsAsync(Guid userId, Guid fullWorthSpaceId, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, fullWorthSpaceId, ct)) return null;
        var start = from ?? DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(-1));
        var end = to ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var baseCurrency = await db.FullWorthSpaces.AsNoTracking().Where(x => x.Id == fullWorthSpaceId).Select(x => x.BaseCurrency).SingleAsync(ct);
        var purchases = await VisiblePurchases(userId, fullWorthSpaceId)
            .Where(x => x.ReviewState == "confirmed" && x.PurchaseDate >= start && x.PurchaseDate <= end)
            .Select(x => new { x.Id, Date = x.PurchaseDate!.Value, x.Currency, x.DiscountAmount })
            .ToListAsync(ct);
        var ids = purchases.Select(x => x.Id).ToArray();
        var discountRows = ids.Length == 0
            ? []
            : await db.Set<PurchaseDiscount>().AsNoTracking()
                .Where(x => ids.Contains(x.PurchaseId))
                .Select(x => new { x.PurchaseId, x.Type, x.Amount, itemLinked = x.PurchaseItemId.HasValue })
                .ToListAsync(ct);
        var byPurchase = discountRows.GroupBy(x => x.PurchaseId).ToDictionary(x => x.Key, x => x.ToList());
        var snapshot = await currencyConverter.PrepareAsync(baseCurrency, start, end, ct);
        var acc = new FxAccumulator(snapshot);
        var byType = new Dictionary<string, (decimal Amount, int Count)>(StringComparer.OrdinalIgnoreCase);
        decimal total = 0m;
        decimal itemLinked = 0m;
        decimal basket = 0m;

        foreach (var purchase in purchases)
        {
            if (!byPurchase.TryGetValue(purchase.Id, out var rows) || rows.Count == 0)
            {
                var legacy = Math.Max(0m, purchase.DiscountAmount ?? 0m);
                if (legacy <= 0m) continue;
                var convertedLegacy = acc.Convert(legacy, purchase.Currency, purchase.Date);
                if (!convertedLegacy.HasValue) continue;
                total += convertedLegacy.Value;
                basket += convertedLegacy.Value;
                var currentLegacy = byType.GetValueOrDefault("other");
                byType["other"] = (currentLegacy.Amount + convertedLegacy.Value, currentLegacy.Count + 1);
                continue;
            }

            foreach (var row in rows)
            {
                if (row.Amount <= 0m) continue;
                var converted = acc.Convert(row.Amount, purchase.Currency, purchase.Date);
                if (!converted.HasValue) continue;
                total += converted.Value;
                if (row.itemLinked) itemLinked += converted.Value; else basket += converted.Value;
                var type = string.IsNullOrWhiteSpace(row.Type) ? "other" : row.Type;
                var current = byType.GetValueOrDefault(type);
                byType[type] = (current.Amount + converted.Value, current.Count + 1);
            }
        }

        return new
        {
            from = start,
            to = end,
            currency = baseCurrency,
            totalSavings = Math.Round(total, 2, MidpointRounding.AwayFromZero),
            itemLinkedSavings = Math.Round(itemLinked, 2, MidpointRounding.AwayFromZero),
            basketSavings = Math.Round(basket, 2, MidpointRounding.AwayFromZero),
            incompleteFx = acc.Incomplete,
            byType = byType.OrderByDescending(x => x.Value.Amount).Select(x => new
            {
                type = x.Key,
                amount = Math.Round(x.Value.Amount, 2, MidpointRounding.AwayFromZero),
                count = x.Value.Count
            }).ToList()
        };
    }

    public async Task<object?> PriceChangesAsync(Guid userId, Guid fullWorthSpaceId, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, fullWorthSpaceId, ct)) return null;
        var q = VisibleItems(userId, fullWorthSpaceId).Where(x => x.ProductId.HasValue && x.Purchase.ReviewState == "confirmed" && x.Purchase.PurchaseDate.HasValue);
        if (from.HasValue) q = q.Where(x => x.Purchase.PurchaseDate >= from.Value);
        if (to.HasValue) q = q.Where(x => x.Purchase.PurchaseDate <= to.Value);
        var rows = await q.OrderBy(x => x.Purchase.PurchaseDate).ThenBy(x => x.CreatedAt)
            .Select(x => new
            {
                ProductId = x.ProductId!.Value,
                productName = x.Product!.CanonicalName,
                x.Product.Brand,
                x.Purchase.PurchaseDate,
                x.Purchase.Merchant,
                x.Quantity,
                x.UnitPrice,
                x.OriginalUnitPrice,
                x.DiscountAmount,
                x.DiscountLabel,
                x.BaseUnitPrice,
                x.PackageQuantity,
                x.PackageCount,
                x.PackageUnit,
                x.QuantityUnit,
                x.TotalPrice,
                x.Currency
            })
            .ToListAsync(ct);
        var changes = new List<object>();
        foreach (var group in rows.GroupBy(x => x.ProductId))
        {
            var ordered = group.ToList();
            if (ordered.Count < 2) continue;
            var previous = ordered[^2];
            var current = ordered[^1];
            if (!string.Equals(previous.Currency, current.Currency, StringComparison.OrdinalIgnoreCase)) continue;
            var previousUnit = PurchaseArticleCalculator.ComparableBaseUnit(previous.PackageUnit ?? previous.QuantityUnit);
            var currentUnit = PurchaseArticleCalculator.ComparableBaseUnit(current.PackageUnit ?? current.QuantityUnit);
            var comparable = previousUnit is not null && previousUnit == currentUnit;
            var previousSize = comparable && previous.PackageQuantity.HasValue
                ? PurchaseArticleCalculator.ConvertPackageToBase(previous.PackageQuantity.Value * (previous.PackageCount ?? 1m), previous.PackageUnit)
                : null;
            var currentSize = comparable && current.PackageQuantity.HasValue
                ? PurchaseArticleCalculator.ConvertPackageToBase(current.PackageQuantity.Value * (current.PackageCount ?? 1m), current.PackageUnit)
                : null;
            var effectiveComparison = PurchaseArticleCalculator.Compare(
                previous.UnitPrice ?? previous.TotalPrice,
                current.UnitPrice ?? current.TotalPrice,
                comparable ? previous.BaseUnitPrice : null,
                comparable ? current.BaseUnitPrice : null,
                previousSize,
                currentSize);
            ProductPriceComparison? referenceComparison = null;
            decimal? previousOriginalBase = null;
            decimal? currentOriginalBase = null;
            if (previous.OriginalUnitPrice.HasValue && current.OriginalUnitPrice.HasValue)
            {
                previousOriginalBase = PurchaseArticleCalculator.BaseUnitPrice(previous.OriginalUnitPrice, previous.Quantity, previous.QuantityUnit, previous.PackageCount, previous.PackageQuantity, previous.PackageUnit, previous.Currency);
                currentOriginalBase = PurchaseArticleCalculator.BaseUnitPrice(current.OriginalUnitPrice, current.Quantity, current.QuantityUnit, current.PackageCount, current.PackageQuantity, current.PackageUnit, current.Currency);
                referenceComparison = PurchaseArticleCalculator.Compare(
                    previous.OriginalUnitPrice,
                    current.OriginalUnitPrice,
                    comparable ? previousOriginalBase : null,
                    comparable ? currentOriginalBase : null,
                    previousSize,
                    currentSize);
            }
            var comparison = referenceComparison ?? effectiveComparison;
            changes.Add(new
            {
                productId = group.Key,
                current.productName,
                current.Brand,
                current.Currency,
                comparisonBasis = referenceComparison is null ? "effective" : "reference",
                previous = new
                {
                    previous.PurchaseDate,
                    previous.Merchant,
                    price = previous.UnitPrice ?? previous.TotalPrice,
                    originalPrice = previous.OriginalUnitPrice,
                    previous.DiscountAmount,
                    previous.DiscountLabel,
                    previous.BaseUnitPrice,
                    originalBasePrice = previousOriginalBase,
                    packageSize = previousSize,
                    unit = previousUnit
                },
                current = new
                {
                    current.PurchaseDate,
                    current.Merchant,
                    price = current.UnitPrice ?? current.TotalPrice,
                    originalPrice = current.OriginalUnitPrice,
                    current.DiscountAmount,
                    current.DiscountLabel,
                    current.BaseUnitPrice,
                    originalBasePrice = currentOriginalBase,
                    packageSize = currentSize,
                    unit = currentUnit
                },
                comparison,
                effectiveComparison,
                referenceComparison
            });
        }
        return new { count = changes.Count, items = changes };
    }

    public async Task<object?> ProductMerchantComparisonAsync(Guid userId, Guid fullWorthSpaceId, Guid productId, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, fullWorthSpaceId, ct) || !await db.Set<Product>().AnyAsync(x => x.Id == productId && x.FullWorthSpaceId == fullWorthSpaceId, ct)) return null;
        var q = VisibleItems(userId, fullWorthSpaceId).Where(x => x.ProductId == productId && x.Purchase.ReviewState == "confirmed");
        if (from.HasValue) q = q.Where(x => x.Purchase.PurchaseDate >= from.Value);
        if (to.HasValue) q = q.Where(x => x.Purchase.PurchaseDate <= to.Value);
        var rows = await q.Select(x => new
        {
            x.Purchase.Merchant,
            x.Purchase.MerchantId,
            x.UnitPrice,
            x.OriginalUnitPrice,
            x.DiscountAmount,
            x.BaseUnitPrice,
            x.TotalPrice,
            x.Currency,
            x.Purchase.PurchaseDate
        }).ToListAsync(ct);
        var grouped = rows.GroupBy(x => new { x.MerchantId, x.Merchant, x.Currency }).Select(g =>
        {
            var originals = g.Where(x => x.OriginalUnitPrice.HasValue).Select(x => x.OriginalUnitPrice!.Value).ToList();
            var discounts = g.Where(x => x.DiscountAmount.HasValue).Select(x => Math.Max(0m, x.DiscountAmount!.Value)).ToList();
            return new
            {
                g.Key.MerchantId,
                merchant = g.Key.Merchant,
                currency = g.Key.Currency,
                purchaseCount = g.Count(),
                lastDate = g.Max(x => x.PurchaseDate),
                latest = g.OrderByDescending(x => x.PurchaseDate).Select(x => x.UnitPrice ?? x.TotalPrice).FirstOrDefault(),
                latestOriginal = g.OrderByDescending(x => x.PurchaseDate).Select(x => x.OriginalUnitPrice).FirstOrDefault(),
                average = Math.Round(g.Average(x => x.UnitPrice ?? x.TotalPrice), 4, MidpointRounding.AwayFromZero),
                averageOriginal = originals.Count == 0 ? (decimal?)null : Math.Round(originals.Average(), 4, MidpointRounding.AwayFromZero),
                averageDiscount = discounts.Count == 0 ? 0m : Math.Round(discounts.Average(), 4, MidpointRounding.AwayFromZero),
                min = g.Min(x => x.UnitPrice ?? x.TotalPrice),
                max = g.Max(x => x.UnitPrice ?? x.TotalPrice),
                latestBasePrice = g.OrderByDescending(x => x.PurchaseDate).Select(x => x.BaseUnitPrice).FirstOrDefault()
            };
        }).OrderBy(x => x.latestBasePrice ?? x.latest).ToList();
        return new { productId, merchants = grouped };
    }

    private async Task<object?> GroupSpendAsync(Guid userId, Guid fullWorthSpaceId, DateOnly? from, DateOnly? to, string mode, CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, fullWorthSpaceId, ct)) return null;
        var start = from ?? DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(-1));
        var end = to ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var baseCurrency = await db.FullWorthSpaces.AsNoTracking().Where(x => x.Id == fullWorthSpaceId).Select(x => x.BaseCurrency).SingleAsync(ct);
        var snapshot = await currencyConverter.PrepareAsync(baseCurrency, start, end, ct);
        var acc = new FxAccumulator(snapshot);
        var result = new Dictionary<string, (string Label, Guid? Id, decimal Amount, int Count)>(StringComparer.OrdinalIgnoreCase);
        if (mode == "merchant")
        {
            var rows = await VisiblePurchases(userId, fullWorthSpaceId).Where(x => x.ReviewState == "confirmed" && x.PurchaseDate >= start && x.PurchaseDate <= end)
                .Select(x => new { x.Merchant, x.MerchantId, x.TotalAmount, x.Currency, Date = x.PurchaseDate!.Value }).ToListAsync(ct);
            foreach (var row in rows)
            {
                var converted = acc.Convert(Math.Abs(row.TotalAmount), row.Currency, row.Date); if (!converted.HasValue) continue;
                var key = row.MerchantId?.ToString() ?? row.Merchant.Trim().ToLowerInvariant(); var old = result.GetValueOrDefault(key, (Label: row.Merchant, Id: row.MerchantId, Amount: 0m, Count: 0));
                result[key] = (old.Label, old.Id, old.Amount + converted.Value, old.Count + 1);
            }
        }
        else
        {
            var rows = await VisibleItems(userId, fullWorthSpaceId).Where(x => x.Purchase.ReviewState == "confirmed" && x.Purchase.PurchaseDate >= start && x.Purchase.PurchaseDate <= end)
                .Select(x => new { x.CategoryId, CategoryName = x.CategoryId.HasValue ? db.Categories.Where(c => c.Id == x.CategoryId.Value).Select(c => c.Name).FirstOrDefault() : null, x.ProductId, ProductName = x.ProductId.HasValue ? x.Product!.CanonicalName : null, x.Brand, x.TotalPrice, x.Currency, Date = x.Purchase.PurchaseDate!.Value }).ToListAsync(ct);
            foreach (var row in rows)
            {
                // Item TotalPrice is the effective charged merchandise contribution. Canonical basket
                // discounts stay separate and are reported by SavingsAsync instead of being invented as
                // product/category rows. Item-linked discounts are already reflected in TotalPrice.
                var converted = acc.Convert(row.TotalPrice, row.Currency, row.Date); if (!converted.HasValue) continue;
                Guid? id; string label; string key;
                if (mode == "category") { id = row.CategoryId; label = row.CategoryName ?? "Uncategorized"; key = id?.ToString() ?? "uncategorized"; }
                else if (mode == "product") { id = row.ProductId; label = row.ProductName ?? "Unmatched product"; key = id?.ToString() ?? $"unmatched:{label.ToLowerInvariant()}"; }
                else { id = null; label = string.IsNullOrWhiteSpace(row.Brand) ? "Unknown brand" : row.Brand; key = label.ToLowerInvariant(); }
                var old = result.GetValueOrDefault(key, (Label: label, Id: id, Amount: 0m, Count: 0)); result[key] = (old.Label, old.Id, old.Amount + converted.Value, old.Count + 1);
            }
        }
        var items = result.Values.OrderByDescending(x => x.Amount).Select(x => new { x.Id, label = x.Label, amount = Math.Round(x.Amount, 2, MidpointRounding.AwayFromZero), x.Count }).ToList();
        return new { from = start, to = end, currency = baseCurrency, incompleteFx = acc.Incomplete, items };
    }

    private IQueryable<Purchase> VisiblePurchases(Guid userId, Guid fullWorthSpaceId) => db.Purchases.AsNoTracking().Where(p => p.FullWorthSpaceId == fullWorthSpaceId && db.FullWorthSpaceMembers.Any(m => m.FullWorthSpaceId == fullWorthSpaceId && m.UserId == userId) && (p.Visibility != "private" || p.CreatedByUserId == userId) && (!p.PaymentLinks.Any() || p.PaymentLinks.Any(link => db.Transactions.Any(tx => tx.Id == link.TransactionId && db.Accounts.Any(a => a.Id == tx.AccountId && a.Owners.Any(o => o.UserId == userId))))) && (p.TransactionId == null || db.Transactions.Any(tx => tx.Id == p.TransactionId && db.Accounts.Any(a => a.Id == tx.AccountId && a.Owners.Any(o => o.UserId == userId)))));
    private IQueryable<PurchaseItem> VisibleItems(Guid userId, Guid fullWorthSpaceId) => db.PurchaseItems.AsNoTracking().Where(i => i.Purchase.FullWorthSpaceId == fullWorthSpaceId && db.FullWorthSpaceMembers.Any(m => m.FullWorthSpaceId == fullWorthSpaceId && m.UserId == userId) && (i.Purchase.Visibility != "private" || i.Purchase.CreatedByUserId == userId) && (!i.Purchase.PaymentLinks.Any() || i.Purchase.PaymentLinks.Any(link => db.Transactions.Any(tx => tx.Id == link.TransactionId && db.Accounts.Any(a => a.Id == tx.AccountId && a.Owners.Any(o => o.UserId == userId))))) && (i.Purchase.TransactionId == null || db.Transactions.Any(tx => tx.Id == i.Purchase.TransactionId && db.Accounts.Any(a => a.Id == tx.AccountId && a.Owners.Any(o => o.UserId == userId)))));
    private Task<bool> IsMemberAsync(Guid userId, Guid fullWorthSpaceId, CancellationToken ct) => db.FullWorthSpaceMembers.AsNoTracking().AnyAsync(x => x.UserId == userId && x.FullWorthSpaceId == fullWorthSpaceId, ct);
}
