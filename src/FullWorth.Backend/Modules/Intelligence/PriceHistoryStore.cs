using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Intelligence;

/// <summary>
/// Eine gekaufte Position, soweit der Preisverlauf sie braucht: woran der Artikel wiedererkannt
/// wird, in welcher Waehrung, und zu welchem Haushalt er gehoert. Preis, Menge und Datum standen
/// hier ebenfalls, solange die Cloud einen Beobachtungsmonat und einen Stueckpreis mitgeschickt
/// bekam - fuer die eigene Historie werden sie erst in der Abfrage darunter gebraucht.
/// </summary>
public sealed record PricedPurchaseItem(
    Guid? ProductId, string? Barcode, string Currency, Guid FullWorthSpaceId);

/// <summary>Ein Stueckpreis aus der eigenen Historie.</summary>
public sealed record LocalPriceObservation(
    decimal? UnitPrice, decimal? BaseUnitPrice, decimal Quantity, decimal TotalPrice);

/// <summary>
/// Was der Preisverlauf aus der eigenen Datenbank liest: die Position selbst und die frueheren
/// Kaeufe desselben Artikels.
///
/// Gerechnet wird hier nichts - Median und Mittelwert entstehen im Endpunkt, weil sie die Antwort
/// sind und nicht die Daten.
/// </summary>
public sealed class PriceHistoryStore(FullWorthDbContext db)
{
    /// <summary>Weiter zurueck als zweihundert Kaeufe sagt ueber den heutigen Preis nichts mehr.</summary>
    private const int MaxObservations = 200;

    public Task<PricedPurchaseItem?> FindPurchaseItemAsync(Guid purchaseItemId, CancellationToken ct) =>
        db.PurchaseItems.AsNoTracking()
            .Where(item => item.Id == purchaseItemId)
            .Select(item => new PricedPurchaseItem(
                item.ProductId, item.Barcode, item.Currency, item.Purchase.FullWorthSpaceId))
            .SingleOrDefaultAsync(ct);

    /// <summary>Eine Waehrung ist drei Buchstaben. Alles andere ist keine.</summary>
    public static string? NormalizeCurrency(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim().ToUpperInvariant();
        return normalized.Length == 3 && normalized.All(char.IsAsciiLetter) ? normalized : null;
    }

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
