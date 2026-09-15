using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Audit;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Purchases;

/// <summary>Ein Produkt in der Liste, mit seinem ersten Strichcode und der Zahl seiner Schreibweisen.</summary>
public sealed record ProductListRow(
    Guid Id, string CanonicalName, string? Brand, string? Barcode, Guid? DefaultCategoryId, string? UnitKind,
    decimal? UnitSize, int AliasCount);

/// <summary>Ein gefundenes Produkt samt der Auskunft, woher der Treffer stammt.</summary>
public sealed record ProductSuggestionRow(
    Guid Id, string CanonicalName, string? Brand, Guid? DefaultCategoryId, string Source);

/// <summary>Ein Kauf dieses Produkts.</summary>
public sealed record ProductHistoryRow(
    Guid PurchaseItemId, DateOnly? PurchaseDate, string? Merchant, decimal? Quantity, decimal? PackageQuantity,
    string? PackageUnit, decimal? Total, string? Currency, decimal? ComparableUnitPrice, bool ComparisonSafe);

/// <summary>
/// Produkte, ihre Schreibweisen, ihre Strichcodes und ihre Kaufhistorie.
///
/// Ein Produkt wird nie geloescht, nur stillgelegt: an ihm haengen Positionen aus echten Kaeufen, und
/// eine Kaufhistorie darf nicht verschwinden, weil jemand ein Produkt aufraeumt.
///
/// Ein Strichcode gehoert genau einem Produkt - deshalb prueft <see cref="BarcodeTakenAsync"/> ueber
/// Produktgrenzen hinweg und nicht nur innerhalb des Bereichs.
/// </summary>
public sealed class ProductIdentityStore(FullWorthDbContext db, AuditService audit)
{
    public Task<List<ProductListRow>> ListAsync(Guid space, CancellationToken ct) =>
        db.Products.AsNoTracking()
            .Where(product => product.FullWorthSpaceId == space && !product.IsArchived)
            .OrderBy(product => product.CanonicalName)
            .Select(product => new ProductListRow(
                product.Id,
                product.CanonicalName,
                product.Brand,
                product.Barcodes.OrderBy(barcode => barcode.CreatedAt).Select(barcode => barcode.Code).FirstOrDefault(),
                product.DefaultCategoryId,
                product.DefaultPackageUnit ?? product.DefaultQuantityUnit,
                product.DefaultPackageQuantity,
                product.Aliases.Count))
            .ToListAsync(ct);

    public Task<bool> ExistsAsync(Guid space, Guid id, CancellationToken ct) =>
        db.Products.AsNoTracking().AnyAsync(product =>
            product.Id == id && product.FullWorthSpaceId == space && !product.IsArchived, ct);

    public Task<bool> CategoryUsableAsync(Guid space, Guid categoryId, CancellationToken ct) =>
        db.Categories.AsNoTracking().AnyAsync(category =>
            category.Id == categoryId && category.FullWorthSpaceId == space && !category.IsArchived, ct);

    public Task<bool> BarcodeTakenAsync(string code, Guid productId, CancellationToken ct) =>
        db.ProductBarcodes.AsNoTracking().AnyAsync(barcode =>
            barcode.Code == code && barcode.ProductId != productId, ct);

    /// <summary>
    /// Anlegen oder aendern. Die Oberflaeche kennt genau einen Strichcode je Produkt; das Datenmodell
    /// erlaubt mehrere. Darum wird beim Speichern der erste ueberschrieben und der Rest entfernt -
    /// sonst wuerde ein zweiter, unsichtbarer Strichcode stehenbleiben.
    /// </summary>
    public async Task<bool> SaveAsync(
        Guid userId, Guid space, Guid id, ProductIdentityWrite request, string? unit, string? barcode, bool update,
        CancellationToken ct)
    {
        Product product;
        if (update)
        {
            var existing = await db.Products.Include(candidate => candidate.Barcodes)
                .SingleOrDefaultAsync(candidate => candidate.Id == id && candidate.FullWorthSpaceId == space, ct);
            if (existing is null) return false;
            product = existing;
        }
        else
        {
            product = new Product { Id = id, FullWorthSpaceId = space, CreatedAt = DateTimeOffset.UtcNow };
            db.Products.Add(product);
        }

        product.CanonicalName = request.CanonicalName.Trim();
        product.Brand = string.IsNullOrWhiteSpace(request.Brand) ? null : request.Brand.Trim();
        product.DefaultCategoryId = request.DefaultCategoryId;
        product.DefaultQuantityUnit = "piece";
        product.DefaultPackageQuantity = request.UnitSize;
        product.DefaultPackageUnit = unit;
        product.IsArchived = false;
        product.UpdatedAt = DateTimeOffset.UtcNow;

        var existingBarcodes = update ? product.Barcodes.ToList() : [];
        if (barcode is null)
        {
            if (existingBarcodes.Count > 0) db.ProductBarcodes.RemoveRange(existingBarcodes);
        }
        else if (existingBarcodes.FirstOrDefault() is { } first)
        {
            first.Code = barcode;
            first.Standard = BarcodeStandard(barcode);
            if (existingBarcodes.Count > 1) db.ProductBarcodes.RemoveRange(existingBarcodes.Skip(1));
        }
        else
        {
            db.ProductBarcodes.Add(new ProductBarcode
            {
                ProductId = id, Code = barcode, Standard = BarcodeStandard(barcode)
            });
        }

        audit.Record(space, userId, update ? "product.updated" : "product.created", "Product", id);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> ArchiveAsync(Guid userId, Guid space, Guid id, CancellationToken ct)
    {
        var product = await db.Products.SingleOrDefaultAsync(
            candidate => candidate.Id == id && candidate.FullWorthSpaceId == space, ct);
        if (product is null) return false;

        product.IsArchived = true;
        product.UpdatedAt = DateTimeOffset.UtcNow;
        audit.Record(space, userId, "product.archived", "Product", id);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Eine Schreibweise ohne Haendler gilt ueberall. Gibt es sie schon, wird sie ueberschrieben statt
    /// ein zweites Mal angelegt - sonst haette dasselbe Wort zwei Eintraege mit verschiedener Herkunft.
    /// </summary>
    public async Task SaveAliasAsync(
        Guid userId, Guid space, Guid productId, string alias, string normalized, string aliasType,
        CancellationToken ct)
    {
        var existing = await db.ProductAliases.SingleOrDefaultAsync(candidate =>
            candidate.ProductId == productId && candidate.MerchantId == null
            && candidate.NormalizedAlias == normalized, ct);

        if (existing is null)
            db.ProductAliases.Add(new ProductAlias
            {
                ProductId = productId, Alias = alias, NormalizedAlias = normalized, AliasType = aliasType,
                CreatedAt = DateTimeOffset.UtcNow
            });
        else
        {
            existing.Alias = alias;
            existing.AliasType = aliasType;
        }

        audit.Record(space, userId, "product.alias.updated", "Product", productId);
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> DeleteAliasAsync(
        Guid userId, Guid space, Guid productId, Guid aliasId, CancellationToken ct)
    {
        var alias = await db.ProductAliases.Include(candidate => candidate.Product)
            .SingleOrDefaultAsync(candidate =>
                candidate.Id == aliasId && candidate.ProductId == productId
                && candidate.Product.FullWorthSpaceId == space, ct);
        if (alias is null) return false;

        db.ProductAliases.Remove(alias);
        audit.Record(space, userId, "product.alias.deleted", "Product", productId);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public Task<ProductSuggestionRow?> FindByAliasAsync(Guid space, string normalized, CancellationToken ct) =>
        db.ProductAliases.AsNoTracking()
            .Where(alias => alias.NormalizedAlias == normalized
                && alias.Product.FullWorthSpaceId == space && !alias.Product.IsArchived)
            .Select(alias => new ProductSuggestionRow(
                alias.Product.Id, alias.Product.CanonicalName, alias.Product.Brand,
                alias.Product.DefaultCategoryId, alias.AliasType))
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// Ein Produkt, dessen Name auf dasselbe hinauslaeuft wie der gesuchte Text - Gross- und
    /// Kleinschreibung und alles, was kein Buchstabe und keine Ziffer ist, zaehlen nicht.
    ///
    /// Der Vergleich steht bewusst in SQL. Er lief bis 2026-09-15 im Arbeitsspeicher ueber die ersten
    /// 2000 Produkte des Bereichs - wer mehr hat, bekam ab dem 2001. Produkt nie einen Treffer, ohne
    /// dass irgendwo etwas davon stand.
    /// </summary>
    public async Task<ProductSuggestionRow?> FindByCanonicalNameAsync(
        Guid space, string normalized, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        await using var command = RawSql.Command(connection, """
SELECT "Id","CanonicalName","Brand","DefaultCategoryId" FROM "Products"
WHERE "FullWorthSpaceId"=@space AND "IsArchived"=false
  AND lower(regexp_replace("CanonicalName",'[^[:alnum:]]','','g'))=@normalized
ORDER BY "CanonicalName" LIMIT 1
""", ("@space", space), ("@normalized", normalized));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        return new ProductSuggestionRow(
            RawSql.Guid(reader, "Id"), RawSql.String(reader, "CanonicalName"),
            RawSql.NullableString(reader, "Brand"), RawSql.NullableGuid(reader, "DefaultCategoryId"),
            "canonical_name");
    }

    /// <summary>
    /// Die Kaeufe eines Produkts. Ein privater Kauf eines anderen Mitglieds bleibt aussen vor, und
    /// ein noch nicht bestaetigter Kauf auch - eine Preisreihe aus ungeprueften Belegen waere falsch.
    /// </summary>
    public Task<List<ProductHistoryRow>> HistoryAsync(
        Guid space, Guid userId, Guid productId, CancellationToken ct) =>
        db.PurchaseItems.AsNoTracking()
            .Where(item => item.ProductId == productId && item.Purchase.FullWorthSpaceId == space
                && (item.Purchase.Visibility != "private" || item.Purchase.CreatedByUserId == userId)
                && (item.Purchase.ReviewState == "confirmed" || item.Purchase.Status == "confirmed"))
            .OrderByDescending(item => item.Purchase.PurchaseDate).ThenByDescending(item => item.CreatedAt)
            .Select(item => new ProductHistoryRow(
                item.Id, item.Purchase.PurchaseDate, item.Purchase.Merchant, item.Quantity, item.PackageQuantity,
                item.PackageUnit, item.TotalPrice, item.Currency, item.BaseUnitPrice,
                item.BaseUnitPrice != null && item.PackageQuantity != null && item.PackageUnit != null))
            .Take(500)
            .ToListAsync(ct);

    public Task<Guid?> PurchaseOfItemAsync(Guid purchaseItemId, CancellationToken ct) =>
        db.PurchaseItems.AsNoTracking()
            .Where(item => item.Id == purchaseItemId)
            .Select(item => (Guid?)item.PurchaseId)
            .SingleOrDefaultAsync(ct);

    /// <summary>
    /// Das Produkt einer Kaufposition setzen oder loesen. Beides markiert die Position als von Hand
    /// korrigiert, damit die automatische Zuordnung sie nicht wieder ueberschreibt.
    /// </summary>
    public async Task SetItemProductAsync(
        Guid userId, Guid space, Guid purchaseItemId, Guid? productId, string auditAction, CancellationToken ct)
    {
        var item = await db.PurchaseItems.SingleAsync(candidate => candidate.Id == purchaseItemId, ct);
        item.ProductId = productId;
        item.IsManuallyCorrected = true;
        item.UpdatedAt = DateTimeOffset.UtcNow;

        audit.Record(space, userId, auditAction, "PurchaseItem", purchaseItemId);
        await db.SaveChangesAsync(ct);
    }

    private static string BarcodeStandard(string code) => code.Length switch
    {
        8 => "ean8", 12 => "upc", 13 => "ean13", 14 => "gtin14", _ => "unknown"
    };
}
