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
            FullWorthDbContext financeDb,
            CloudIntelligenceStateService cloudState,
            CloudInstanceCredentialStore credentials,
            IFullWorthCloudClient cloud,
            CancellationToken ct) =>
        {
            var userId = currentUser.RequireUserId();

            var item = await financeDb.PurchaseItems.AsNoTracking()
                .Where(x => x.Id == purchaseItemId)
                .Select(x => new
                {
                    x.Id,
                    x.ProductId,
                    x.Barcode,
                    x.UnitPrice,
                    x.BaseUnitPrice,
                    x.Quantity,
                    x.TotalPrice,
                    x.Currency,
                    x.CreatedAt,
                    x.Purchase.FullWorthSpaceId,
                    x.Purchase.PurchaseDate
                })
                .SingleOrDefaultAsync(ct);
            if (item is null)
                return Results.NotFound();

            var isMember = await financeDb.FullWorthSpaceMembers.AsNoTracking().AnyAsync(x =>
                x.FullWorthSpaceId == item.FullWorthSpaceId && x.UserId == userId, ct);
            if (!isMember)
                return Results.NotFound();

            var productKey = await ResolvePublicProductKeyAsync(
                item.Barcode,
                item.ProductId,
                financeDb,
                ct);

            var currency = NormalizeCurrency(item.Currency);
            if (productKey is null || currency is null)
            {
                return Results.Ok(new
                {
                    available = false,
                    reason = productKey is null ? "public_product_id_missing" : "currency_invalid",
                    local = await LocalHistoryAsync(
                        item.FullWorthSpaceId,
                        item.ProductId,
                        item.Barcode,
                        currency,
                        financeDb,
                        ct)
                });
            }

            var local = await LocalHistoryAsync(
                item.FullWorthSpaceId,
                item.ProductId,
                item.Barcode,
                currency,
                financeDb,
                ct);

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

            var secret = await credentials.GetSecretAsync(state.InstanceId, ct);
            if (string.IsNullOrWhiteSpace(secret))
            {
                try
                {
                    var registration = await cloud.RegisterAsync(
                        state.InstanceId,
                        CloudIntelligencePolicy.CurrentVersion,
                        typeof(CloudPriceEndpoints).Assembly.GetName().Version?.ToString() ?? "unknown",
                        ct);
                    await credentials.SaveAsync(registration, ct);
                    secret = registration.Credential;
                    await cloudState.SetTransportStatusAsync(
                        state.InstanceId,
                        null,
                        registration.EntitlementStatus,
                        DateTimeOffset.UtcNow,
                        null,
                        ct);
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
            }

            var country = await SpaceCountryAsync(item.FullWorthSpaceId, financeDb, ct);
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

    private static async Task<string?> ResolvePublicProductKeyAsync(
        string? barcode,
        Guid? productId,
        FullWorthDbContext db,
        CancellationToken ct)
    {
        if (GtinKey.TryCreateGtinSubjectKey(barcode, out var direct))
            return direct;

        if (!productId.HasValue)
            return null;

        var barcodes = await db.ProductBarcodes.AsNoTracking()
            .Where(x => x.ProductId == productId.Value)
            .OrderBy(x => x.CreatedAt)
            .Select(x => x.Code)
            .Take(10)
            .ToListAsync(ct);

        foreach (var candidate in barcodes)
        {
            if (GtinKey.TryCreateGtinSubjectKey(candidate, out var key))
                return key;
        }

        return null;
    }

    private static async Task<object> LocalHistoryAsync(
        Guid fullWorthSpaceId,
        Guid? productId,
        string? barcode,
        string? currency,
        FullWorthDbContext db,
        CancellationToken ct)
    {
        if (currency is null)
            return new { count = 0 };

        var query = db.PurchaseItems.AsNoTracking()
            .Where(x =>
                x.Purchase.FullWorthSpaceId == fullWorthSpaceId &&
                x.Purchase.Status == "confirmed" &&
                x.Currency == currency &&
                x.LineType == "product");

        if (productId.HasValue)
            query = query.Where(x => x.ProductId == productId.Value);
        else if (!string.IsNullOrWhiteSpace(barcode))
            query = query.Where(x => x.Barcode == barcode);
        else
            return new { count = 0 };

        var rows = await query
            .OrderByDescending(x => x.Purchase.PurchaseDate)
            .Take(200)
            .Select(x => new
            {
                x.UnitPrice,
                x.BaseUnitPrice,
                x.Quantity,
                x.TotalPrice
            })
            .ToListAsync(ct);

        var values = rows
            .Select(x => EffectiveUnitPrice(
                x.UnitPrice,
                x.BaseUnitPrice,
                x.Quantity,
                x.TotalPrice))
            .Where(x => x is > 0m)
            .Select(x => x!.Value)
            .OrderBy(x => x)
            .ToArray();

        if (values.Length == 0)
            return new { count = 0 };

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

    private static async Task<string?> SpaceCountryAsync(
        Guid fullWorthSpaceId,
        FullWorthDbContext db,
        CancellationToken ct)
    {
        var countries = (await db.BankConnections.AsNoTracking()
                .Where(x => x.FullWorthSpaceId == fullWorthSpaceId &&
                            x.Country != null &&
                            x.Country != "")
                .Select(x => x.Country)
                .Distinct()
                .Take(3)
                .ToListAsync(ct))
            .Select(NormalizeCountry)
            .Where(x => x is not null)
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return countries.Count == 1 ? countries[0] : null;
    }

    private static string? NormalizeCurrency(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim().ToUpperInvariant();
        return normalized.Length == 3 && normalized.All(char.IsAsciiLetter)
            ? normalized
            : null;
    }

    private static string? NormalizeCountry(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim().ToUpperInvariant();
        return normalized.Length == 2 && normalized.All(char.IsAsciiLetter)
            ? normalized
            : null;
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
