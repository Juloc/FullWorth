using FullWorth.Backend.Modules.Merchants;
using FullWorth.Backend.Security;

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
        Guid fullWorthSpaceId, CurrentUserContext currentUser, SpaceAccess space,
        ProductIntelligenceStore store, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await space.IsMemberAsync(userId, fullWorthSpaceId, ct)) return Results.NotFound();

        var visibleAccounts = await space.VisibleAccountIdsAsync(userId, fullWorthSpaceId, ct);
        var purchases = await store.RecentPurchasesAsync(fullWorthSpaceId, visibleAccounts, ct);
        var aliases = await store.AliasesAsync(fullWorthSpaceId, ct);

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
        Guid fullWorthSpaceId, string name, CurrentUserContext currentUser, SpaceAccess space,
        ProductIntelligenceStore store, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await space.IsMemberAsync(userId, fullWorthSpaceId, ct)) return Results.NotFound();

        var normalized = NormalizeProduct(name);
        var visibleAccounts = await space.VisibleAccountIdsAsync(userId, fullWorthSpaceId, ct);
        var purchases = await store.AllPurchasesAsync(fullWorthSpaceId, visibleAccounts, ct);

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
        CurrentUserContext currentUser, SpaceAccess space, ProductIntelligenceStore store,
        CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await space.IsOwnerAsync(userId, fullWorthSpaceId, ct)) return Results.StatusCode(403);

        var normalized = NormalizeProduct(normalizedName);
        if (string.IsNullOrWhiteSpace(request.DisplayName) || normalized.Length < 2) return Results.BadRequest();
        if (request.CategoryId.HasValue &&
            !await store.CategoryExistsAsync(fullWorthSpaceId, request.CategoryId.Value, ct))
            return Results.BadRequest(new { error = "Category is invalid." });

        await store.UpsertAliasAsync(
            userId, fullWorthSpaceId, normalized, request.DisplayName.Trim(), request.CategoryId, ct);
        return Results.NoContent();
    }

    private static string NormalizeProduct(string? value) => MerchantNormalization.Normalize(value)?.ToLowerInvariant() ?? string.Empty;
}
