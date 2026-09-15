using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Audit;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Purchases;

/// <summary>Eine von Hand kategorisierte Kaufzeile - der Rohstoff, aus dem ein Vorschlag entsteht.</summary>
public sealed record CategorizedItemName(string Name, Guid CategoryId);

/// <summary>Was schon zu einem normalisierten Namen hinterlegt ist.</summary>
public sealed record LearnedAlias(string NormalizedAlias, Guid ProductId, string ProductName, Guid? DefaultCategoryId);

/// <summary>Eine Zielkategorie, soweit die Annahme eines Vorschlags sie braucht.</summary>
public sealed record LearningTargetCategory(string Key, bool IsSystem);

/// <summary>
/// Woraus das Produktwissen lernt, und wohin es das Gelernte schreibt.
///
/// Zwei Grenzen stecken in der Leseabfrage: gelernt wird nur aus Zeilen, die ein Mensch selbst
/// kategorisiert hat (<c>CategorizationSource == "manual"</c>) - aus einer automatischen Zuordnung zu
/// lernen hiesse, den eigenen Vorschlag zu bestaetigen. Und ein privater Kauf zaehlt nur fuer den,
/// der ihn angelegt hat.
/// </summary>
public sealed class ProductLearningStore(FullWorthDbContext db, AuditService audit)
{
    /// <summary>Genug, um ein Muster zu sehen; mehr macht die Gruppierung im Speicher teuer.</summary>
    private const int MaxSamples = 20_000;

    public Task<List<CategorizedItemName>> ManuallyCategorizedItemsAsync(
        Guid userId, Guid fullWorthSpaceId, CancellationToken ct) =>
        db.PurchaseItems.AsNoTracking()
            .Where(item => item.CategoryId.HasValue
                        && item.CategorizationSource == "manual"
                        && item.Purchase.FullWorthSpaceId == fullWorthSpaceId
                        && (item.Purchase.Visibility != "private" || item.Purchase.CreatedByUserId == userId))
            .Select(item => new CategorizedItemName(item.Name, item.CategoryId!.Value))
            .Take(MaxSamples)
            .ToListAsync(ct);

    public Task<Dictionary<Guid, string>> CategoryNamesAsync(
        Guid fullWorthSpaceId, Guid[] categoryIds, CancellationToken ct) =>
        db.Categories.AsNoTracking()
            .Where(category => category.FullWorthSpaceId == fullWorthSpaceId && categoryIds.Contains(category.Id))
            .ToDictionaryAsync(category => category.Id, category => category.Name, ct);

    public Task<List<LearnedAlias>> AliasesForAsync(
        Guid fullWorthSpaceId, IReadOnlySet<string> normalizedTexts, CancellationToken ct) =>
        db.ProductAliases.AsNoTracking()
            .Where(alias => alias.Product.FullWorthSpaceId == fullWorthSpaceId
                         && normalizedTexts.Contains(alias.NormalizedAlias))
            .Select(alias => new LearnedAlias(
                alias.NormalizedAlias, alias.ProductId, alias.Product.CanonicalName, alias.Product.DefaultCategoryId))
            .ToListAsync(ct);

    /// <summary>Eine archivierte Kategorie ist kein gueltiges Ziel mehr.</summary>
    public Task<LearningTargetCategory?> FindTargetCategoryAsync(
        Guid fullWorthSpaceId, Guid categoryId, CancellationToken ct) =>
        db.Categories.AsNoTracking()
            .Where(category => category.Id == categoryId
                            && category.FullWorthSpaceId == fullWorthSpaceId
                            && !category.IsArchived)
            .Select(category => new LearningTargetCategory(category.Key, category.IsSystem))
            .SingleOrDefaultAsync(ct);

    public Task<Product?> FindProductAsync(Guid fullWorthSpaceId, Guid productId, CancellationToken ct) =>
        db.Products.Include(product => product.Aliases)
            .SingleOrDefaultAsync(product => product.Id == productId
                                          && product.FullWorthSpaceId == fullWorthSpaceId
                                          && !product.IsArchived, ct);

    public Task<Product?> FindProductByAliasAsync(
        Guid fullWorthSpaceId, string normalizedAlias, CancellationToken ct) =>
        db.ProductAliases
            .Where(alias => alias.NormalizedAlias == normalizedAlias
                         && alias.Product.FullWorthSpaceId == fullWorthSpaceId
                         && !alias.Product.IsArchived)
            .Select(alias => alias.Product)
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// Haelt fest, dass dieser Name zu dieser Kategorie gehoert: das Produkt bekommt die Kategorie als
    /// Standard, und der Name wird als Alias dazu vermerkt.
    ///
    /// Zwei SaveChanges, und das ist kein Versehen - der Alias braucht die Id des Produkts, die erst
    /// beim ersten Speichern feststeht.
    /// </summary>
    public async Task<Product> LearnAsync(
        Guid userId, Guid fullWorthSpaceId, Product? existing, string normalizedAlias, string displayText,
        string? canonicalName, Guid categoryId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var product = existing;
        if (product is null)
        {
            product = new Product
            {
                FullWorthSpaceId = fullWorthSpaceId,
                CanonicalName = string.IsNullOrWhiteSpace(canonicalName) ? displayText.Trim() : canonicalName.Trim(),
                DefaultCategoryId = categoryId,
                DefaultQuantityUnit = "piece",
                CreatedAt = now,
                UpdatedAt = now
            };
            db.Products.Add(product);
        }
        else
        {
            product.DefaultCategoryId = categoryId;
            product.UpdatedAt = now;
        }
        await db.SaveChangesAsync(ct);

        var alias = await db.ProductAliases.SingleOrDefaultAsync(row =>
            row.ProductId == product.Id && row.MerchantId == null && row.NormalizedAlias == normalizedAlias, ct);
        if (alias is null)
            db.ProductAliases.Add(new ProductAlias
            {
                ProductId = product.Id,
                Alias = displayText.Trim(),
                NormalizedAlias = normalizedAlias,
                AliasType = "learning",
                CreatedAt = now
            });
        else
        {
            alias.Alias = displayText.Trim();
            alias.AliasType = "learning";
        }

        audit.Record(fullWorthSpaceId, userId, "product.category.learned", "Product", product.Id);
        await db.SaveChangesAsync(ct);
        return product;
    }

    public Task<List<string>> BarcodesAsync(Guid productId, CancellationToken ct) =>
        db.ProductBarcodes.AsNoTracking()
            .Where(barcode => barcode.ProductId == productId)
            .OrderBy(barcode => barcode.Code)
            .Select(barcode => barcode.Code)
            .ToListAsync(ct);

    public Task<bool> ProductExistsAsync(Guid fullWorthSpaceId, Guid productId, CancellationToken ct) =>
        db.Products.AsNoTracking()
            .AnyAsync(product => product.Id == productId
                              && product.FullWorthSpaceId == fullWorthSpaceId
                              && !product.IsArchived, ct);

    public Task<List<ProductAlias>> ListAliasesAsync(Guid productId, CancellationToken ct) =>
        db.ProductAliases.AsNoTracking()
            .Where(alias => alias.ProductId == productId)
            .OrderBy(alias => alias.NormalizedAlias)
            .ToListAsync(ct);
}
