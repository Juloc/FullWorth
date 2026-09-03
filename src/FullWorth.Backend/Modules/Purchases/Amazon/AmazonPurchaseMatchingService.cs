using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Accounts;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Purchases.Amazon;

public sealed record AmazonPaymentCandidate(Guid TransactionId, decimal Amount, DateOnly? BookingDate, string? Counterparty);
public sealed record AmazonPurchaseRemainder(Guid PurchaseId, decimal Amount, DateOnly PurchaseDate, string Currency);

public sealed class AmazonPurchaseMatchingService(FullWorthDbContext db, AmazonSqlStore amazonStore)
{
    public async Task<int> TryMatchPaymentsAsync(Guid userId, Guid fullWorthSpaceId, Guid purchaseId, CancellationToken ct)
    {
        var purchase = await db.Purchases.SingleOrDefaultAsync(x => x.Id == purchaseId && x.FullWorthSpaceId == fullWorthSpaceId && x.Source == "amazon", ct);
        if (purchase is null || !purchase.PurchaseDate.HasValue || purchase.TotalAmount <= 0) return 0;

        var metadata = await amazonStore.GetOrderMetadataAsync(purchaseId, ct);
        var nonBankAmount = Math.Clamp(metadata?.NonBankPaymentAmount ?? 0m, 0m, purchase.TotalAmount);
        var bankTarget = Math.Max(0m, purchase.TotalAmount - nonBankAmount);
        await EnsurePrimaryLinkAsync(purchase, bankTarget, ct);

        var existingLinks = await amazonStore.ListPaymentLinksAsync(purchaseId, ct);
        var remaining = bankTarget - existingLinks.Sum(x => x.AllocatedAmount);
        if (Math.Abs(remaining) <= .01m)
        {
            await ReconcilePurchaseAsync(purchase, ct);
            return 0;
        }
        if (remaining < 0m)
        {
            await ReconcilePurchaseAsync(purchase, ct);
            return 0;
        }

        var date = purchase.PurchaseDate.Value;
        var existingIds = existingLinks.Select(x => x.TransactionId).ToHashSet();
        var allLinks = await amazonStore.ListAllPaymentLinksAsync(ct);
        var allocatedByTransaction = allLinks.GroupBy(x => x.TransactionId)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.AllocatedAmount));

        var candidates = await LoadCandidateTransactionsAsync(userId, fullWorthSpaceId, purchase.Currency, date.AddDays(-3), date.AddDays(21), ct);
        var usable = candidates
            .Where(x => !existingIds.Contains(x.Id))
            .Select(x => new
            {
                Row = x,
                Available = Math.Max(0m, Math.Abs(x.Amount) - allocatedByTransaction.GetValueOrDefault(x.Id))
            })
            .Where(x => x.Available > .01m && (IsAmazon(x.Row.Counterparty) || Math.Abs(x.Available - remaining) <= .01m))
            .OrderByDescending(x => IsAmazon(x.Row.Counterparty))
            .ThenBy(x => x.Row.BookingDate)
            .Take(12)
            .Select(x => new AmazonPaymentCandidate(x.Row.Id, -x.Available, x.Row.BookingDate, x.Row.Counterparty))
            .ToList();

        var combination = FindBestCombination(usable, remaining, date);
        if (combination.Count == 0)
        {
            // Pre-orders and split shipments can be charged much later. Do not run the combinatorial
            // matcher over a year of data: only accept one exact, uniquely identifiable Amazon charge.
            var delayed = await LoadCandidateTransactionsAsync(userId, fullWorthSpaceId, purchase.Currency, date.AddDays(22), date.AddDays(365), ct);
            var exactDelayed = delayed
                .Where(x => !existingIds.Contains(x.Id) && IsAmazon(x.Counterparty))
                .Select(x => new
                {
                    Row = x,
                    Available = Math.Max(0m, Math.Abs(x.Amount) - allocatedByTransaction.GetValueOrDefault(x.Id))
                })
                .Where(x => x.Available > .01m && Math.Abs(x.Available - remaining) <= .01m)
                .Take(2)
                .ToList();
            if (exactDelayed.Count == 1)
                combination = [new AmazonPaymentCandidate(exactDelayed[0].Row.Id, -exactDelayed[0].Available, exactDelayed[0].Row.BookingDate, exactDelayed[0].Row.Counterparty)];
        }
        if (combination.Count == 0) return 0;

        foreach (var candidate in combination)
            await amazonStore.UpsertPaymentLinkAsync(purchase.Id, candidate.TransactionId, Math.Abs(candidate.Amount), CombinationConfidence(candidate, date), "auto_amazon", ct);

        if (!purchase.TransactionId.HasValue)
        {
            var primary = combination.OrderBy(x => Math.Abs((x.BookingDate ?? date).DayNumber - date.DayNumber)).First();
            purchase.TransactionId = primary.TransactionId;
            purchase.MatchConfidence = CombinationConfidence(primary, date);
        }
        await ReconcilePurchaseAsync(purchase, ct);
        return combination.Count;
    }

    public async Task<int> TryMatchCombinedPaymentsAsync(Guid userId, Guid fullWorthSpaceId, IReadOnlyList<Guid> purchaseIds, CancellationToken ct)
    {
        if (purchaseIds.Count < 2) return 0;
        var purchases = await db.Purchases
            .Where(x => purchaseIds.Contains(x.Id) && x.FullWorthSpaceId == fullWorthSpaceId && x.Source == "amazon" && x.PurchaseDate != null)
            .ToListAsync(ct);
        if (purchases.Count < 2) return 0;

        var remainders = new List<AmazonPurchaseRemainder>();
        foreach (var purchase in purchases)
        {
            var metadata = await amazonStore.GetOrderMetadataAsync(purchase.Id, ct);
            var nonBank = Math.Clamp(metadata?.NonBankPaymentAmount ?? 0m, 0m, purchase.TotalAmount);
            var links = await amazonStore.ListPaymentLinksAsync(purchase.Id, ct);
            var remaining = purchase.TotalAmount - nonBank - links.Sum(x => x.AllocatedAmount);
            if (remaining > .01m && purchase.PurchaseDate.HasValue)
                remainders.Add(new(purchase.Id, remaining, purchase.PurchaseDate.Value, purchase.Currency));
        }
        if (remainders.Count < 2) return 0;

        var minDate = remainders.Min(x => x.PurchaseDate).AddDays(-3);
        var maxDate = remainders.Max(x => x.PurchaseDate).AddDays(365);
        var transactions = await db.Transactions.AsNoTracking()
            .Where(tx => tx.Amount < 0 && tx.BookingDate >= minDate && tx.BookingDate <= maxDate)
            .Where(tx => db.Accounts.Any(account => account.Id == tx.AccountId && account.FullWorthSpaceId == fullWorthSpaceId &&
                account.Owners.Any(owner => owner.UserId == userId && owner.OwnershipType == AccountOwnershipTypes.Owner)))
            .Where(tx => tx.Counterparty != null && (EF.Functions.ILike(tx.Counterparty, "%amazon%") || EF.Functions.ILike(tx.Counterparty, "%amzn%")))
            .Select(tx => new { tx.Id, tx.BookingDate, tx.Amount, tx.Currency, tx.Counterparty })
            .ToListAsync(ct);

        var allLinks = await amazonStore.ListAllPaymentLinksAsync(ct);
        var allocatedByTransaction = allLinks.GroupBy(x => x.TransactionId)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.AllocatedAmount));
        var linkedCount = 0;

        foreach (var transaction in transactions.OrderBy(x => x.BookingDate))
        {
            if (!transaction.BookingDate.HasValue) continue;
            var available = Math.Max(0m, Math.Abs(transaction.Amount) - allocatedByTransaction.GetValueOrDefault(transaction.Id));
            if (available <= .01m) continue;
            var txDate = transaction.BookingDate.Value;
            var eligible = remainders
                .Where(x => x.Currency == transaction.Currency && x.Amount > .01m && x.PurchaseDate >= txDate.AddDays(-365) && x.PurchaseDate <= txDate.AddDays(3))
                .ToList();
            var combination = FindUniquePurchaseCombination(eligible, available);
            if (combination.Count < 2) continue;

            foreach (var item in combination)
            {
                await amazonStore.UpsertPaymentLinkAsync(item.PurchaseId, transaction.Id, item.Amount, .97m, "auto_amazon_combined", ct);
                var purchase = purchases.Single(x => x.Id == item.PurchaseId);
                if (!purchase.TransactionId.HasValue)
                {
                    purchase.TransactionId = transaction.Id;
                    purchase.MatchConfidence = .97m;
                }
                await ReconcilePurchaseAsync(purchase, ct);
                var index = remainders.FindIndex(x => x.PurchaseId == item.PurchaseId);
                if (index >= 0) remainders[index] = remainders[index] with { Amount = 0m };
                linkedCount++;
            }
            allocatedByTransaction[transaction.Id] = allocatedByTransaction.GetValueOrDefault(transaction.Id) + combination.Sum(x => x.Amount);
        }
        return linkedCount;
    }

    public async Task<int> TryMatchRefundsAsync(Guid userId, Guid fullWorthSpaceId, Guid purchaseId, CancellationToken ct)
    {
        var purchase = await db.Purchases.AsNoTracking().SingleOrDefaultAsync(x => x.Id == purchaseId && x.FullWorthSpaceId == fullWorthSpaceId, ct);
        if (purchase is null) return 0;

        var refunds = (await amazonStore.ListRefundsAsync(purchaseId, ct)).Where(x => x.Amount > 0 && x.TransactionId == null).ToList();
        var usedRefundTransactionIds = await amazonStore.ListAllLinkedRefundTransactionIdsAsync(ct);
        var linked = 0;
        foreach (var refund in refunds)
        {
            var anchor = refund.RefundDate ?? purchase.PurchaseDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
            var candidates = await db.Transactions.AsNoTracking()
                .Where(tx => tx.Amount > 0 && tx.Currency == refund.Currency && tx.BookingDate >= anchor.AddDays(-7) && tx.BookingDate <= anchor.AddDays(21))
                .Where(tx => Math.Abs(tx.Amount - refund.Amount) <= .01m)
                .Where(tx => db.Accounts.Any(account => account.Id == tx.AccountId && account.FullWorthSpaceId == fullWorthSpaceId &&
                    account.Owners.Any(owner => owner.UserId == userId && owner.OwnershipType == AccountOwnershipTypes.Owner)))
                .Where(tx => !usedRefundTransactionIds.Contains(tx.Id))
                .Select(tx => new { tx.Id, tx.BookingDate, tx.Counterparty })
                .ToListAsync(ct);

            var best = candidates.Where(x => IsAmazon(x.Counterparty))
                           .OrderBy(x => Math.Abs((x.BookingDate ?? anchor).DayNumber - anchor.DayNumber)).FirstOrDefault()
                       ?? candidates.OrderBy(x => Math.Abs((x.BookingDate ?? anchor).DayNumber - anchor.DayNumber)).FirstOrDefault();
            if (best is null) continue;

            var confidence = IsAmazon(best.Counterparty) ? .99m : .95m;
            if (await amazonStore.SetRefundTransactionAsync(refund.Id, best.Id, confidence, DateTimeOffset.UtcNow, ct) > 0)
            {
                usedRefundTransactionIds.Add(best.Id);
                linked++;
            }
        }
        return linked;
    }

    public static IReadOnlyList<AmazonPaymentCandidate> FindBestCombination(IReadOnlyList<AmazonPaymentCandidate> candidates, decimal target, DateOnly orderDate)
    {
        var usable = candidates.Where(x => x.Amount < 0 && Math.Abs(x.Amount) <= target + .01m).Take(12).ToArray();
        List<AmazonPaymentCandidate>? best = null;
        var bestScore = decimal.MinValue;
        var current = new List<AmazonPaymentCandidate>();

        void Search(int index, decimal sum)
        {
            if (current.Count > 5 || sum > target + .01m) return;
            if (Math.Abs(sum - target) <= .01m && current.Count > 0)
            {
                var score = current.Sum(x => CombinationConfidence(x, orderDate)) - (current.Count - 1) * .01m;
                if (score > bestScore) { bestScore = score; best = [.. current]; }
                return;
            }
            if (index >= usable.Length) return;
            for (var i = index; i < usable.Length; i++)
            {
                current.Add(usable[i]);
                Search(i + 1, sum + Math.Abs(usable[i].Amount));
                current.RemoveAt(current.Count - 1);
            }
        }

        Search(0, 0m);
        return best ?? [];
    }

    public static IReadOnlyList<AmazonPurchaseRemainder> FindUniquePurchaseCombination(IReadOnlyList<AmazonPurchaseRemainder> candidates, decimal target)
    {
        var usable = candidates.Where(x => x.Amount > .01m && x.Amount <= target + .01m).Take(14).ToArray();
        List<AmazonPurchaseRemainder>? solution = null;
        var solutionCount = 0;
        var current = new List<AmazonPurchaseRemainder>();

        void Search(int index, decimal sum)
        {
            if (solutionCount > 1 || current.Count > 5 || sum > target + .01m) return;
            if (Math.Abs(sum - target) <= .01m && current.Count >= 2)
            {
                solutionCount++;
                if (solutionCount == 1) solution = [.. current];
                return;
            }
            if (index >= usable.Length) return;
            for (var i = index; i < usable.Length; i++)
            {
                current.Add(usable[i]);
                Search(i + 1, sum + usable[i].Amount);
                current.RemoveAt(current.Count - 1);
                if (solutionCount > 1) return;
            }
        }

        Search(0, 0m);
        return solutionCount == 1 ? solution! : [];
    }

    private async Task EnsurePrimaryLinkAsync(Purchase purchase, decimal bankTarget, CancellationToken ct)
    {
        if (!purchase.TransactionId.HasValue || bankTarget <= .01m) return;
        var existing = await amazonStore.ListPaymentLinksAsync(purchase.Id, ct);
        if (existing.Any(x => x.TransactionId == purchase.TransactionId.Value)) return;
        var transaction = await db.Transactions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == purchase.TransactionId.Value, ct);
        if (transaction is null || transaction.Amount >= 0 || transaction.Currency != purchase.Currency) return;
        var allLinks = await amazonStore.ListAllPaymentLinksAsync(ct);
        var allocatedElsewhere = allLinks.Where(x => x.TransactionId == transaction.Id).Sum(x => x.AllocatedAmount);
        var available = Math.Max(0m, Math.Abs(transaction.Amount) - allocatedElsewhere);
        var allocation = Math.Min(bankTarget, available);
        if (allocation > .01m)
            await amazonStore.UpsertPaymentLinkAsync(purchase.Id, transaction.Id, allocation, purchase.MatchConfidence, "legacy", ct);
    }

    private async Task ReconcilePurchaseAsync(Purchase purchase, CancellationToken ct)
    {
        var links = await amazonStore.ListPaymentLinksAsync(purchase.Id, ct);
        var metadata = await amazonStore.GetOrderMetadataAsync(purchase.Id, ct);
        var accounted = links.Sum(x => x.AllocatedAmount) + Math.Clamp(metadata?.NonBankPaymentAmount ?? 0m, 0m, purchase.TotalAmount);
        purchase.Status = Math.Abs(accounted - purchase.TotalAmount) <= .01m ? "confirmed" : "review";
        purchase.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private async Task<List<PaymentTransactionRow>> LoadCandidateTransactionsAsync(Guid userId, Guid fullWorthSpaceId, string currency, DateOnly from, DateOnly to, CancellationToken ct) =>
        await db.Transactions.AsNoTracking()
            .Where(tx => tx.Amount < 0 && tx.Currency == currency && tx.BookingDate >= from && tx.BookingDate <= to)
            .Where(tx => db.Accounts.Any(account => account.Id == tx.AccountId && account.FullWorthSpaceId == fullWorthSpaceId &&
                account.Owners.Any(owner => owner.UserId == userId && owner.OwnershipType == AccountOwnershipTypes.Owner)))
            .Select(tx => new PaymentTransactionRow(tx.Id, tx.BookingDate, tx.Amount, tx.Counterparty))
            .ToListAsync(ct);

    private sealed record PaymentTransactionRow(Guid Id, DateOnly? BookingDate, decimal Amount, string? Counterparty);

    private static decimal CombinationConfidence(AmazonPaymentCandidate candidate, DateOnly orderDate)
    {
        var distance = candidate.BookingDate.HasValue ? Math.Abs(candidate.BookingDate.Value.DayNumber - orderDate.DayNumber) : 10;
        var dateScore = Math.Max(0m, 1m - distance / 30m);
        return Math.Clamp((IsAmazon(candidate.Counterparty) ? .75m : .55m) + dateScore * .24m, 0m, .99m);
    }

    private static bool IsAmazon(string? counterparty) => !string.IsNullOrWhiteSpace(counterparty) &&
        (counterparty.Contains("amazon", StringComparison.OrdinalIgnoreCase) || counterparty.Contains("amzn", StringComparison.OrdinalIgnoreCase));
}