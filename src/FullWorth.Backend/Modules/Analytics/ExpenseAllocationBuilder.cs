using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Fx;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Analytics;

/// <summary>An expense transaction reduced to what allocation needs, including its currency and value
/// date so spend can be converted to the base currency at the rate effective on that date (§18).</summary>
internal readonly record struct ExpenseTx(Guid Id, decimal Amount, Guid? CategoryId, string Currency, DateOnly Date);

/// <summary>
/// Splits expense transactions into per-category allocations (in the space BASE currency). Explicit
/// TransactionAllocations have highest precedence and may contain signed adjustment lines: an expense
/// product is normally negative on the transaction and becomes positive spend here, while a positive
/// allocation (for example a coupon reducing that category) becomes negative spend. This preserves the
/// invariant that the NET of all allocation rows equals the real bank outflow without double counting.
/// Confirmed legacy single-payment purchase items are the fallback before the transaction category.
/// Foreign amounts and linked refunds are converted at the rate effective on their own value dates.
/// </summary>
internal sealed class ExpenseAllocationBuilder(FullWorthDbContext db)
{
    public async Task<(List<ExpenseAllocation> Allocations, bool Incomplete)> BuildAsync(
        Guid fullWorthSpaceId, IReadOnlyList<ExpenseTx> transactions, CurrencyConverter fx, string baseCurrency, CancellationToken ct)
    {
        if (transactions.Count == 0) return ([], false);
        var ids = transactions.Select(transaction => transaction.Id).ToArray();
        var metaById = transactions.ToDictionary(t => t.Id, t => (t.Currency, t.Date));

        // Explicit allocations are the accounting truth. Keep their sign while converting from ledger
        // sign to spend sign: -10 expense => +10 spend; +2 coupon adjustment => -2 spend.
        var manualRows = await db.TransactionAllocations.AsNoTracking()
            .Where(allocation => ids.Contains(allocation.TransactionId))
            .Select(allocation => new { allocation.TransactionId, allocation.CategoryId, allocation.Amount, allocation.PurchaseItemId })
            .ToListAsync(ct);
        var manualByTransaction = manualRows.GroupBy(row => row.TransactionId).ToDictionary(group => group.Key, group => group.ToList());

        // Compatibility fallback for purchases still represented by the legacy single TransactionId.
        // New N:M payment links become item-level accounting only after explicit TransactionAllocations
        // exist, because guessing which item belongs to which partial payment would be incorrect.
        var purchaseRows = await db.Purchases.AsNoTracking()
            .Where(purchase => purchase.FullWorthSpaceId == fullWorthSpaceId && purchase.TransactionId.HasValue &&
                               ids.Contains(purchase.TransactionId.Value) && purchase.ReviewState == "confirmed")
            .SelectMany(purchase => purchase.Items.Select(item => new
            {
                TransactionId = purchase.TransactionId!.Value,
                item.CategoryId,
                item.TotalPrice
            }))
            .ToListAsync(ct);
        var byTransaction = purchaseRows.GroupBy(row => row.TransactionId).ToDictionary(group => group.Key, group => group.ToList());

        // Allocations in each transaction's ORIGINAL currency first; converted to base below.
        var native = new List<ExpenseAllocation>();
        foreach (var transaction in transactions)
        {
            var transactionSpend = Math.Abs(transaction.Amount);

            if (manualByTransaction.TryGetValue(transaction.Id, out var manualLines) && manualLines.Count > 0)
            {
                var manualNetSpend = 0m;
                foreach (var line in manualLines)
                {
                    var spend = -line.Amount;
                    if (spend == 0m) continue;
                    manualNetSpend += spend;
                    native.Add(new(transaction.Id, line.CategoryId, spend, line.PurchaseItemId.HasValue));
                }

                // Write APIs require exact balancing, but retain a defensive remainder for old/imported
                // rows so legacy malformed data never makes money disappear from analytics.
                var remainder = transactionSpend - manualNetSpend;
                if (Math.Abs(remainder) > .01m)
                    native.Add(new(transaction.Id, transaction.CategoryId, remainder, false));
                continue;
            }

            if (!byTransaction.TryGetValue(transaction.Id, out var itemRows) || itemRows.Count == 0)
            {
                native.Add(new(transaction.Id, transaction.CategoryId, transactionSpend, false));
                continue;
            }

            var itemNet = 0m;
            foreach (var item in itemRows)
            {
                // PurchaseItem.TotalPrice is the item's signed contribution to the positive receipt
                // total: products/fees are positive, standalone coupons/discounts may be negative.
                var spend = item.TotalPrice;
                if (spend == 0m) continue;
                itemNet += spend;
                native.Add(new(transaction.Id, item.CategoryId, spend, true));
            }

            var itemRemainder = transactionSpend - itemNet;
            if (Math.Abs(itemRemainder) > .01m)
                native.Add(new(transaction.Id, transaction.CategoryId, itemRemainder, false));
        }

        // Linked refunds (in their OWN currency, on their OWN date) — fetched per row so each can be
        // converted at its own value-date rate rather than summed raw.
        var refundRows = await db.Transactions.AsNoTracking()
            .Where(t => t.RefundOfTransactionId != null && ids.Contains(t.RefundOfTransactionId.Value)
                        && t.Amount > 0m && !t.IsIgnored && !t.IsTransfer)
            .Select(t => new { OriginalId = t.RefundOfTransactionId!.Value, TargetCategoryId = t.RefundCategoryId, t.Amount, t.Currency, t.BookingDate, t.ValueDate })
            .ToListAsync(ct);

        var dates = transactions.Select(t => t.Date).ToList();
        foreach (var refund in refundRows)
            dates.Add(refund.BookingDate ?? refund.ValueDate ?? metaById[refund.OriginalId].Date);
        var acc = new FxAccumulator(await fx.PrepareAsync(
            baseCurrency,
            dates.Count > 0 ? dates.Min() : DateOnly.FromDateTime(DateTime.UtcNow),
            dates.Count > 0 ? dates.Max() : DateOnly.FromDateTime(DateTime.UtcNow),
            ct));

        // Signed adjustments stay signed through FX conversion. Therefore the sum of the converted
        // allocation rows still equals the converted net expense (apart from normal FX rounding).
        var baseAllocations = new List<ExpenseAllocation>(native.Count);
        foreach (var allocation in native)
        {
            var meta = metaById[allocation.TransactionId];
            var converted = acc.Convert(allocation.Amount, meta.Currency, meta.Date);
            if (converted.HasValue) baseAllocations.Add(allocation with { Amount = converted.Value });
        }

        if (refundRows.Count == 0) return (baseAllocations, acc.Incomplete);

        var proportionalByOriginal = new Dictionary<Guid, decimal>();
        var targetedByOriginalCategory = new Dictionary<(Guid Original, Guid Category), decimal>();
        foreach (var refund in refundRows)
        {
            var date = refund.BookingDate ?? refund.ValueDate ?? metaById[refund.OriginalId].Date;
            var converted = acc.Convert(refund.Amount, refund.Currency, date);
            if (!converted.HasValue) continue;
            if (refund.TargetCategoryId is { } category)
                targetedByOriginalCategory[(refund.OriginalId, category)] = targetedByOriginalCategory.GetValueOrDefault((refund.OriginalId, category)) + converted.Value;
            else
                proportionalByOriginal[refund.OriginalId] = proportionalByOriginal.GetValueOrDefault(refund.OriginalId) + converted.Value;
        }

        var adjusted = new List<ExpenseAllocation>(baseAllocations.Count);
        foreach (var group in baseAllocations.GroupBy(a => a.TransactionId))
        {
            var pre = group.ToList();
            var preTotal = pre.Sum(a => a.Amount);
            var lines = group.ToList();

            // Targeted refunds reduce the NET spend of that category. If a category currently has no
            // positive net spend (e.g. it only contains a coupon), fall back to proportional netting.
            foreach (var ((original, category), amount) in targetedByOriginalCategory)
            {
                if (original != group.Key || amount <= 0m) continue;
                var categoryTotal = lines.Where(line => line.CategoryId == category).Sum(line => line.Amount);
                if (categoryTotal > 0m)
                {
                    lines = lines.Select(line => line.CategoryId == category
                        ? line with { Amount = line.Amount - amount * (line.Amount / categoryTotal) }
                        : line).ToList();
                }
                else
                {
                    proportionalByOriginal[original] = proportionalByOriginal.GetValueOrDefault(original) + amount;
                }
            }

            // Signed lines are intentionally used as weights. Example: +15 products and -2 coupon on
            // a 13 EUR purchase; a full 13 EUR refund scales both to zero rather than leaving the coupon.
            if (proportionalByOriginal.TryGetValue(group.Key, out var refundedBase) && refundedBase > 0m)
            {
                var total = lines.Sum(a => a.Amount);
                if (total > 0m)
                {
                    for (var i = 0; i < lines.Count; i++)
                        lines[i] = lines[i] with { Amount = lines[i].Amount - refundedBase * (lines[i].Amount / total) };
                }
                else if (preTotal > 0m)
                {
                    for (var i = 0; i < lines.Count; i++)
                        lines[i] = lines[i] with { Amount = lines[i].Amount - refundedBase * (pre[i].Amount / preTotal) };
                }
                else if (lines.Count > 0)
                {
                    lines[0] = lines[0] with { Amount = lines[0].Amount - refundedBase };
                }
            }

            adjusted.AddRange(lines);
        }
        return (adjusted, acc.Incomplete);
    }
}
