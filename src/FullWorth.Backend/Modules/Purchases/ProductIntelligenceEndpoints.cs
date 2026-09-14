using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Audit;
using FullWorth.Backend.Modules.Merchants;
using FullWorth.Backend.Security;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Purchases;

public sealed record ProductIntelligenceAliasWrite(string DisplayName, Guid? CategoryId);

/// <summary>Was ueber ein gekauftes Produkt bekannt ist. Kam aus Parity/ExperienceParityModule.
///
/// ProductIntelligenceAliasWrite hiess der Schreibtyp dort; den Namen gibt es in Purchases schon mit anderer Form,
/// deshalb ProductIntelligenceAliasWrite.</summary>
public static class ProductIntelligenceEndpoints
{
    public static IEndpointRouteBuilder MapProductIntelligenceEndpoints(this IEndpointRouteBuilder app)
    {
        var products = app.MapGroup("/api/product-intelligence").WithTags("Purchases");
        products.MapGet("/summary", ProductSummary);
        products.MapGet("/history", ProductHistory);
        products.MapPut("/aliases/{normalizedName}", PutProductAlias);
        return app;
    }

    private static async Task<IResult> ProductSummary(
        Guid fullWorthSpaceId, CurrentUserContext currentUser, FullWorthDbContext db, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await RawSql.IsMemberAsync(db, userId, fullWorthSpaceId, ct)) return Results.NotFound();
        var visibleAccounts = await RawSql.VisibleAccountIdsAsync(db, userId, fullWorthSpaceId, ct);

        var purchases = await db.Purchases.AsNoTracking()
            .Where(purchase => purchase.FullWorthSpaceId == fullWorthSpaceId &&
                (purchase.TransactionId == null || db.Transactions.Any(transaction =>
                    transaction.Id == purchase.TransactionId.Value && visibleAccounts.Contains(transaction.AccountId))))
            .Include(purchase => purchase.Items)
            .OrderByDescending(purchase => purchase.PurchaseDate)
            .Take(5000)
            .ToListAsync(ct);
        var aliases = await LoadAliases(db, fullWorthSpaceId, ct);

        var rows = purchases
            .SelectMany(purchase => purchase.Items.Select(item => new
            {
                Purchase = purchase,
                Item = item,
                Normalized = NormalizeProduct(item.Name)
            }))
            .Where(row => row.Normalized.Length > 1)
            .GroupBy(row => row.Normalized)
            .Select(group =>
            {
                var latest = group.OrderByDescending(row => row.Purchase.PurchaseDate).First();
                aliases.TryGetValue(group.Key, out var alias);
                var unitPrices = group
                    .Where(row => row.Item.Quantity > 0)
                    .Select(row => row.Item.TotalPrice / row.Item.Quantity)
                    .ToArray();
                return new
                {
                    normalizedName = group.Key,
                    displayName = alias?.DisplayName ?? latest.Item.Name,
                    categoryId = alias?.CategoryId,
                    purchases = group.Count(),
                    latestPrice = latest.Item.Quantity == 0 ? latest.Item.TotalPrice : latest.Item.TotalPrice / latest.Item.Quantity,
                    averagePrice = unitPrices.Length == 0 ? 0 : Math.Round(unitPrices.Average(), 2),
                    currency = latest.Item.Currency,
                    lastPurchased = latest.Purchase.PurchaseDate,
                    merchants = group.Select(row => row.Purchase.Merchant).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct().Take(5)
                };
            })
            .OrderByDescending(row => row.purchases)
            .Take(500);

        return Results.Ok(rows);
    }

    private static async Task<IResult> ProductHistory(
        Guid fullWorthSpaceId, string name, CurrentUserContext currentUser, FullWorthDbContext db, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await RawSql.IsMemberAsync(db, userId, fullWorthSpaceId, ct)) return Results.NotFound();
        var visibleAccounts = await RawSql.VisibleAccountIdsAsync(db, userId, fullWorthSpaceId, ct);
        var normalized = NormalizeProduct(name);

        var purchases = await db.Purchases.AsNoTracking()
            .Where(purchase => purchase.FullWorthSpaceId == fullWorthSpaceId &&
                (purchase.TransactionId == null || db.Transactions.Any(transaction =>
                    transaction.Id == purchase.TransactionId.Value && visibleAccounts.Contains(transaction.AccountId))))
            .Include(purchase => purchase.Items)
            .ToListAsync(ct);

        var rows = purchases.SelectMany(purchase => purchase.Items
                .Where(item => NormalizeProduct(item.Name) == normalized)
                .Select(item => new
                {
                    date = purchase.PurchaseDate,
                    merchant = purchase.Merchant,
                    item.Name,
                    item.Quantity,
                    total = item.TotalPrice,
                    unitPrice = item.Quantity == 0 ? item.TotalPrice : item.TotalPrice / item.Quantity,
                    item.Currency,
                    purchaseId = purchase.Id
                }))
            .OrderBy(row => row.date);
        return Results.Ok(rows);
    }

    private static async Task<IResult> PutProductAlias(
        string normalizedName, Guid fullWorthSpaceId, ProductIntelligenceAliasWrite request,
        CurrentUserContext currentUser, FullWorthDbContext db, AuditService audit, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await RawSql.IsOwnerAsync(db, userId, fullWorthSpaceId, ct)) return Results.StatusCode(403);
        var normalized = NormalizeProduct(normalizedName);
        if (string.IsNullOrWhiteSpace(request.DisplayName) || normalized.Length < 2) return Results.BadRequest();
        if (request.CategoryId.HasValue && !await db.Categories.AsNoTracking().AnyAsync(category =>
                category.Id == request.CategoryId.Value && category.FullWorthSpaceId == fullWorthSpaceId, ct))
            return Results.BadRequest(new { error = "Category is invalid." });

        // Canonical model: a product carries the display (CanonicalName) + default category, and aliases
        // link normalized names to it. Upsert by finding an existing product through a matching alias in
        // this space, otherwise create the product and its manual alias.
        var displayName = request.DisplayName.Trim();
        var now = DateTimeOffset.UtcNow;
        var product = await db.Set<ProductAlias>()
            .Where(alias => alias.NormalizedAlias == normalized && alias.Product.FullWorthSpaceId == fullWorthSpaceId)
            .Select(alias => alias.Product)
            .FirstOrDefaultAsync(ct);
        if (product is not null)
        {
            product.CanonicalName = displayName;
            product.DefaultCategoryId = request.CategoryId;
            product.UpdatedAt = now;
        }
        else
        {
            product = new Product
            {
                FullWorthSpaceId = fullWorthSpaceId,
                CanonicalName = displayName,
                DefaultCategoryId = request.CategoryId,
                CreatedAt = now,
                UpdatedAt = now
            };
            db.Add(product);
            db.Add(new ProductAlias
            {
                ProductId = product.Id,
                Alias = displayName,
                NormalizedAlias = normalized,
                AliasType = "manual",
                CreatedAt = now
            });
        }
        audit.Record(fullWorthSpaceId, userId, "product.alias.updated", "ProductAlias", product.Id);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }


    private sealed record AliasRow(string? DisplayName, Guid? CategoryId);

    private static async Task<Dictionary<string, AliasRow>> LoadAliases(
        FullWorthDbContext db, Guid fullWorthSpaceId, CancellationToken ct)
    {
        var result = new Dictionary<string, AliasRow>(StringComparer.OrdinalIgnoreCase);
        var connection = await RawSql.OpenAsync(db, ct);
        await using var cmd = RawSql.Command(connection,
            "SELECT a.\"NormalizedAlias\" AS \"NormalizedName\", p.\"CanonicalName\" AS \"DisplayName\", p.\"DefaultCategoryId\" AS \"CategoryId\" " +
            "FROM \"ProductAliases\" a JOIN \"Products\" p ON p.\"Id\"=a.\"ProductId\" WHERE p.\"FullWorthSpaceId\"=@space",
            ("@space", fullWorthSpaceId));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result[RawSql.String(reader, "NormalizedName")] = new(
                RawSql.NullableString(reader, "DisplayName"), RawSql.NullableGuid(reader, "CategoryId"));
        return result;
    }

    private static string NormalizeProduct(string? value) => MerchantNormalization.Normalize(value)?.ToLowerInvariant() ?? string.Empty;
}
