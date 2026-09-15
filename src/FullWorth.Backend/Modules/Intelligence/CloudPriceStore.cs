using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Intelligence;

/// <summary>Eine gekaufte Position, soweit ein Preisvergleich sie braucht.</summary>
public sealed record PricedPurchaseItem(
    Guid Id, Guid? ProductId, string? Barcode, decimal? UnitPrice, decimal? BaseUnitPrice,
    decimal Quantity, decimal TotalPrice, string Currency, DateTimeOffset CreatedAt,
    Guid FullWorthSpaceId, DateOnly? PurchaseDate);

/// <summary>Ein Stueckpreis aus der eigenen Historie.</summary>
public sealed record LocalPriceObservation(
    decimal? UnitPrice, decimal? BaseUnitPrice, decimal Quantity, decimal TotalPrice);

/// <summary>
/// Was der Preisvergleich aus der eigenen Datenbank liest, bevor er die Cloud fragt: die Position
/// selbst, die Strichcodes ihres Produkts und die frueheren Kaeufe desselben Artikels.
///
/// Gerechnet wird hier nichts - Median und Mittelwert entstehen im Endpunkt, weil sie die Antwort
/// sind und nicht die Daten.
/// </summary>
public sealed class CloudPriceStore(FullWorthDbContext db)
{
    /// <summary>Weiter zurueck als zweihundert Kaeufe sagt ueber den heutigen Preis nichts mehr.</summary>
    private const int MaxObservations = 200;

    public Task<PricedPurchaseItem?> FindPurchaseItemAsync(Guid purchaseItemId, CancellationToken ct) =>
        db.PurchaseItems.AsNoTracking()
            .Where(item => item.Id == purchaseItemId)
            .Select(item => new PricedPurchaseItem(
                item.Id, item.ProductId, item.Barcode, item.UnitPrice, item.BaseUnitPrice,
                item.Quantity, item.TotalPrice, item.Currency, item.CreatedAt,
                item.Purchase.FullWorthSpaceId, item.Purchase.PurchaseDate))
            .SingleOrDefaultAsync(ct);

    /// <summary>Die ersten Strichcodes eines Produkts, aelteste zuerst - einer davon ist die GTIN.</summary>
    public Task<List<string>> ProductBarcodesAsync(Guid productId, CancellationToken ct) =>
        db.ProductBarcodes.AsNoTracking()
            .Where(barcode => barcode.ProductId == productId)
            .OrderBy(barcode => barcode.CreatedAt)
            .Select(barcode => barcode.Code)
            .Take(10)
            .ToListAsync(ct);

    /// <summary>
    /// Was dieser Haushalt fuer denselben Artikel bezahlt hat - nur bestaetigte Kaeufe, nur echte
    /// Produktzeilen, nur dieselbe Waehrung. Ohne Produkt und ohne Strichcode gibt es nichts zu
    /// vergleichen.
    /// </summary>
    public async Task<IReadOnlyList<LocalPriceObservation>> LocalHistoryAsync(
        Guid fullWorthSpaceId, Guid? productId, string? barcode, string currency, CancellationToken ct)
    {
        var query = db.PurchaseItems.AsNoTracking()
            .Where(item =>
                item.Purchase.FullWorthSpaceId == fullWorthSpaceId &&
                item.Purchase.Status == "confirmed" &&
                item.Currency == currency &&
                item.LineType == "product");

        if (productId.HasValue) query = query.Where(item => item.ProductId == productId.Value);
        else if (!string.IsNullOrWhiteSpace(barcode)) query = query.Where(item => item.Barcode == barcode);
        else return [];

        return await query
            .OrderByDescending(item => item.Purchase.PurchaseDate)
            .Take(MaxObservations)
            .Select(item => new LocalPriceObservation(
                item.UnitPrice, item.BaseUnitPrice, item.Quantity, item.TotalPrice))
            .ToListAsync(ct);
    }
}
