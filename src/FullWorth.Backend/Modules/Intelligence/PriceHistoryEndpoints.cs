using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Intelligence;

/// <summary>
/// Was dieser Haushalt selbst fuer denselben Artikel bezahlt hat.
///
/// Hier stand einmal ein Preisvergleich gegen die Cloud: die eigene Historie war nur die Halbzeile
/// unter einem Median aus fremden Haushalten, und die Antwort trug ein available/reason-Feldpaar,
/// das in neun von zehn Faellen erklaerte, warum nichts da ist. Der fremde Median brauchte fuenf
/// beitragende Instanzen und ist mit der Cloud weggefallen. Die eigene Historie brauchte nie
/// jemanden - sie steht in dieser Datenbank - und bleibt deshalb genau so, wie sie war.
/// </summary>
public static class PriceHistoryEndpoints
{
    public static IEndpointRouteBuilder MapPriceHistoryEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/intelligence/prices")
            .WithTags("Intelligence Prices");

        group.MapGet("/purchase-items/{purchaseItemId:guid}", async (
            Guid purchaseItemId,
            CurrentUserContext currentUser,
            SpaceAccess space,
            PriceHistoryStore prices,
            CancellationToken ct) =>
        {
            var userId = currentUser.RequireUserId();

            var item = await prices.FindPurchaseItemAsync(purchaseItemId, ct);
            if (item is null)
                return Results.NotFound();

            if (!await space.IsMemberAsync(userId, item.FullWorthSpaceId, ct))
                return Results.NotFound();

            return Results.Ok(new
            {
                local = await LocalHistoryAsync(item, PriceHistoryStore.NormalizeCurrency(item.Currency), prices, ct)
            });
        });

        return app;
    }

    private static async Task<object> LocalHistoryAsync(
        PricedPurchaseItem item, string? currency, PriceHistoryStore prices, CancellationToken ct)
    {
        if (currency is null) return new { count = 0 };

        var observations = await prices.LocalHistoryAsync(
            item.FullWorthSpaceId, item.ProductId, item.Barcode, currency, ct);

        var values = observations
            .Select(row => EffectiveUnitPrice(row.UnitPrice, row.BaseUnitPrice, row.Quantity, row.TotalPrice))
            .Where(price => price is > 0m)
            .Select(price => price!.Value)
            .OrderBy(price => price)
            .ToArray();
        if (values.Length == 0) return new { count = 0 };

        return new
        {
            count = values.Length,
            median = Math.Round(Median(values), 4),
            mean = Math.Round(values.Average(), 4),
            min = values[0],
            max = values[^1]
        };
    }

    private static decimal? EffectiveUnitPrice(
        decimal? unitPrice,
        decimal? baseUnitPrice,
        decimal quantity,
        decimal totalPrice)
    {
        if (unitPrice is > 0m)
            return unitPrice;
        if (baseUnitPrice is > 0m)
            return baseUnitPrice;
        if (quantity > 0m && totalPrice > 0m)
            return totalPrice / quantity;
        return null;
    }

    private static decimal Median(IReadOnlyList<decimal> sorted)
    {
        if (sorted.Count == 0) return 0m;
        var middle = sorted.Count / 2;
        return sorted.Count % 2 == 1
            ? sorted[middle]
            : (sorted[middle - 1] + sorted[middle]) / 2m;
    }
}
