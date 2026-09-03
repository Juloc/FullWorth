using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.Audit;
using FullWorth.Backend.Modules.Merchants;
using FullWorth.Backend.Modules.Transactions;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Import;

public sealed record FinanzguruReconciliationResult(int AccountsReconciled, int TransactionsMoved, int TransactionsMerged);

/// <summary>
/// Reattaches historical Finanzguru imports to a real bank account once that account is connected.
/// Matching is deliberately conservative: same FullWorthSpace, currency, IBAN last-4 and at least one
/// common account owner. Ambiguous matches are left untouched rather than risking a cross-account merge.
/// </summary>
public sealed class FinanzguruAccountReconciliationService(FullWorthDbContext db, AuditService audit)
{
    public const string ImportProvider = "finanzguru-import";

    public async Task<FinanzguruReconciliationResult> ReconcileAsync(
        Guid fullWorthSpaceId,
        IEnumerable<FinanceAccount> candidateLiveAccounts,
        CancellationToken ct)
    {
        var liveAccounts = candidateLiveAccounts
            .Where(account => account.FullWorthSpaceId == fullWorthSpaceId
                              && account.Provider != ImportProvider
                              && !string.IsNullOrWhiteSpace(account.IbanLast4))
            .GroupBy(account => account.Id)
            .Select(group => group.First())
            .ToList();
        if (liveAccounts.Count == 0) return new(0, 0, 0);

        var liveIds = liveAccounts.Select(account => account.Id).ToArray();
        var liveOwnerRows = await db.AccountOwners.AsNoTracking()
            .Where(owner => liveIds.Contains(owner.AccountId) && owner.OwnershipType == AccountOwnershipTypes.Owner)
            .Select(owner => new { owner.AccountId, owner.UserId })
            .ToListAsync(ct);
        var liveOwners = liveOwnerRows
            .GroupBy(row => row.AccountId)
            .ToDictionary(group => group.Key, group => group.Select(row => row.UserId).ToHashSet());

        var importAccounts = await db.Accounts
            .Where(account => account.FullWorthSpaceId == fullWorthSpaceId && account.Provider == ImportProvider)
            .Include(account => account.Owners)
            .ToListAsync(ct);

        var accountsReconciled = 0;
        var transactionsMoved = 0;
        var transactionsMerged = 0;

        foreach (var importedAccount in importAccounts)
        {
            // Import-only accounts represent history, never a current balance. Keep them archived even
            // if no safe live match can be established yet.
            importedAccount.IsActive = false;
            importedAccount.IncludeInNetWorth = false;
            importedAccount.UpdatedAt = DateTimeOffset.UtcNow;

            if (string.IsNullOrWhiteSpace(importedAccount.IbanLast4)) continue;
            var importedOwners = importedAccount.Owners
                .Where(owner => owner.OwnershipType == AccountOwnershipTypes.Owner)
                .Select(owner => owner.UserId)
                .ToHashSet();
            if (importedOwners.Count == 0) continue;

            var matches = liveAccounts.Where(live =>
                    string.Equals(live.IbanLast4, importedAccount.IbanLast4, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(live.Currency, importedAccount.Currency, StringComparison.OrdinalIgnoreCase)
                    && liveOwners.TryGetValue(live.Id, out var owners)
                    && owners.Overlaps(importedOwners))
                .ToList();
            if (matches.Count != 1) continue;

            var result = await ReconcileAccountAsync(importedAccount, matches[0], ct);
            accountsReconciled++;
            transactionsMoved += result.Moved;
            transactionsMerged += result.Merged;
            audit.Record(fullWorthSpaceId, null, "finanzguru.account.reconciled", "FinanceAccount", matches[0].Id);
        }

        await db.SaveChangesAsync(ct);
        return new(accountsReconciled, transactionsMoved, transactionsMerged);
    }

    private async Task<(int Moved, int Merged)> ReconcileAccountAsync(FinanceAccount importedAccount, FinanceAccount liveAccount, CancellationToken ct)
    {
        var imported = await db.Transactions
            .Where(transaction => transaction.AccountId == importedAccount.Id)
            .OrderBy(transaction => transaction.BookingDate)
            .ThenBy(transaction => transaction.Id)
            .ToListAsync(ct);
        if (imported.Count == 0) return (0, 0);

        var live = await db.Transactions
            .Where(transaction => transaction.AccountId == liveAccount.Id
                                  && transaction.Status != "PDNG"
                                  && !transaction.ExternalKey.StartsWith("finanzguru:"))
            .OrderBy(transaction => transaction.BookingDate)
            .ThenBy(transaction => transaction.Id)
            .ToListAsync(ct);
        var liveBySignature = live
            .GroupBy(Signature)
            .ToDictionary(group => group.Key, group => new Queue<FinanceTransaction>(group));

        var moved = 0;
        var merged = 0;
        foreach (var historical in imported)
        {
            if (liveBySignature.TryGetValue(Signature(historical), out var matches) && matches.Count > 0)
            {
                var providerTransaction = matches.Dequeue();
                await MergeIntoLiveTransactionAsync(historical, providerTransaction, ct);
                merged++;
            }
            else
            {
                // Provider may not expose the full historical range. Keep the Finanzguru row, but attach
                // it to the real account. A later broader bank sync can still merge it automatically.
                historical.AccountId = liveAccount.Id;
                historical.UpdatedAt = DateTimeOffset.UtcNow;
                moved++;
            }
        }

        return (moved, merged);
    }

    private async Task MergeIntoLiveTransactionAsync(FinanceTransaction historical, FinanceTransaction live, CancellationToken ct)
    {
        var historicalAllocations = await db.TransactionAllocations
            .Where(allocation => allocation.TransactionId == historical.Id)
            .ToListAsync(ct);
        var liveHasAllocations = await db.TransactionAllocations.AsNoTracking()
            .AnyAsync(allocation => allocation.TransactionId == live.Id, ct);
        if (!liveHasAllocations)
        {
            foreach (var allocation in historicalAllocations)
            {
                allocation.TransactionId = live.Id;
                allocation.UpdatedAt = DateTimeOffset.UtcNow;
            }
        }
        else
        {
            db.TransactionAllocations.RemoveRange(historicalAllocations);
        }

        // Preserve anything the user attached to the historical row before connecting the bank.
        foreach (var purchase in await db.Purchases.Where(purchase => purchase.TransactionId == historical.Id).ToListAsync(ct))
            purchase.TransactionId = live.Id;
        foreach (var refund in await db.Transactions.Where(transaction => transaction.RefundOfTransactionId == historical.Id).ToListAsync(ct))
            refund.RefundOfTransactionId = live.Id;
        foreach (var suggestion in await db.PriceChangeSuggestions.Where(suggestion => suggestion.EvidenceTransactionId == historical.Id).ToListAsync(ct))
            suggestion.EvidenceTransactionId = live.Id;

        // Manual user edits always win. Otherwise retain the imported Finanzguru category/split when the
        // bank row has only automatic/no categorization.
        if (historical.CategorizationSource == "manual")
        {
            live.CategoryId = historical.CategoryId;
            live.IsIgnored = historical.IsIgnored;
            live.IsTransfer = historical.IsTransfer;
            live.TransferPurpose = historical.TransferPurpose;
            live.UserNote = historical.UserNote;
            live.CategorizationSource = "manual";
        }
        else if (live.CategorizationSource != "manual")
        {
            if (historical.CategoryId.HasValue) live.CategoryId = historical.CategoryId;
            if (historical.CategorizationSource == "finanzguru" && (historical.CategoryId.HasValue || historicalAllocations.Count > 0))
                live.CategorizationSource = "finanzguru";
            if (historical.IsTransfer) live.IsTransfer = true;
            if (string.IsNullOrWhiteSpace(live.UserNote) && !string.IsNullOrWhiteSpace(historical.UserNote))
                live.UserNote = historical.UserNote;
        }

        live.UpdatedAt = DateTimeOffset.UtcNow;
        db.Transactions.Remove(historical);
    }

    private static TransactionSignature Signature(FinanceTransaction transaction) => new(
        transaction.BookingDate ?? transaction.ValueDate,
        transaction.Amount,
        transaction.Currency.ToUpperInvariant(),
        transaction.NormalizedCounterparty ?? MerchantNormalization.Normalize(transaction.Counterparty) ?? NormalizeDescription(transaction.Description));

    private static string? NormalizeDescription(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
    }

    private sealed record TransactionSignature(DateOnly? Date, decimal Amount, string Currency, string? Party);
}
