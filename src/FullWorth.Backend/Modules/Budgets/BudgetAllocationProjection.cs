using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Budgets;

/// <summary>
/// Shared budget projection that follows the same accounting precedence as transaction analytics:
/// explicit allocations replace the parent transaction category; without allocations the parent
/// category is used. Allocation amounts keep ledger sign, so positive coupon/discount adjustments
/// reduce the category's net spend instead of being converted to an additional expense via Abs().
/// It never adds purchase totals on top of transaction money.
/// </summary>
internal sealed class BudgetAllocationProjection(FullWorthDbContext db)
{
    public async Task<(decimal Spent, List<BudgetContributionRow> Rows)> BuildAsync(
        Guid fullWorthSpaceId,
        Guid? userId,
        Guid? categoryId,
        DateOnly from,
        DateOnly to,
        int contributionLimit,
        CancellationToken ct)
    {
        var txQuery = db.Transactions.AsNoTracking().Where(transaction =>
            !transaction.IsIgnored && !transaction.IsTransfer && transaction.Amount < 0 &&
            transaction.BookingDate != null && transaction.BookingDate >= from && transaction.BookingDate <= to &&
            db.Accounts.Any(account => account.Id == transaction.AccountId && account.FullWorthSpaceId == fullWorthSpaceId &&
                (!userId.HasValue || account.Owners.Any(owner => owner.UserId == userId.Value))));

        var transactions = await txQuery
            .Select(transaction => new { transaction.Id, transaction.BookingDate, transaction.Counterparty, transaction.Amount, transaction.Currency, transaction.CategoryId, transaction.UpdatedAt })
            .ToListAsync(ct);
        if (transactions.Count == 0) return (0m, []);

        var ids = transactions.Select(x => x.Id).ToArray();
        var allocationRows = await db.TransactionAllocations.AsNoTracking().Where(x => ids.Contains(x.TransactionId))
            .Select(x => new { x.TransactionId, x.CategoryId, x.Amount }).ToListAsync(ct);
        var byTransaction = allocationRows.GroupBy(x => x.TransactionId).ToDictionary(x => x.Key, x => x.ToList());
        var categoryNames = await db.Categories.AsNoTracking().Where(x => x.FullWorthSpaceId == fullWorthSpaceId)
            .ToDictionaryAsync(x => x.Id, x => x.Name, ct);

        var contributions = new List<BudgetContributionRow>();
        decimal spent = 0m;
        foreach (var transaction in transactions)
        {
            decimal nativeSpend;
            Guid? effectiveCategory;
            if (categoryId.HasValue)
            {
                if (byTransaction.TryGetValue(transaction.Id, out var lines) && lines.Count > 0)
                {
                    // Expense allocation is negative ledger money; a positive adjustment is a coupon.
                    // Net the category first so -15 + 2 becomes 13 EUR spend, not 17 EUR.
                    nativeSpend = -lines.Where(x => x.CategoryId == categoryId.Value).Sum(x => x.Amount);
                    effectiveCategory = categoryId;
                }
                else
                {
                    nativeSpend = transaction.CategoryId == categoryId.Value ? Math.Abs(transaction.Amount) : 0m;
                    effectiveCategory = transaction.CategoryId;
                }
            }
            else
            {
                // An all-category budget measures actual bank cash outflow once, regardless of the
                // number or sign of detail allocation rows.
                nativeSpend = Math.Abs(transaction.Amount);
                effectiveCategory = transaction.CategoryId;
            }

            if (nativeSpend <= 0m) continue;
            spent += nativeSpend;
            categoryNames.TryGetValue(effectiveCategory ?? Guid.Empty, out var categoryName);
            contributions.Add(new BudgetContributionRow(transaction.Id, transaction.BookingDate, transaction.Counterparty, -nativeSpend, transaction.Currency, categoryName));
        }

        // Refund transactions are positive and therefore absent above. Net linked refunds against the
        // original budget category instead of treating them as income. Targeted refunds reduce only the
        // selected category; untargeted refunds reduce the original proportionally.
        var refundRows = await db.Transactions.AsNoTracking()
            .Where(refund => refund.RefundOfTransactionId.HasValue && ids.Contains(refund.RefundOfTransactionId.Value) && refund.Amount > 0m && !refund.IsIgnored && !refund.IsTransfer)
            .Select(refund => new { OriginalId = refund.RefundOfTransactionId!.Value, refund.RefundCategoryId, refund.Amount, refund.Currency, refund.BookingDate, refund.Counterparty })
            .ToListAsync(ct);
        foreach (var refund in refundRows)
        {
            if (categoryId.HasValue && refund.RefundCategoryId.HasValue && refund.RefundCategoryId != categoryId) continue;
            var original = transactions.First(x => x.Id == refund.OriginalId);
            decimal reduction;
            if (!categoryId.HasValue) reduction = Math.Abs(refund.Amount);
            else if (refund.RefundCategoryId == categoryId) reduction = Math.Abs(refund.Amount);
            else
            {
                decimal originalCategorySpend;
                if (byTransaction.TryGetValue(original.Id, out var lines) && lines.Count > 0)
                    originalCategorySpend = -lines.Where(x => x.CategoryId == categoryId).Sum(x => x.Amount);
                else
                    originalCategorySpend = original.CategoryId == categoryId ? Math.Abs(original.Amount) : 0m;
                if (originalCategorySpend <= 0m) continue;
                reduction = Math.Abs(refund.Amount) * (originalCategorySpend / Math.Abs(original.Amount));
            }
            spent -= reduction;
        }

        spent = Math.Max(0m, spent);
        var rows = contributions.OrderByDescending(x => x.BookingDate).Take(Math.Max(0, contributionLimit)).ToList();
        return (spent, rows);
    }
}
