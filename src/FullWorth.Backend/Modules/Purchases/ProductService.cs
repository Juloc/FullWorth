using System.Text;
using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Purchases;

public sealed record ProductWrite(
    string CanonicalName,
    string? Brand,
    Guid? DefaultCategoryId,
    string? DefaultQuantityUnit,
    decimal? DefaultPackageQuantity,
    string? DefaultPackageUnit,
    string? ImageReference,
    string? Notes);

public sealed record ProductAliasWrite(string Alias, Guid? MerchantId = null, string AliasType = "manual");
public sealed record ProductBarcodeWrite(string Code, string Standard = "unknown");
public sealed record ProductMergeRequest(Guid SourceProductId, Guid TargetProductId, bool PreferSourceName = false, bool PreferSourceBrand = false, bool PreferSourceCategory = false);

public sealed record ProductListItem(
    Guid Id,
    string CanonicalName,
    string? Brand,
    Guid? DefaultCategoryId,
    string? DefaultQuantityUnit,
    decimal? DefaultPackageQuantity,
    string? DefaultPackageUnit,
    bool IsArchived,
    decimal? LastPrice,
    decimal? LastOriginalPrice,
    decimal? LastDiscountAmount,
    string? LastDiscountLabel,
    decimal? LastBasePrice,
    decimal? LastOriginalBasePrice,
    string? LastCurrency,
    DateOnly? LastPurchased,
    int PurchaseCount);

public sealed class ProductService(FullWorthDbContext db)
{
    public async Task<bool> IsMemberAsync(Guid userId, Guid fullWorthSpaceId, CancellationToken ct) =>
        await db.FullWorthSpaceMembers.AsNoTracking().AnyAsync(x => x.UserId == userId && x.FullWorthSpaceId == fullWorthSpaceId, ct);

    public async Task<object?> ListAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        string? query,
        Guid? categoryId,
        string? brand,
        bool includeArchived,
        int offset,
        int limit,
        CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, fullWorthSpaceId, ct)) return null;
        offset = Math.Max(0, offset);
        limit = Math.Clamp(limit <= 0 ? 100 : limit, 1, 500);

        var products = db.Set<Product>().AsNoTracking().Where(x => x.FullWorthSpaceId == fullWorthSpaceId);
        if (!includeArchived) products = products.Where(x => !x.IsArchived);
        if (categoryId.HasValue) products = products.Where(x => x.DefaultCategoryId == categoryId.Value);
        if (!string.IsNullOrWhiteSpace(brand))
        {
            var pattern = $"%{brand.Trim()}%";
            products = products.Where(x => x.Brand != null && EF.Functions.ILike(x.Brand, pattern));
        }
        if (!string.IsNullOrWhiteSpace(query))
        {
            var pattern = $"%{query.Trim()}%";
            products = products.Where(x =>
                EF.Functions.ILike(x.CanonicalName, pattern) ||
                (x.Brand != null && EF.Functions.ILike(x.Brand, pattern)) ||
                x.Aliases.Any(a => EF.Functions.ILike(a.Alias, pattern)) ||
                x.Barcodes.Any(b => EF.Functions.ILike(b.Code, pattern)));
        }

        var total = await products.CountAsync(ct);
        var page = await products.OrderBy(x => x.CanonicalName).ThenBy(x => x.Id)
            .Skip(offset).Take(limit)
            .Select(x => new
            {
                x.Id,
                x.CanonicalName,
                x.Brand,
                x.DefaultCategoryId,
                x.DefaultQuantityUnit,
                x.DefaultPackageQuantity,
                x.DefaultPackageUnit,
                x.IsArchived
            }).ToListAsync(ct);

        var ids = page.Select(x => x.Id).ToArray();
        // Only confirmed purchase items are price observations. OCR/import drafts are deliberately excluded
        // so an unreviewed extraction can never become the product's "latest price" or purchase count.
        var observations = await VisiblePurchaseItems(userId, fullWorthSpaceId)
            .Where(x => x.ProductId.HasValue && ids.Contains(x.ProductId.Value) && x.Purchase.ReviewState == "confirmed")
            .OrderByDescending(x => x.Purchase.PurchaseDate)
            .ThenByDescending(x => x.CreatedAt)
            .Select(x => new
            {
                ProductId = x.ProductId!.Value,
                x.UnitPrice,
                x.OriginalUnitPrice,
                x.DiscountAmount,
                x.DiscountLabel,
                x.TotalPrice,
                x.BaseUnitPrice,
                x.Quantity,
                x.QuantityUnit,
                x.PackageCount,
                x.PackageQuantity,
                x.PackageUnit,
                x.Currency,
                x.Purchase.PurchaseDate
            })
            .ToListAsync(ct);
        var counts = observations.GroupBy(x => x.ProductId).ToDictionary(g => g.Key, g => g.Count());
        var last = observations.GroupBy(x => x.ProductId).ToDictionary(g => g.Key, g => g.First());

        var items = page.Select(x =>
        {
            last.TryGetValue(x.Id, out var lastRow);
            var originalBasePrice = lastRow?.OriginalUnitPrice is null ? null : PurchaseArticleCalculator.BaseUnitPrice(
                lastRow.OriginalUnitPrice,
                lastRow.Quantity,
                lastRow.QuantityUnit,
                lastRow.PackageCount,
                lastRow.PackageQuantity,
                lastRow.PackageUnit,
                lastRow.Currency);
            return new ProductListItem(
                x.Id, x.CanonicalName, x.Brand, x.DefaultCategoryId, x.DefaultQuantityUnit,
                x.DefaultPackageQuantity, x.DefaultPackageUnit, x.IsArchived,
                lastRow?.UnitPrice ?? lastRow?.TotalPrice,
                lastRow?.OriginalUnitPrice,
                lastRow?.DiscountAmount,
                lastRow?.DiscountLabel,
                lastRow?.BaseUnitPrice,
                originalBasePrice,
                lastRow?.Currency,
                lastRow?.PurchaseDate,
                counts.GetValueOrDefault(x.Id));
        }).ToList();
        return new { total, offset, limit, items };
    }

    public async Task<object?> GetAsync(Guid userId, Guid fullWorthSpaceId, Guid id, CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, fullWorthSpaceId, ct)) return null;
        var product = await db.Set<Product>().AsNoTracking()
            .Where(x => x.Id == id && x.FullWorthSpaceId == fullWorthSpaceId)
            .Select(x => new
            {
                x.Id, x.FullWorthSpaceId, x.CanonicalName, x.Brand, x.DefaultCategoryId,
                x.DefaultQuantityUnit, x.DefaultPackageQuantity, x.DefaultPackageUnit,
                x.ImageReference, x.Notes, x.IsArchived, x.CreatedAt, x.UpdatedAt,
                aliases = x.Aliases.OrderBy(a => a.Alias).Select(a => new { a.Id, a.MerchantId, a.Alias, a.AliasType, a.CreatedAt }).ToList(),
                barcodes = x.Barcodes.OrderBy(b => b.Code).Select(b => new { b.Id, b.Code, b.Standard, b.CreatedAt }).ToList()
            }).SingleOrDefaultAsync(ct);
        if (product is null) return null;
        var history = await HistoryAsync(userId, fullWorthSpaceId, id, null, null, ct);
        return new { product, history };
    }

    public async Task<(PurchaseMutationResult Result, object? Value, string? Error)> CreateAsync(Guid userId, Guid fullWorthSpaceId, ProductWrite request, CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, fullWorthSpaceId, ct)) return (PurchaseMutationResult.NotFound, null, null);
        var error = await ValidateWriteAsync(fullWorthSpaceId, request, ct);
        if (error is not null) return (PurchaseMutationResult.Invalid, null, error);
        var entity = new Product { FullWorthSpaceId = fullWorthSpaceId };
        Apply(entity, request);
        db.Set<Product>().Add(entity);
        await db.SaveChangesAsync(ct);
        return (PurchaseMutationResult.Success, await GetAsync(userId, fullWorthSpaceId, entity.Id, ct), null);
    }

    public async Task<(PurchaseMutationResult Result, object? Value, string? Error)> UpdateAsync(Guid userId, Guid fullWorthSpaceId, Guid id, ProductWrite request, CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, fullWorthSpaceId, ct)) return (PurchaseMutationResult.NotFound, null, null);
        var entity = await db.Set<Product>().SingleOrDefaultAsync(x => x.Id == id && x.FullWorthSpaceId == fullWorthSpaceId, ct);
        if (entity is null) return (PurchaseMutationResult.NotFound, null, null);
        var error = await ValidateWriteAsync(fullWorthSpaceId, request, ct);
        if (error is not null) return (PurchaseMutationResult.Invalid, null, error);
        Apply(entity, request);
        await db.SaveChangesAsync(ct);
        return (PurchaseMutationResult.Success, await GetAsync(userId, fullWorthSpaceId, id, ct), null);
    }

    public async Task<PurchaseMutationResult> ArchiveAsync(Guid userId, Guid fullWorthSpaceId, Guid id, bool archived, CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, fullWorthSpaceId, ct)) return PurchaseMutationResult.NotFound;
        var entity = await db.Set<Product>().SingleOrDefaultAsync(x => x.Id == id && x.FullWorthSpaceId == fullWorthSpaceId, ct);
        if (entity is null) return PurchaseMutationResult.NotFound;
        entity.IsArchived = archived;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return PurchaseMutationResult.Success;
    }

    public async Task<(PurchaseMutationResult Result, object? Value, string? Error)> AddAliasAsync(Guid userId, Guid fullWorthSpaceId, Guid productId, ProductAliasWrite request, CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, fullWorthSpaceId, ct)) return (PurchaseMutationResult.NotFound, null, null);
        var product = await db.Set<Product>().SingleOrDefaultAsync(x => x.Id == productId && x.FullWorthSpaceId == fullWorthSpaceId, ct);
        if (product is null) return (PurchaseMutationResult.NotFound, null, null);
        var alias = request.Alias?.Trim();
        if (string.IsNullOrWhiteSpace(alias)) return (PurchaseMutationResult.Invalid, null, "Alias is required.");
        var normalized = Normalize(alias);
        var duplicate = await db.Set<ProductAlias>().AsNoTracking().AnyAsync(x => x.ProductId == productId && x.NormalizedAlias == normalized && x.MerchantId == request.MerchantId, ct);
        if (duplicate) return (PurchaseMutationResult.Invalid, null, "Alias already exists for this product.");
        var entity = new ProductAlias { ProductId = productId, MerchantId = request.MerchantId, Alias = alias, NormalizedAlias = normalized, AliasType = NormalizeToken(request.AliasType, "manual") };
        db.Set<ProductAlias>().Add(entity);
        await db.SaveChangesAsync(ct);
        return (PurchaseMutationResult.Success, new { entity.Id, entity.MerchantId, entity.Alias, entity.AliasType, entity.CreatedAt }, null);
    }

    public async Task<PurchaseMutationResult> RemoveAliasAsync(Guid userId, Guid fullWorthSpaceId, Guid productId, Guid aliasId, CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, fullWorthSpaceId, ct)) return PurchaseMutationResult.NotFound;
        var alias = await db.Set<ProductAlias>().Where(x => x.Id == aliasId && x.ProductId == productId)
            .Where(x => db.Set<Product>().Any(p => p.Id == x.ProductId && p.FullWorthSpaceId == fullWorthSpaceId)).SingleOrDefaultAsync(ct);
        if (alias is null) return PurchaseMutationResult.NotFound;
        db.Remove(alias);
        await db.SaveChangesAsync(ct);
        return PurchaseMutationResult.Success;
    }

    public async Task<(PurchaseMutationResult Result, object? Value, string? Error)> AddBarcodeAsync(Guid userId, Guid fullWorthSpaceId, Guid productId, ProductBarcodeWrite request, CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, fullWorthSpaceId, ct)) return (PurchaseMutationResult.NotFound, null, null);
        if (!await db.Set<Product>().AnyAsync(x => x.Id == productId && x.FullWorthSpaceId == fullWorthSpaceId, ct)) return (PurchaseMutationResult.NotFound, null, null);
        var code = NormalizeBarcode(request.Code);
        if (code.Length < 4 || code.Length > 64) return (PurchaseMutationResult.Invalid, null, "Barcode is invalid.");
        if (await db.Set<ProductBarcode>().AsNoTracking().AnyAsync(x => x.Code == code, ct))
            return (PurchaseMutationResult.Invalid, null, "Barcode is already linked to a product.");
        var entity = new ProductBarcode { ProductId = productId, Code = code, Standard = NormalizeBarcodeStandard(request.Standard, code) };
        db.Set<ProductBarcode>().Add(entity);
        await db.SaveChangesAsync(ct);
        return (PurchaseMutationResult.Success, new { entity.Id, entity.Code, entity.Standard, entity.CreatedAt }, null);
    }

    public async Task<PurchaseMutationResult> RemoveBarcodeAsync(Guid userId, Guid fullWorthSpaceId, Guid productId, Guid barcodeId, CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, fullWorthSpaceId, ct)) return PurchaseMutationResult.NotFound;
        var barcode = await db.Set<ProductBarcode>().Where(x => x.Id == barcodeId && x.ProductId == productId)
            .Where(x => db.Set<Product>().Any(p => p.Id == x.ProductId && p.FullWorthSpaceId == fullWorthSpaceId)).SingleOrDefaultAsync(ct);
        if (barcode is null) return PurchaseMutationResult.NotFound;
        db.Remove(barcode);
        await db.SaveChangesAsync(ct);
        return PurchaseMutationResult.Success;
    }

    public async Task<object?> MatchAsync(Guid userId, Guid fullWorthSpaceId, string? barcode, string? name, string? brand, Guid? merchantId, CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, fullWorthSpaceId, ct)) return null;
        if (!string.IsNullOrWhiteSpace(barcode))
        {
            var code = NormalizeBarcode(barcode);
            var exact = await db.Set<ProductBarcode>().AsNoTracking()
                .Where(x => x.Code == code && x.Product.FullWorthSpaceId == fullWorthSpaceId && !x.Product.IsArchived)
                .Select(x => new { x.Product.Id, x.Product.CanonicalName, x.Product.Brand, confidence = 1m, reason = "barcode" }).SingleOrDefaultAsync(ct);
            if (exact is not null) return exact;
        }

        var normalizedName = Normalize(name);
        if (normalizedName.Length == 0) return new { candidates = Array.Empty<object>() };
        var aliases = await db.Set<ProductAlias>().AsNoTracking()
            .Where(x => x.Product.FullWorthSpaceId == fullWorthSpaceId && !x.Product.IsArchived && x.NormalizedAlias == normalizedName)
            .Where(x => !merchantId.HasValue || x.MerchantId == null || x.MerchantId == merchantId)
            .Select(x => new { x.Product.Id, x.Product.CanonicalName, x.Product.Brand, confidence = .98m, reason = "alias" }).Take(5).ToListAsync(ct);
        if (aliases.Count > 0) return new { candidates = aliases };

        var pattern = $"%{name!.Trim()}%";
        var nameCandidates = await db.Set<Product>().AsNoTracking()
            .Where(x => x.FullWorthSpaceId == fullWorthSpaceId && !x.IsArchived && EF.Functions.ILike(x.CanonicalName, pattern))
            .Select(x => new { x.Id, x.CanonicalName, x.Brand }).Take(10).ToListAsync(ct);
        var candidates = nameCandidates.Select(x => new
        {
            x.Id, x.CanonicalName, x.Brand,
            confidence = string.Equals(Normalize(x.CanonicalName), normalizedName, StringComparison.Ordinal) &&
                         (string.IsNullOrWhiteSpace(brand) || string.Equals(Normalize(x.Brand), Normalize(brand), StringComparison.Ordinal)) ? .95m : .70m,
            reason = "name"
        }).OrderByDescending(x => x.confidence).ToList();
        return new { candidates };
    }

    public async Task<object?> HistoryAsync(Guid userId, Guid fullWorthSpaceId, Guid productId, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, fullWorthSpaceId, ct)) return null;
        if (!await db.Set<Product>().AsNoTracking().AnyAsync(x => x.Id == productId && x.FullWorthSpaceId == fullWorthSpaceId, ct)) return null;
        var query = VisiblePurchaseItems(userId, fullWorthSpaceId).Where(x => x.ProductId == productId && x.Purchase.ReviewState == "confirmed");
        if (from.HasValue) query = query.Where(x => x.Purchase.PurchaseDate >= from.Value);
        if (to.HasValue) query = query.Where(x => x.Purchase.PurchaseDate <= to.Value);
        var rows = await query.OrderBy(x => x.Purchase.PurchaseDate).ThenBy(x => x.CreatedAt)
            .Select(x => new
            {
                x.Id,
                x.PurchaseId,
                x.Purchase.PurchaseDate,
                merchant = x.Purchase.Merchant,
                x.Name,
                x.Quantity,
                x.QuantityUnit,
                x.PackageCount,
                x.PackageQuantity,
                x.PackageUnit,
                x.UnitPrice,
                x.OriginalUnitPrice,
                x.DiscountAmount,
                x.DiscountLabel,
                x.BaseUnitPrice,
                x.TotalPrice,
                x.Currency,
                x.LineType
            }).ToListAsync(ct);

        object? PriceComparison(int previousIndex, int currentIndex, bool reference)
        {
            var previous = rows[previousIndex];
            var current = rows[currentIndex];
            if (!string.Equals(previous.Currency, current.Currency, StringComparison.OrdinalIgnoreCase)) return null;
            var previousBaseUnit = PurchaseArticleCalculator.ComparableBaseUnit(previous.PackageUnit ?? previous.QuantityUnit);
            var currentBaseUnit = PurchaseArticleCalculator.ComparableBaseUnit(current.PackageUnit ?? current.QuantityUnit);
            var comparableUnit = !string.IsNullOrWhiteSpace(previousBaseUnit) && previousBaseUnit == currentBaseUnit;
            var previousSize = comparableUnit && previous.PackageQuantity.HasValue
                ? PurchaseArticleCalculator.ConvertPackageToBase(previous.PackageQuantity.Value * (previous.PackageCount ?? 1m), previous.PackageUnit)
                : null;
            var currentSize = comparableUnit && current.PackageQuantity.HasValue
                ? PurchaseArticleCalculator.ConvertPackageToBase(current.PackageQuantity.Value * (current.PackageCount ?? 1m), current.PackageUnit)
                : null;
            var previousPrice = reference ? previous.OriginalUnitPrice : previous.UnitPrice ?? previous.TotalPrice;
            var currentPrice = reference ? current.OriginalUnitPrice : current.UnitPrice ?? current.TotalPrice;
            if (!previousPrice.HasValue || !currentPrice.HasValue) return null;
            var previousBasePrice = reference
                ? PurchaseArticleCalculator.BaseUnitPrice(previous.OriginalUnitPrice, previous.Quantity, previous.QuantityUnit, previous.PackageCount, previous.PackageQuantity, previous.PackageUnit, previous.Currency)
                : previous.BaseUnitPrice;
            var currentBasePrice = reference
                ? PurchaseArticleCalculator.BaseUnitPrice(current.OriginalUnitPrice, current.Quantity, current.QuantityUnit, current.PackageCount, current.PackageQuantity, current.PackageUnit, current.Currency)
                : current.BaseUnitPrice;
            return PurchaseArticleCalculator.Compare(
                previousPrice,
                currentPrice,
                comparableUnit ? previousBasePrice : null,
                comparableUnit ? currentBasePrice : null,
                previousSize,
                currentSize);
        }

        object? latestEffectiveComparison = null;
        object? latestReferenceComparison = null;
        object? latestComparison = null;
        var latestComparisonBasis = "effective";
        if (rows.Count >= 2)
        {
            latestEffectiveComparison = PriceComparison(rows.Count - 2, rows.Count - 1, reference: false);
            latestReferenceComparison = PriceComparison(rows.Count - 2, rows.Count - 1, reference: true);
            latestComparison = latestReferenceComparison ?? latestEffectiveComparison;
            latestComparisonBasis = latestReferenceComparison is null ? "effective" : "reference";
        }

        var observations = rows.Select(row =>
        {
            var originalBaseUnitPrice = row.OriginalUnitPrice.HasValue
                ? PurchaseArticleCalculator.BaseUnitPrice(row.OriginalUnitPrice, row.Quantity, row.QuantityUnit, row.PackageCount, row.PackageQuantity, row.PackageUnit, row.Currency)
                : null;
            var effectivePrice = row.UnitPrice ?? row.TotalPrice;
            var savingsPercent = row.OriginalUnitPrice is > 0m && effectivePrice < row.OriginalUnitPrice.Value
                ? Math.Round((row.OriginalUnitPrice.Value - effectivePrice) / row.OriginalUnitPrice.Value * 100m, 2, MidpointRounding.AwayFromZero)
                : (decimal?)null;
            return new
            {
                row.Id, row.PurchaseId, row.PurchaseDate, row.merchant, row.Name,
                row.Quantity, row.QuantityUnit, row.PackageCount, row.PackageQuantity, row.PackageUnit,
                row.UnitPrice, row.OriginalUnitPrice, row.DiscountAmount, row.DiscountLabel,
                row.BaseUnitPrice, originalBaseUnitPrice, row.TotalPrice, row.Currency, row.LineType,
                effectivePrice, savingsPercent
            };
        }).ToList();

        return new
        {
            productId,
            count = observations.Count,
            observations,
            latestComparison,
            latestComparisonBasis,
            latestEffectiveComparison,
            latestReferenceComparison
        };
    }

    public async Task<(PurchaseMutationResult Result, string? Error)> MergeAsync(Guid userId, Guid fullWorthSpaceId, ProductMergeRequest request, CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, fullWorthSpaceId, ct)) return (PurchaseMutationResult.NotFound, null);
        if (request.SourceProductId == request.TargetProductId) return (PurchaseMutationResult.Invalid, "Source and target product must differ.");
        var products = await db.Set<Product>().Where(x => x.FullWorthSpaceId == fullWorthSpaceId && (x.Id == request.SourceProductId || x.Id == request.TargetProductId)).ToListAsync(ct);
        var source = products.SingleOrDefault(x => x.Id == request.SourceProductId);
        var target = products.SingleOrDefault(x => x.Id == request.TargetProductId);
        if (source is null || target is null) return (PurchaseMutationResult.NotFound, null);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var items = await db.PurchaseItems.Where(x => x.ProductId == source.Id).ToListAsync(ct);
        foreach (var item in items) item.ProductId = target.Id;

        var targetCodes = await db.Set<ProductBarcode>().Where(x => x.ProductId == target.Id).Select(x => x.Code).ToListAsync(ct);
        var sourceCodes = await db.Set<ProductBarcode>().Where(x => x.ProductId == source.Id).ToListAsync(ct);
        foreach (var code in sourceCodes)
        {
            if (targetCodes.Contains(code.Code, StringComparer.OrdinalIgnoreCase)) db.Remove(code);
            else code.ProductId = target.Id;
        }

        var targetAliasKeys = await db.Set<ProductAlias>().Where(x => x.ProductId == target.Id).Select(x => new { x.NormalizedAlias, x.MerchantId }).ToListAsync(ct);
        var sourceAliases = await db.Set<ProductAlias>().Where(x => x.ProductId == source.Id).ToListAsync(ct);
        foreach (var alias in sourceAliases)
        {
            if (targetAliasKeys.Any(x => x.NormalizedAlias == alias.NormalizedAlias && x.MerchantId == alias.MerchantId)) db.Remove(alias);
            else alias.ProductId = target.Id;
        }

        if (request.PreferSourceName) target.CanonicalName = source.CanonicalName;
        if (request.PreferSourceBrand) target.Brand = source.Brand;
        if (request.PreferSourceCategory) target.DefaultCategoryId = source.DefaultCategoryId;
        target.UpdatedAt = DateTimeOffset.UtcNow;
        source.IsArchived = true;
        source.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return (PurchaseMutationResult.Success, null);
    }

    private IQueryable<PurchaseItem> VisiblePurchaseItems(Guid userId, Guid fullWorthSpaceId) =>
        db.PurchaseItems.AsNoTracking().Where(item =>
            item.Purchase.FullWorthSpaceId == fullWorthSpaceId &&
            db.FullWorthSpaceMembers.Any(m => m.FullWorthSpaceId == fullWorthSpaceId && m.UserId == userId) &&
            (item.Purchase.Visibility != "private" || item.Purchase.CreatedByUserId == userId) &&
            (!item.Purchase.PaymentLinks.Any() || item.Purchase.PaymentLinks.Any(link =>
                db.Transactions.Any(tx => tx.Id == link.TransactionId && db.Accounts.Any(account =>
                    account.Id == tx.AccountId && account.FullWorthSpaceId == fullWorthSpaceId && account.Owners.Any(owner => owner.UserId == userId))))) &&
            (item.Purchase.TransactionId == null || db.Transactions.Any(tx => tx.Id == item.Purchase.TransactionId && db.Accounts.Any(account =>
                account.Id == tx.AccountId && account.FullWorthSpaceId == fullWorthSpaceId && account.Owners.Any(owner => owner.UserId == userId)))));

    private async Task<string?> ValidateWriteAsync(Guid fullWorthSpaceId, ProductWrite request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.CanonicalName)) return "Product name is required.";
        if (request.CanonicalName.Trim().Length > 500) return "Product name is too long.";
        if (request.DefaultCategoryId.HasValue && !await db.Categories.AsNoTracking().AnyAsync(x => x.Id == request.DefaultCategoryId && x.FullWorthSpaceId == fullWorthSpaceId, ct))
            return "Product category must belong to the FullWorth Space.";
        if (request.DefaultPackageQuantity is <= 0m) return "Package quantity must be greater than zero.";
        return null;
    }

    private static void Apply(Product entity, ProductWrite request)
    {
        entity.CanonicalName = request.CanonicalName.Trim();
        entity.Brand = Clean(request.Brand);
        entity.DefaultCategoryId = request.DefaultCategoryId;
        entity.DefaultQuantityUnit = Clean(request.DefaultQuantityUnit)?.ToLowerInvariant();
        entity.DefaultPackageQuantity = request.DefaultPackageQuantity;
        entity.DefaultPackageUnit = Clean(request.DefaultPackageUnit)?.ToLowerInvariant();
        entity.ImageReference = Clean(request.ImageReference);
        entity.Notes = Clean(request.Notes);
        entity.UpdatedAt = DateTimeOffset.UtcNow;
    }

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var sb = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var c in value.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormKD))
        {
            if (char.IsLetterOrDigit(c))
            {
                if (pendingSpace && sb.Length > 0) sb.Append(' ');
                sb.Append(c);
                pendingSpace = false;
            }
            else pendingSpace = true;
        }
        return sb.ToString();
    }

    private static string NormalizeToken(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim().ToLowerInvariant();
    private static string Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null! : value.Trim();
    private static string NormalizeBarcode(string? value) => new((value ?? string.Empty).Where(char.IsLetterOrDigit).ToArray());
    private static string NormalizeBarcodeStandard(string? standard, string code)
    {
        var normalized = NormalizeToken(standard, "unknown");
        if (normalized != "unknown") return normalized;
        return code.Length switch { 8 => "EAN8", 12 => "UPC", 13 => "EAN13", 14 => "GTIN", _ => "unknown" };
    }
}

public static class ProductEndpoints
{
    public static IEndpointRouteBuilder MapProductEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/products").WithTags("Products");
        group.MapGet("/", async (Guid fullWorthSpaceId, string? query, Guid? categoryId, string? brand, bool? includeArchived, int? offset, int? limit, FullWorth.Backend.Security.CurrentUserContext user, ProductService service, CancellationToken ct) =>
        {
            var value = await service.ListAsync(user.RequireUserId(), fullWorthSpaceId, query, categoryId, brand, includeArchived == true, offset ?? 0, limit ?? 100, ct);
            return value is null ? Results.NotFound() : Results.Ok(value);
        });
        group.MapGet("/{id:guid}", async (Guid id, Guid fullWorthSpaceId, FullWorth.Backend.Security.CurrentUserContext user, ProductService service, CancellationToken ct) =>
        {
            var value = await service.GetAsync(user.RequireUserId(), fullWorthSpaceId, id, ct);
            return value is null ? Results.NotFound() : Results.Ok(value);
        });
        group.MapPost("/", async (Guid fullWorthSpaceId, ProductWrite request, FullWorth.Backend.Security.CurrentUserContext user, ProductService service, CancellationToken ct) => Map(await service.CreateAsync(user.RequireUserId(), fullWorthSpaceId, request, ct), true));
        group.MapPatch("/{id:guid}", async (Guid id, Guid fullWorthSpaceId, ProductWrite request, FullWorth.Backend.Security.CurrentUserContext user, ProductService service, CancellationToken ct) => Map(await service.UpdateAsync(user.RequireUserId(), fullWorthSpaceId, id, request, ct)));
        group.MapDelete("/{id:guid}", async (Guid id, Guid fullWorthSpaceId, FullWorth.Backend.Security.CurrentUserContext user, ProductService service, CancellationToken ct) =>
            (await service.ArchiveAsync(user.RequireUserId(), fullWorthSpaceId, id, true, ct)) == PurchaseMutationResult.Success ? Results.NoContent() : Results.NotFound());
        group.MapPost("/{id:guid}/restore", async (Guid id, Guid fullWorthSpaceId, FullWorth.Backend.Security.CurrentUserContext user, ProductService service, CancellationToken ct) =>
            (await service.ArchiveAsync(user.RequireUserId(), fullWorthSpaceId, id, false, ct)) == PurchaseMutationResult.Success ? Results.NoContent() : Results.NotFound());
        group.MapGet("/{id:guid}/history", async (Guid id, Guid fullWorthSpaceId, DateOnly? from, DateOnly? to, FullWorth.Backend.Security.CurrentUserContext user, ProductService service, CancellationToken ct) =>
        {
            var value = await service.HistoryAsync(user.RequireUserId(), fullWorthSpaceId, id, from, to, ct);
            return value is null ? Results.NotFound() : Results.Ok(value);
        });
        group.MapGet("/match", async (Guid fullWorthSpaceId, string? barcode, string? name, string? brand, Guid? merchantId, FullWorth.Backend.Security.CurrentUserContext user, ProductService service, CancellationToken ct) =>
        {
            var value = await service.MatchAsync(user.RequireUserId(), fullWorthSpaceId, barcode, name, brand, merchantId, ct);
            return value is null ? Results.NotFound() : Results.Ok(value);
        });
        group.MapPost("/{id:guid}/aliases", async (Guid id, Guid fullWorthSpaceId, ProductAliasWrite request, FullWorth.Backend.Security.CurrentUserContext user, ProductService service, CancellationToken ct) => Map(await service.AddAliasAsync(user.RequireUserId(), fullWorthSpaceId, id, request, ct), true));
        group.MapDelete("/{id:guid}/aliases/{aliasId:guid}", async (Guid id, Guid aliasId, Guid fullWorthSpaceId, FullWorth.Backend.Security.CurrentUserContext user, ProductService service, CancellationToken ct) =>
            (await service.RemoveAliasAsync(user.RequireUserId(), fullWorthSpaceId, id, aliasId, ct)) == PurchaseMutationResult.Success ? Results.NoContent() : Results.NotFound());
        group.MapPost("/{id:guid}/barcodes", async (Guid id, Guid fullWorthSpaceId, ProductBarcodeWrite request, FullWorth.Backend.Security.CurrentUserContext user, ProductService service, CancellationToken ct) => Map(await service.AddBarcodeAsync(user.RequireUserId(), fullWorthSpaceId, id, request, ct), true));
        group.MapDelete("/{id:guid}/barcodes/{barcodeId:guid}", async (Guid id, Guid barcodeId, Guid fullWorthSpaceId, FullWorth.Backend.Security.CurrentUserContext user, ProductService service, CancellationToken ct) =>
            (await service.RemoveBarcodeAsync(user.RequireUserId(), fullWorthSpaceId, id, barcodeId, ct)) == PurchaseMutationResult.Success ? Results.NoContent() : Results.NotFound());
        group.MapPost("/merge", async (Guid fullWorthSpaceId, ProductMergeRequest request, FullWorth.Backend.Security.CurrentUserContext user, ProductService service, CancellationToken ct) =>
        {
            var result = await service.MergeAsync(user.RequireUserId(), fullWorthSpaceId, request, ct);
            return result.Result switch
            {
                PurchaseMutationResult.Success => Results.NoContent(),
                PurchaseMutationResult.Invalid => Results.BadRequest(new { error = result.Error }),
                _ => Results.NotFound()
            };
        });
        return app;
    }

    private static IResult Map((PurchaseMutationResult Result, object? Value, string? Error) outcome, bool created = false) => outcome.Result switch
    {
        PurchaseMutationResult.Success when created => Results.Created(string.Empty, outcome.Value),
        PurchaseMutationResult.Success => Results.Ok(outcome.Value),
        PurchaseMutationResult.Invalid => Results.BadRequest(new { error = outcome.Error }),
        PurchaseMutationResult.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
        _ => Results.NotFound()
    };
}
