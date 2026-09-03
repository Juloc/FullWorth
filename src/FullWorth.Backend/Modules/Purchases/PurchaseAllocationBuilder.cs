using FullWorth.Backend.Modules.Transactions;

namespace FullWorth.Backend.Modules.Purchases;

/// <summary>
/// The single canonical conversion from a reconciled purchase breakdown to ledger-signed transaction
/// allocations. Both confirmation and the explicit workspace import flow use this builder so coupons,
/// deposits, fees and rounding can never diverge between the two paths.
/// </summary>
internal static class PurchaseAllocationBuilder
{
    internal static (List<TransactionAllocation> Allocations, List<PurchaseAllocationLink> Links) Build(
        Purchase purchase,
        FinanceTransaction transaction,
        PurchaseReconciliationCalculation reconciliation,
        DateTimeOffset now)
    {
        var allocations = new List<TransactionAllocation>();
        var links = new List<PurchaseAllocationLink>();
        var sign = transaction.Amount < 0m ? -1m : 1m;
        var sequence = 0;

        void Add(decimal amount, Guid? categoryId, Guid? itemId, string note, string type, Guid? discountId = null)
        {
            amount = PurchaseArticleCalculator.RoundMoney(amount, transaction.Currency);
            if (amount == 0m) return;
            var allocation = new TransactionAllocation
            {
                Id = Guid.NewGuid(),
                TransactionId = transaction.Id,
                CategoryId = categoryId,
                Amount = amount,
                Note = note,
                PurchaseItemId = itemId,
                CreatedAt = now.AddTicks(sequence++),
                UpdatedAt = now
            };
            allocations.Add(allocation);
            links.Add(new PurchaseAllocationLink
            {
                TransactionAllocationId = allocation.Id,
                PurchaseId = purchase.Id,
                PurchaseDiscountId = discountId,
                AllocationType = type,
                CreatedAt = now
            });
        }

        var hasCanonicalDiscounts = purchase.Discounts.Count > 0;
        decimal explicitDeposit = 0m;
        decimal legacyDiscountFromItems = 0m;
        foreach (var item in purchase.Items.OrderBy(x => x.SortOrder).ThenBy(x => x.Id))
        {
            var type = (item.LineType ?? "product").Trim().ToLowerInvariant();
            if (type is "discount" or "coupon")
            {
                if (!hasCanonicalDiscounts)
                {
                    legacyDiscountFromItems += Math.Abs(item.TotalPrice);
                    Add(Math.Abs(item.TotalPrice) * -sign, item.CategoryId, item.Id, item.Name, "legacy_discount");
                }
                continue;
            }
            if (type is "deposit" or "pfand")
            {
                var amount = Math.Abs(item.TotalPrice);
                explicitDeposit += amount;
                Add(amount * sign, null, item.Id, item.Name, "deposit");
                continue;
            }

            Add(item.TotalPrice * sign, item.CategoryId, item.Id, item.Name, "article");
            var itemDeposit = Math.Max(0m, item.DepositAmount ?? 0m);
            if (itemDeposit > 0m)
            {
                explicitDeposit += itemDeposit;
                Add(itemDeposit * sign, null, item.Id, $"Pfand · {item.Name}", "deposit");
            }
        }

        // Item-linked discounts are already reflected in PurchaseItem.TotalPrice. Only basket discounts
        // become opposite-signed ledger adjustments.
        foreach (var discount in purchase.Discounts.Where(x => !x.PurchaseItemId.HasValue).OrderBy(x => x.CreatedAt).ThenBy(x => x.Id))
            Add(discount.Amount * -sign, null, null, discount.Label, "discount", discount.Id);

        // Compatibility for historical synthetic discount rows that predate PurchaseDiscounts. The
        // basket-discount total already includes negative coupon/discount item lines, which the item
        // loop above emitted as legacy_discount allocations, so subtract those to avoid double counting.
        var canonicalBasketDiscount = purchase.Discounts.Where(x => !x.PurchaseItemId.HasValue).Sum(x => x.Amount);
        var residualLegacyDiscount = Math.Max(0m, reconciliation.BasketDiscountTotal - canonicalBasketDiscount - legacyDiscountFromItems);
        if (residualLegacyDiscount > reconciliation.Tolerance)
            Add(residualLegacyDiscount * -sign, null, null, "Legacy basket discount", "discount");

        var residualDeposit = Math.Max(0m, reconciliation.DepositTotal - explicitDeposit);
        if (residualDeposit > reconciliation.Tolerance)
            Add(residualDeposit * sign, null, null, "Pfand", "deposit");

        var normalTypes = purchase.Items.Select(x => (x.LineType ?? "product").Trim().ToLowerInvariant()).ToHashSet();
        if (!normalTypes.Contains("tip") && purchase.TipAmount is > 0m)
            Add(purchase.TipAmount.Value * sign, null, null, "Trinkgeld", "tip");
        if (!normalTypes.Contains("shipping") && purchase.ShippingAmount is > 0m)
            Add(purchase.ShippingAmount.Value * sign, null, null, "Versand", "shipping");
        if (!normalTypes.Contains("fee") && purchase.FeeAmount is > 0m)
            Add(purchase.FeeAmount.Value * sign, null, null, "Gebühr", "fee");
        if (purchase.RoundingAmount != 0m)
            Add(purchase.RoundingAmount * sign, null, null, "Rundung", "rounding");

        return (allocations, links);
    }
}
