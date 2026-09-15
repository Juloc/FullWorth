using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Audit;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Purchases;

/// <summary>Der Anzeigename und die Standardkategorie, die zu einem normalisierten Produktnamen hinterlegt sind.</summary>
public sealed record ProductAliasRow(string? DisplayName, Guid? CategoryId);

/// <summary>
/// Was ueber gekaufte Produkte bekannt ist: die Kaeufe, aus denen sich Preise ablesen lassen, und die
/// Namen, die der Benutzer ihnen gegeben hat.
///
/// Die Sichtbarkeitsgrenze steckt in beiden Kaufabfragen und ist der Grund fuer ihre Laenge: ein Kauf
/// ohne verknuepfte Buchung gehoert dem Haushalt und zaehlt fuer alle; haengt er an einer Buchung,
/// zaehlt er nur, wenn der Fragende deren Konto sehen darf.
/// </summary>
public sealed class ProductIntelligenceStore(FullWorthDbContext db, AuditService audit)
{
    /// <summary>Weiter zurueck sagt ueber das heutige Einkaufsverhalten nichts mehr.</summary>
    private const int SummaryPurchaseLimit = 5000;

    /// <summary>Die juengsten Kaeufe, fuer die Uebersicht ueber alle Produkte.</summary>
    public Task<List<Purchase>> RecentPurchasesAsync(
        Guid fullWorthSpaceId, IReadOnlySet<Guid> visibleAccountIds, CancellationToken ct) =>
        Visible(fullWorthSpaceId, visibleAccountIds)
            .OrderByDescending(purchase => purchase.PurchaseDate)
            .Take(SummaryPurchaseLimit)
            .ToListAsync(ct);

    /// <summary>Alle Kaeufe - die Historie eines einzelnen Produkts darf nicht bei 5000 abschneiden.</summary>
    public Task<List<Purchase>> AllPurchasesAsync(
        Guid fullWorthSpaceId, IReadOnlySet<Guid> visibleAccountIds, CancellationToken ct) =>
        Visible(fullWorthSpaceId, visibleAccountIds).ToListAsync(ct);

    public async Task<Dictionary<string, ProductAliasRow>> AliasesAsync(Guid fullWorthSpaceId, CancellationToken ct)
    {
        var result = new Dictionary<string, ProductAliasRow>(StringComparer.OrdinalIgnoreCase);
        var connection = await RawSql.OpenAsync(db, ct);
        await using var cmd = RawSql.Command(connection,
            "SELECT a.\"NormalizedAlias\" AS \"NormalizedName\", p.\"CanonicalName\" AS \"DisplayName\", p.\"DefaultCategoryId\" AS \"CategoryId\" " +
            "FROM \"ProductAliases\" a JOIN \"Products\" p ON p.\"Id\"=a.\"ProductId\" WHERE p.\"FullWorthSpaceId\"=@space",
            ("@space", fullWorthSpaceId));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result[RawSql.String(reader, "NormalizedName")] = new ProductAliasRow(
                RawSql.NullableString(reader, "DisplayName"), RawSql.NullableGuid(reader, "CategoryId"));
        return result;
    }

    public Task<bool> CategoryExistsAsync(Guid fullWorthSpaceId, Guid categoryId, CancellationToken ct) =>
        db.Categories.AsNoTracking()
            .AnyAsync(category => category.Id == categoryId && category.FullWorthSpaceId == fullWorthSpaceId, ct);

    /// <summary>
    /// Benennt das Produkt hinter diesem Namen um, oder legt es an.
    ///
    /// Das kanonische Modell: ein Produkt traegt den Anzeigenamen und die Standardkategorie, und
    /// Aliase verbinden normalisierte Namen damit. Gefunden wird ueber einen passenden Alias in
    /// diesem Space - gibt es keinen, entstehen Produkt und manueller Alias zusammen.
    /// </summary>
    public async Task UpsertAliasAsync(
        Guid userId, Guid fullWorthSpaceId, string normalizedAlias, string displayName, Guid? categoryId,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var product = await db.Set<ProductAlias>()
            .Where(alias => alias.NormalizedAlias == normalizedAlias
                         && alias.Product.FullWorthSpaceId == fullWorthSpaceId)
            .Select(alias => alias.Product)
            .FirstOrDefaultAsync(ct);

        if (product is not null)
        {
            product.CanonicalName = displayName;
            product.DefaultCategoryId = categoryId;
            product.UpdatedAt = now;
        }
        else
        {
            product = new Product
            {
                FullWorthSpaceId = fullWorthSpaceId,
                CanonicalName = displayName,
                DefaultCategoryId = categoryId,
                CreatedAt = now,
                UpdatedAt = now
            };
            db.Add(product);
            db.Add(new ProductAlias
            {
                ProductId = product.Id,
                Alias = displayName,
                NormalizedAlias = normalizedAlias,
                AliasType = "manual",
                CreatedAt = now
            });
        }

        audit.Record(fullWorthSpaceId, userId, "product.alias.updated", "ProductAlias", product.Id);
        await db.SaveChangesAsync(ct);
    }

    private IQueryable<Purchase> Visible(Guid fullWorthSpaceId, IReadOnlySet<Guid> visibleAccountIds) =>
        db.Purchases.AsNoTracking()
            .Where(purchase => purchase.FullWorthSpaceId == fullWorthSpaceId
                            && (purchase.TransactionId == null || db.Transactions.Any(transaction =>
                                   transaction.Id == purchase.TransactionId.Value
                                   && visibleAccountIds.Contains(transaction.AccountId))))
            .Include(purchase => purchase.Items);
}
