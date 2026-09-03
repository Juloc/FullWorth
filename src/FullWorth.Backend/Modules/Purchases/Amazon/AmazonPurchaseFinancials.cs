using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Purchases.Amazon;

public static class AmazonPurchaseFinancials
{
    public static async Task ApplyAsync(
        FullWorthDbContext db,
        PurchaseAuthorizationStore authorization,
        Guid userId,
        Guid fullWorthSpaceId,
        Guid purchaseId,
        AmazonOrderSnapshot order,
        CancellationToken ct)
    {
        var status = await db.Purchases.AsNoTracking()
            .Where(x => x.Id == purchaseId && x.FullWorthSpaceId == fullWorthSpaceId && x.Source == "amazon")
            .Select(x => x.Status)
            .SingleOrDefaultAsync(ct);
        if (status is null) return;

        var store = new PurchaseDiscountDetailsStore(db, authorization);
        var current = await store.GetAsync(userId, fullWorthSpaceId, purchaseId, ct);
        if (current is null) return;

        // A confirmed order or an explicitly manual discount edit is user-owned evidence. A later Amazon
        // page-layout change must never replace it with a newly parsed automatic interpretation.
        if (string.Equals(status, "confirmed", StringComparison.OrdinalIgnoreCase) ||
            current.Discounts.Any(x => string.Equals(x.Source, "manual", StringComparison.OrdinalIgnoreCase)))
            return;

        var explicitDiscounts = (order.Discounts ?? [])
            .Where(x => x.Amount > 0m)
            .ToList();
        if (explicitDiscounts.Count == 0 && !order.SubtotalAmount.HasValue)
            return; // absence of evidence is not evidence that a historical Amazon discount disappeared

        var discountTotal = explicitDiscounts.Count > 0
            ? explicitDiscounts.Sum(x => Math.Max(0m, x.Amount))
            : current.DiscountAmount;

        // Amazon order pages can include shipping/other components not represented by the receipt model.
        // Only make its subtotal authoritative for reconciliation when the visible equation is complete.
        decimal? subtotal = null;
        if (order.SubtotalAmount.HasValue &&
            Math.Abs(order.SubtotalAmount.Value - discountTotal - order.TotalAmount) <= .01m)
            subtotal = order.SubtotalAmount.Value;
        else if (!explicitDiscounts.Any())
            subtotal = current.SubtotalAmount;

        var discounts = explicitDiscounts.Count > 0
            ? explicitDiscounts.Select(x => new PurchaseDiscountWrite(
                null,
                null,
                PurchaseDiscountTypes.Allowed.Contains(x.Type) ? x.Type.ToLowerInvariant() : "other",
                x.Label,
                Math.Max(0m, x.Amount),
                null,
                null,
                x.RawText,
                "amazon",
                1m)).ToList()
            : current.Discounts.Select(x => new PurchaseDiscountWrite(
                x.Id, x.PurchaseItemId, x.Type, x.Label, x.Amount, x.Percentage, x.CouponCode,
                x.RawText, x.Source, x.Confidence)).ToList();

        var result = await store.SaveAsync(
            userId,
            fullWorthSpaceId,
            purchaseId,
            new PurchaseFinancialWrite(
                subtotal,
                discountTotal,
                current.DepositAmount,
                current.TaxAmount,
                current.RoundingAmount,
                null,
                discounts),
            ct);
        if (result != PurchaseMutationResult.Success)
            throw new InvalidOperationException($"Amazon financial metadata could not be applied: {result}.");
    }
}
