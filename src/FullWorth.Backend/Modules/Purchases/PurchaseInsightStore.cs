using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Purchases;

/// <summary>Eine gekaufte Zeile, wie die Preisentwicklung sie braucht.</summary>
public sealed record InsightItemRow(
    Guid ProductId, string ProductName, string? Brand, DateOnly Date, string Merchant, string Currency,
    decimal Quantity, decimal TotalPrice, decimal? UnitPrice, decimal? BaseUnitPrice,
    string? PackageUnit, string? QuantityUnit);

/// <summary>Ein Einkauf, wie die Korbentwicklung ihn braucht.</summary>
public sealed record InsightBasketRow(Guid Id, DateOnly Date, decimal TotalAmount, string Currency, decimal Savings);

/// <summary>Ein Kauf eines Produkts - Datum und Menge genuegen fuer eine Nachschub-Prognose.</summary>
public sealed record InsightRestockRow(Guid ProductId, string ProductName, string? Brand, DateOnly Date, decimal Quantity);

/// <summary>
/// Die Kaeufe, aus denen sich Preisentwicklung, Korbentwicklung und Nachschub ablesen lassen.
///
/// Der ganze Wert dieser Datei steckt in <c>VisiblePurchases</c>: ein Kauf zaehlt fuer diesen
/// Benutzer nur, wenn er Mitglied des Space ist, der Kauf nicht privat ist (oder ihm gehoert), und
/// jede daran haengende Zahlung ueber ein Konto lief, das ihm gehoert. Diese Bedingung stand dreimal
/// gleichlautend im Code, einmal fuer Kaeufe und einmal fuer ihre Zeilen.
///
/// Die Kontopruefung steht absichtlich zweimal ausgeschrieben statt in einem Helfer: EF uebersetzt
/// einen Methodenaufruf im Ausdrucksbaum nicht, und der Versuch endete in einem 500.
///
/// Gewertet werden nur bestaetigte Kaeufe (<c>ReviewState == "confirmed"</c>). Aus einem Beleg, den
/// noch niemand gesehen hat, eine Teuerungsrate zu rechnen waere eine Zahl ohne Deckung.
/// </summary>
public sealed class PurchaseInsightStore(FullWorthDbContext db)
{
    public Task<string?> BaseCurrencyAsync(Guid userId, Guid fullWorthSpaceId, CancellationToken ct) =>
        db.FullWorthSpaces.AsNoTracking()
            .Where(space => space.Id == fullWorthSpaceId
                         && db.FullWorthSpaceMembers.Any(member =>
                                member.FullWorthSpaceId == fullWorthSpaceId && member.UserId == userId))
            .Select(space => space.BaseCurrency)
            .SingleOrDefaultAsync(ct);

    public Task<bool> IsMemberAsync(Guid userId, Guid fullWorthSpaceId, CancellationToken ct) =>
        RawSql.IsMemberAsync(db, userId, fullWorthSpaceId, ct);

    /// <summary>Produktzeilen mit Preis, aeltester Kauf zuerst - die Reihenfolge trägt die Auswertung.</summary>
    public Task<List<InsightItemRow>> PricedItemsAsync(
        Guid userId, Guid fullWorthSpaceId, DateOnly from, DateOnly to, CancellationToken ct) =>
        VisibleItems(userId, fullWorthSpaceId)
            .Where(item => item.ProductId.HasValue
                        && item.Purchase.ReviewState == "confirmed"
                        && item.Purchase.PurchaseDate >= from
                        && item.Purchase.PurchaseDate <= to)
            .OrderBy(item => item.Purchase.PurchaseDate).ThenBy(item => item.CreatedAt)
            .Select(item => new InsightItemRow(
                item.ProductId!.Value,
                item.Product!.CanonicalName,
                item.Product.Brand,
                item.Purchase.PurchaseDate!.Value,
                item.Purchase.Merchant,
                item.Currency,
                item.Quantity,
                item.TotalPrice,
                item.UnitPrice,
                item.BaseUnitPrice,
                item.PackageUnit,
                item.QuantityUnit))
            .ToListAsync(ct);

    /// <summary>
    /// Je Einkauf die Summe und was daran gespart wurde. Die einzelnen Rabattzeilen gehen vor dem
    /// Gesamtrabatt: sie sind die genauere Angabe, wenn es sie gibt.
    /// </summary>
    public Task<List<InsightBasketRow>> BasketsAsync(
        Guid userId, Guid fullWorthSpaceId, DateOnly from, DateOnly to, CancellationToken ct) =>
        VisiblePurchases(userId, fullWorthSpaceId)
            .Where(purchase => purchase.ReviewState == "confirmed"
                            && purchase.PurchaseDate >= from
                            && purchase.PurchaseDate <= to)
            .Select(purchase => new InsightBasketRow(
                purchase.Id,
                purchase.PurchaseDate!.Value,
                purchase.TotalAmount,
                purchase.Currency,
                purchase.Discounts.Sum(discount => (decimal?)discount.Amount) ?? purchase.DiscountAmount ?? 0m))
            .ToListAsync(ct);

    public Task<List<InsightRestockRow>> RestockHistoryAsync(
        Guid userId, Guid fullWorthSpaceId, DateOnly since, DateOnly until, CancellationToken ct) =>
        VisibleItems(userId, fullWorthSpaceId)
            .Where(item => item.ProductId.HasValue
                        && item.Purchase.ReviewState == "confirmed"
                        && item.Purchase.PurchaseDate >= since
                        && item.Purchase.PurchaseDate <= until)
            .Select(item => new InsightRestockRow(
                item.ProductId!.Value,
                item.Product!.CanonicalName,
                item.Product.Brand,
                item.Purchase.PurchaseDate!.Value,
                item.Quantity))
            .ToListAsync(ct);

    /// <summary>
    /// Welche Kaeufe dieser Benutzer sehen darf. Ein Kauf ohne Zahlungsverknuepfung gehoert dem
    /// Haushalt; sobald er an einer Buchung haengt, entscheidet das Konto dahinter.
    /// </summary>
    private IQueryable<Purchase> VisiblePurchases(Guid userId, Guid fullWorthSpaceId) =>
        db.Purchases.AsNoTracking().Where(purchase =>
            purchase.FullWorthSpaceId == fullWorthSpaceId
            && db.FullWorthSpaceMembers.Any(member =>
                   member.FullWorthSpaceId == fullWorthSpaceId && member.UserId == userId)
            && (purchase.Visibility != "private" || purchase.CreatedByUserId == userId)
            && (!purchase.PaymentLinks.Any() || purchase.PaymentLinks.Any(link =>
                   db.Transactions.Any(transaction => transaction.Id == link.TransactionId && db.Accounts.Any(account => account.Id == transaction.AccountId && account.FullWorthSpaceId == fullWorthSpaceId && account.Owners.Any(owner => owner.UserId == userId)))))
            && (purchase.TransactionId == null || db.Transactions.Any(transaction => transaction.Id == purchase.TransactionId && db.Accounts.Any(account => account.Id == transaction.AccountId && account.FullWorthSpaceId == fullWorthSpaceId && account.Owners.Any(owner => owner.UserId == userId)))));

    private IQueryable<PurchaseItem> VisibleItems(Guid userId, Guid fullWorthSpaceId) =>
        db.PurchaseItems.AsNoTracking().Where(item =>
            item.Purchase.FullWorthSpaceId == fullWorthSpaceId
            && db.FullWorthSpaceMembers.Any(member =>
                   member.FullWorthSpaceId == fullWorthSpaceId && member.UserId == userId)
            && (item.Purchase.Visibility != "private" || item.Purchase.CreatedByUserId == userId)
            && (!item.Purchase.PaymentLinks.Any() || item.Purchase.PaymentLinks.Any(link =>
                   db.Transactions.Any(transaction => transaction.Id == link.TransactionId && db.Accounts.Any(account => account.Id == transaction.AccountId && account.FullWorthSpaceId == fullWorthSpaceId && account.Owners.Any(owner => owner.UserId == userId)))))
            && (item.Purchase.TransactionId == null || db.Transactions.Any(transaction => transaction.Id == item.Purchase.TransactionId && db.Accounts.Any(account => account.Id == transaction.AccountId && account.FullWorthSpaceId == fullWorthSpaceId && account.Owners.Any(owner => owner.UserId == userId)))));

}
