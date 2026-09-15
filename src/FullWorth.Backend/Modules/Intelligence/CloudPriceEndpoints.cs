using FullWorth.Backend.Data;
using FullWorth.Backend.Security;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Intelligence;

public static class CloudPriceEndpoints
{
    public static IEndpointRouteBuilder MapCloudPriceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/intelligence/prices")
            .WithTags("Intelligence Prices");

        group.MapGet("/purchase-items/{purchaseItemId:guid}", async (
            Guid purchaseItemId,
            CurrentUserContext currentUser,
            SpaceAccess space,
            CloudPriceStore prices,
            CloudRequestContextStore cloudContext,
            CloudIntelligenceStateService cloudState,
            CloudCredentialAcquisition acquisition,
            IFullWorthCloudClient cloud,
            CancellationToken ct) =>
        {
            var userId = currentUser.RequireUserId();

            var item = await prices.FindPurchaseItemAsync(purchaseItemId, ct);
            if (item is null)
                return Results.NotFound();

            if (!await space.IsMemberAsync(userId, item.FullWorthSpaceId, ct))
                return Results.NotFound();

            var productKey = await ResolvePublicProductKeyAsync(item.Barcode, item.ProductId, prices, ct);

            var currency = CloudRequestContextStore.NormalizeCurrency(item.Currency);
            if (productKey is null || currency is null)
            {
                return Results.Ok(new
                {
                    available = false,
                    reason = productKey is null ? "public_product_id_missing" : "currency_invalid",
                    local = await LocalHistoryAsync(item, currency, prices, ct)
                });
            }

            var local = await LocalHistoryAsync(item, currency, prices, ct);

            var observedDate = item.PurchaseDate ??
                               DateOnly.FromDateTime(item.CreatedAt.UtcDateTime);
            var observedMonth = observedDate.ToString("yyyy-MM");

            if (!await cloudState.HasCurrentActiveConsentAsync(ct))
            {
                return Results.Ok(new
                {
                    available = false,
                    reason = "cloud_disabled",
                    productKey,
                    currency,
                    observedMonth,
                    local
                });
            }

            var state = await cloudState.GetEnabledStateAsync(ct);
            if (state is null)
            {
                return Results.Ok(new
                {
                    available = false,
                    reason = "cloud_disabled",
                    productKey,
                    currency,
                    observedMonth,
                    local
                });
            }

            // A page load must never wait out the Cloud client's HTTP timeout, so the attempt is
            // budgeted and a failure puts the next request straight into this branch.
            var (secret, _) = await acquisition.TryGetAsync(state.InstanceId, ct);
            if (string.IsNullOrWhiteSpace(secret))
            {
                return Results.Ok(new
                {
                    available = false,
                    reason = "cloud_unavailable",
                    productKey,
                    currency,
                    observedMonth,
                    local
                });
            }

            var country = await cloudContext.SpaceCountryAsync(item.FullWorthSpaceId, ct);
            try
            {
                var aggregate = await cloud.GetPriceAsync(
                    secret,
                    productKey,
                    currency,
                    country,
                    null,
                    observedMonth,
                    ct);

                if (aggregate is null)
                {
                    return Results.Ok(new
                    {
                        available = false,
                        reason = "privacy_threshold",
                        productKey,
                        currency,
                        observedMonth,
                        local
                    });
                }

                return Results.Ok(new
                {
                    available = true,
                    productKey,
                    currency,
                    observedMonth,
                    local,
                    cloud = new
                    {
                        aggregate.ObservationCount,
                        aggregate.DistinctInstanceCount,
                        aggregate.Median,
                        aggregate.Mean,
                        aggregate.P25,
                        aggregate.P75,
                        aggregate.Min,
                        aggregate.Max,
                        aggregate.Country,
                        aggregate.MerchantKey,
                        scope = aggregate.MerchantKey is not null
                            ? "merchant"
                            : aggregate.Country is not null
                                ? "country"
                                : "global"
                    }
                });
            }
            catch (FullWorthCloudException)
            {
                return Results.Ok(new
                {
                    available = false,
                    reason = "cloud_unavailable",
                    productKey,
                    currency,
                    observedMonth,
                    local
                });
            }
        });

        return app;
    }

    /// <summary>
    /// Der oeffentliche Schluessel eines Artikels ist seine GTIN. Steht sie nicht an der Position,
    /// wird sie unter den Strichcodes des Produkts gesucht.
    /// </summary>
    private static async Task<string?> ResolvePublicProductKeyAsync(
        string? barcode,
        Guid? productId,
        CloudPriceStore prices,
        CancellationToken ct)
    {
        if (GtinKey.TryCreateGtinSubjectKey(barcode, out var direct))
            return direct;

        if (!productId.HasValue)
            return null;

        var barcodes = await prices.ProductBarcodesAsync(productId.Value, ct);

        foreach (var candidate in barcodes)
        {
            if (GtinKey.TryCreateGtinSubjectKey(candidate, out var key))
                return key;
        }

        return null;
    }

    /// <summary>
    /// Was der Haushalt selbst fuer denselben Artikel bezahlt hat. Das ist der Vergleich, der auch
    /// ohne Cloud funktioniert - darum steht er hier und nicht hinter der Verfuegbarkeitspruefung.
    /// </summary>
    private static async Task<object> LocalHistoryAsync(
        PricedPurchaseItem item, string? currency, CloudPriceStore prices, CancellationToken ct)
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
