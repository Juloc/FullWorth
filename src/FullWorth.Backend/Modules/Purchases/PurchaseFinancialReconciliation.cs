using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Purchases;

public sealed class PurchaseFinancialReconciliationRow
{
    public Guid PurchaseId { get; set; }
    public Guid? TransactionId { get; set; }
    public string Currency { get; set; } = "EUR";
    public decimal PurchaseTotal { get; set; }
    public decimal ItemTotal { get; set; }
    public decimal? SubtotalAmount { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal DepositAmount { get; set; }
    public decimal? TaxAmount { get; set; }
    public decimal RoundingAmount { get; set; }
}

public sealed class PurchaseReconciliationItemFingerprintRow
{
    public Guid Id { get; set; }
    public Guid? CategoryId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Brand { get; set; }
    public string? Sku { get; set; }
    public string? Asin { get; set; }
    public decimal Quantity { get; set; }
    public decimal? UnitPrice { get; set; }
    public decimal TotalPrice { get; set; }
    public string Currency { get; set; } = "EUR";
    public string CategorizationSource { get; set; } = "none";
    public string? Notes { get; set; }
    public decimal? OriginalUnitPrice { get; set; }
    public decimal DiscountAmount { get; set; }
    public string? DiscountLabel { get; set; }
    public decimal DepositAmount { get; set; }
}

public sealed class PurchaseReconciliationDiscountFingerprintRow
{
    public Guid? PurchaseItemId { get; set; }
    public string Type { get; set; } = "other";
    public string Label { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public decimal? Percentage { get; set; }
    public string? CouponCode { get; set; }
    public string? RawText { get; set; }
    public string Source { get; set; } = "manual";
    public decimal? Confidence { get; set; }
}

public sealed record PurchaseFinancialReconciliationState(
    Guid PurchaseId,
    Guid? TransactionId,
    string Currency,
    decimal PurchaseTotal,
    decimal ItemTotal,
    decimal? SubtotalAmount,
    decimal DiscountAmount,
    decimal DepositAmount,
    decimal? TaxAmount,
    decimal RoundingAmount,
    decimal? CalculatedTotal,
    decimal ItemDifference,
    decimal? FinancialDifference,
    decimal? TransactionAmount,
    decimal? TransactionDifference,
    string ReconciliationBasis,
    bool ItemsReconciled,
    bool TransactionReconciled,
    bool FullyReconciled,
    string StateFingerprint);

public static class PurchaseFinancialReconciliation
{
    public const decimal Tolerance = .01m;

    public static async Task<PurchaseFinancialReconciliationState?> CalculateAsync(
        FullWorthDbContext db,
        Guid fullWorthSpaceId,
        Guid purchaseId,
        CancellationToken ct)
    {
        var row = await db.Database.SqlQuery<PurchaseFinancialReconciliationRow>($"""
            SELECT p."Id" AS "PurchaseId",
                   p."TransactionId",
                   p."Currency",
                   p."TotalAmount" AS "PurchaseTotal",
                   COALESCE((SELECT SUM(i."TotalPrice") FROM "PurchaseItems" i WHERE i."PurchaseId" = p."Id"), 0) AS "ItemTotal",
                   p."SubtotalAmount",
                   COALESCE(p."DiscountAmount", 0) AS "DiscountAmount",
                   COALESCE(p."DepositAmount", 0) AS "DepositAmount",
                   p."TaxAmount",
                   p."RoundingAmount"
            FROM "Purchases" p
            WHERE p."Id" = {purchaseId} AND p."FullWorthSpaceId" = {fullWorthSpaceId}
            """).SingleOrDefaultAsync(ct);
        if (row is null) return null;

        decimal? transactionAmount = null;
        if (row.TransactionId.HasValue)
            transactionAmount = await db.Transactions.AsNoTracking()
                .Where(transaction => transaction.Id == row.TransactionId.Value)
                .Select(transaction => (decimal?)transaction.Amount)
                .SingleOrDefaultAsync(ct);

        // When the receipt exposes a pre-discount subtotal, it is the authoritative receipt equation.
        // This prevents legitimate basket coupons/Pfand from appearing as unexplained item differences.
        var calculatedTotal = row.SubtotalAmount.HasValue
            ? row.SubtotalAmount.Value - row.DiscountAmount + row.DepositAmount + row.RoundingAmount
            : (decimal?)null;
        var financialDifference = calculatedTotal.HasValue ? row.PurchaseTotal - calculatedTotal.Value : (decimal?)null;
        var legacyItemDifference = row.PurchaseTotal - row.ItemTotal;
        var itemDifference = financialDifference ?? legacyItemDifference;
        var basis = calculatedTotal.HasValue ? "receipt_financials" : "items";

        var transactionDifference = transactionAmount.HasValue
            ? Math.Abs(transactionAmount.Value) - row.PurchaseTotal
            : (decimal?)null;
        var itemsReconciled = Math.Abs(itemDifference) <= Tolerance;
        var transactionReconciled = !transactionDifference.HasValue || Math.Abs(transactionDifference.Value) <= Tolerance;
        var fingerprint = await FingerprintAsync(db, row, transactionAmount, ct);

        return new PurchaseFinancialReconciliationState(
            row.PurchaseId,
            row.TransactionId,
            row.Currency,
            row.PurchaseTotal,
            row.ItemTotal,
            row.SubtotalAmount,
            row.DiscountAmount,
            row.DepositAmount,
            row.TaxAmount,
            row.RoundingAmount,
            calculatedTotal,
            itemDifference,
            financialDifference,
            transactionAmount,
            transactionDifference,
            basis,
            itemsReconciled,
            transactionReconciled,
            itemsReconciled && transactionReconciled,
            fingerprint);
    }

    private static async Task<string> FingerprintAsync(
        FullWorthDbContext db,
        PurchaseFinancialReconciliationRow purchase,
        decimal? transactionAmount,
        CancellationToken ct)
    {
        var items = await db.Database.SqlQuery<PurchaseReconciliationItemFingerprintRow>($"""
            SELECT "Id", "CategoryId", "Name", "Brand", "Sku", "Asin", "Quantity", "UnitPrice", "TotalPrice",
                   "Currency", "CategorizationSource", "Notes", "OriginalUnitPrice",
                   COALESCE("DiscountAmount", 0) AS "DiscountAmount", "DiscountLabel", COALESCE("DepositAmount", 0) AS "DepositAmount"
            FROM "PurchaseItems"
            WHERE "PurchaseId" = {purchase.PurchaseId}
            """).ToListAsync(ct);
        var itemKeyById = items.ToDictionary(item => item.Id, ItemKey);
        var itemKeys = itemKeyById.Values.OrderBy(value => value, StringComparer.Ordinal).ToArray();

        var discounts = await db.Database.SqlQuery<PurchaseReconciliationDiscountFingerprintRow>($"""
            SELECT "PurchaseItemId", "Type", "Label", "Amount", "Percentage", "CouponCode", "RawText", "Source", "Confidence"
            FROM "PurchaseDiscounts"
            WHERE "PurchaseId" = {purchase.PurchaseId}
            """).ToListAsync(ct);
        var discountKeys = discounts.Select(discount => string.Join("|",
                S(discount.Type), S(discount.Label), D(discount.Amount), D(discount.Percentage),
                S(discount.CouponCode), S(discount.RawText), S(discount.Source), D(discount.Confidence),
                discount.PurchaseItemId is { } itemId && itemKeyById.TryGetValue(itemId, out var itemKey) ? itemKey : "~"))
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

        var canonical = new StringBuilder()
            .Append("purchase|").Append(purchase.PurchaseId.ToString("N")).Append('|')
            .Append(purchase.TransactionId?.ToString("N") ?? "~").Append('|')
            .Append(S(purchase.Currency)).Append('|').Append(D(purchase.PurchaseTotal)).Append('|')
            .Append(D(purchase.SubtotalAmount)).Append('|').Append(D(purchase.DiscountAmount)).Append('|')
            .Append(D(purchase.DepositAmount)).Append('|').Append(D(purchase.TaxAmount)).Append('|')
            .Append(D(purchase.RoundingAmount)).Append('|').Append(D(transactionAmount)).AppendLine();
        foreach (var item in itemKeys) canonical.Append("item|").Append(item).AppendLine();
        foreach (var discount in discountKeys) canonical.Append("discount|").Append(discount).AppendLine();

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))).ToLowerInvariant();
    }

    private static string ItemKey(PurchaseReconciliationItemFingerprintRow item) => string.Join("|",
        item.CategoryId?.ToString("N") ?? "~", S(item.Name), S(item.Brand), S(item.Sku), S(item.Asin),
        D(item.Quantity), D(item.UnitPrice), D(item.TotalPrice), S(item.Currency), S(item.CategorizationSource),
        S(item.Notes), D(item.OriginalUnitPrice), D(item.DiscountAmount), S(item.DiscountLabel), D(item.DepositAmount));

    private static string S(string? value) => string.IsNullOrWhiteSpace(value) ? "~" : value.Trim();
    private static string D(decimal? value) => value?.ToString("0.############################", CultureInfo.InvariantCulture) ?? "~";
}
