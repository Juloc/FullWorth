using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.Audit;
using FullWorth.Backend.Modules.Merchants;
using FullWorth.Backend.Modules.Transactions;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Import;

public sealed record FinanzguruReconciliationResult(int AccountsReconciled, int TransactionsMoved, int TransactionsMerged);

public sealed record FinanzguruExplicitLinkResult(
    Guid ImportAccountId,
    Guid TargetAccountId,
    int TransactionsMoved,
    int TransactionsMerged,
    int TransactionsTrustedForHistory,
    bool CurrentBalanceAdded);

public sealed record FinanzguruImportAccountLinkView(
    Guid Id,
    string DisplayName,
    string Currency,
    string? IbanLast4,
    int TransactionCount,
    DateOnly? FirstBookingDate,
    DateOnly? LastBookingDate,
    Guid? SuggestedTargetAccountId,
    Guid? LinkedTargetAccountId);

public sealed record FinanzguruTargetAccountLinkView(
    Guid Id,
    string DisplayName,
    string InstitutionName,
    string Currency,
    string? IbanLast4,
    bool HasCurrentBalance,
    bool IsActive,
    bool IncludeInNetWorth);

public sealed record FinanzguruAttachedHistoryView(
    Guid TargetAccountId,
    string DisplayName,
    string InstitutionName,
    string Currency,
    int TransactionCount,
    DateOnly? FirstBookingDate,
    DateOnly? LastBookingDate,
    bool HasCurrentBalance);

public sealed record FinanzguruLinkOptionsView(
    IReadOnlyList<FinanzguruImportAccountLinkView> ImportAccounts,
    IReadOnlyList<FinanzguruTargetAccountLinkView> TargetAccounts,
    IReadOnlyList<FinanzguruAttachedHistoryView> AttachedHistory);

/// <summary>
/// Reattaches historical Finanzguru imports to a real bank account once that account is connected.
/// Matching is deliberately conservative: same FullWorthSpace, currency, IBAN last-4 and at least one
/// common account owner. Ambiguous matches are left untouched rather than risking a cross-account merge.
/// </summary>
public sealed class FinanzguruAccountReconciliationService(FullWorthDbContext db, AuditService audit)
{
    public const string ImportProvider = "finanzguru-import";

    public async Task<FinanzguruLinkOptionsView?> ListLinkOptionsAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        CancellationToken ct)
    {
        var isMember = await db.FullWorthSpaceMembers.AsNoTracking()
            .AnyAsync(member => member.FullWorthSpaceId == fullWorthSpaceId && member.UserId == userId, ct);
        if (!isMember) return null;

        var importAccounts = await db.Accounts.AsNoTracking()
            .Where(account =>
                account.FullWorthSpaceId == fullWorthSpaceId &&
                account.Provider == ImportProvider &&
                account.Owners.Any(owner => owner.UserId == userId && owner.OwnershipType == AccountOwnershipTypes.Owner))
            .OrderBy(account => account.DisplayName)
            .ToListAsync(ct);

        var targets = await db.Accounts.AsNoTracking()
            .Where(account =>
                account.FullWorthSpaceId == fullWorthSpaceId &&
                account.Provider != ImportProvider &&
                account.Owners.Any(owner => owner.UserId == userId && owner.OwnershipType == AccountOwnershipTypes.Owner))
            .OrderByDescending(account => account.IsActive)
            .ThenBy(account => account.InstitutionName)
            .ThenBy(account => account.DisplayName)
            .ToListAsync(ct);

        var targetIds = targets.Select(account => account.Id).ToArray();
        var targetBalanceIds = (await db.BalanceSnapshots.AsNoTracking()
                .Where(balance => targetIds.Contains(balance.AccountId))
                .Select(balance => balance.AccountId)
                .Distinct()
                .ToListAsync(ct))
            .ToHashSet();
        var targetViews = targets.Select(account => new FinanzguruTargetAccountLinkView(
            account.Id,
            account.DisplayName,
            account.InstitutionName,
            account.Currency,
            account.IbanLast4,
            targetBalanceIds.Contains(account.Id),
            account.IsActive,
            account.IncludeInNetWorth)).ToArray();

        var importViews = new List<FinanzguruImportAccountLinkView>();
        foreach (var account in importAccounts)
        {
            var dates = await db.Transactions.AsNoTracking()
                .Where(transaction => transaction.AccountId == account.Id)
                .Select(transaction => transaction.BookingDate ?? transaction.ValueDate)
                .ToListAsync(ct);
            var knownDates = dates.Where(date => date.HasValue).Select(date => date!.Value).ToArray();
            if (dates.Count == 0) continue;

            var matchingTargets = targets
                .Where(target =>
                    string.Equals(target.Currency, account.Currency, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(account.IbanLast4) &&
                    string.Equals(target.IbanLast4, account.IbanLast4, StringComparison.OrdinalIgnoreCase))
                .Select(target => target.Id)
                .ToList();
            var suggested = account.ImportLinkedAccountId
                ?? (matchingTargets.Count == 1 ? matchingTargets[0] : (Guid?)null);

            importViews.Add(new FinanzguruImportAccountLinkView(
                account.Id,
                account.DisplayName,
                account.Currency,
                account.IbanLast4,
                dates.Count,
                knownDates.Length == 0 ? null : knownDates.Min(),
                knownDates.Length == 0 ? null : knownDates.Max(),
                suggested,
                account.ImportLinkedAccountId));
        }

        var pendingDirectRows = await db.Transactions.AsNoTracking()
            .Where(transaction =>
                targetIds.Contains(transaction.AccountId) &&
                transaction.ExternalKey.StartsWith("finanzguru:") &&
                !transaction.UseForBalanceHistory)
            .Select(transaction => new
            {
                transaction.AccountId,
                Date = transaction.BookingDate ?? transaction.ValueDate
            })
            .ToListAsync(ct);
        var targetsById = targets.ToDictionary(account => account.Id);
        var attached = pendingDirectRows
            .GroupBy(row => row.AccountId)
            .Select(group =>
            {
                var account = targetsById[group.Key];
                var dates = group.Where(row => row.Date.HasValue).Select(row => row.Date!.Value).ToArray();
                return new FinanzguruAttachedHistoryView(
                    account.Id,
                    account.DisplayName,
                    account.InstitutionName,
                    account.Currency,
                    group.Count(),
                    dates.Length == 0 ? null : dates.Min(),
                    dates.Length == 0 ? null : dates.Max(),
                    targetBalanceIds.Contains(account.Id));
            })
            .OrderBy(item => item.InstitutionName)
            .ThenBy(item => item.DisplayName)
            .ToArray();

        return new FinanzguruLinkOptionsView(importViews, targetViews, attached);
    }

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

            var matches = importedAccount.ImportLinkedAccountId is { } linkedId
                ? liveAccounts.Where(live =>
                        live.Id == linkedId &&
                        liveOwners.TryGetValue(live.Id, out var owners) &&
                        owners.Overlaps(importedOwners))
                    .ToList()
                : liveAccounts.Where(live =>
                        string.Equals(live.IbanLast4, importedAccount.IbanLast4, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(live.Currency, importedAccount.Currency, StringComparison.OrdinalIgnoreCase)
                        && liveOwners.TryGetValue(live.Id, out var owners)
                        && owners.Overlaps(importedOwners))
                    .ToList();
            if (matches.Count != 1) continue;

            var result = await ReconcileAccountAsync(importedAccount, matches[0], trustMovedHistory: false, ct);
            accountsReconciled++;
            transactionsMoved += result.Moved;
            transactionsMerged += result.Merged;
            audit.Record(fullWorthSpaceId, null, "finanzguru.account.reconciled", "FinanceAccount", matches[0].Id);
        }

        await db.SaveChangesAsync(ct);
        return new(accountsReconciled, transactionsMoved, transactionsMerged);
    }

    public async Task<FinanzguruExplicitLinkResult?> LinkExplicitAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        Guid importAccountId,
        Guid targetAccountId,
        decimal? currentBalance,
        string? currentBalanceCurrency,
        CancellationToken ct)
    {
        if (importAccountId == targetAccountId) return null;

        var importedAccount = await db.Accounts
            .Where(account =>
                account.Id == importAccountId &&
                account.FullWorthSpaceId == fullWorthSpaceId &&
                account.Provider == ImportProvider &&
                account.Owners.Any(owner => owner.UserId == userId && owner.OwnershipType == AccountOwnershipTypes.Owner))
            .SingleOrDefaultAsync(ct);
        var targetAccount = await db.Accounts
            .Where(account =>
                account.Id == targetAccountId &&
                account.FullWorthSpaceId == fullWorthSpaceId &&
                account.Provider != ImportProvider &&
                account.Owners.Any(owner => owner.UserId == userId && owner.OwnershipType == AccountOwnershipTypes.Owner))
            .SingleOrDefaultAsync(ct);
        if (importedAccount is null || targetAccount is null) return null;

        if (!string.Equals(importedAccount.Currency, targetAccount.Currency, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Import and target account must use the same currency.");

        var balanceAdded = await EnsureCurrentBalanceAsync(
            targetAccount, currentBalance, currentBalanceCurrency, ct);

        var result = await ReconcileAccountAsync(importedAccount, targetAccount, trustMovedHistory: true, ct);

        // Automatic reconciliation may already have moved imported rows onto this target account. Explicit
        // user confirmation upgrades every remaining Finanzguru row on the target to a trusted history source.
        var importedRowsOnTarget = await db.Transactions
            .Where(transaction =>
                transaction.AccountId == targetAccount.Id &&
                transaction.ExternalKey.StartsWith("finanzguru:"))
            .ToListAsync(ct);
        foreach (var row in importedRowsOnTarget)
        {
            row.UseForBalanceHistory = true;
            row.UpdatedAt = DateTimeOffset.UtcNow;
        }

        importedAccount.IsActive = false;
        importedAccount.IncludeInNetWorth = false;
        importedAccount.ImportLinkedAccountId = targetAccount.Id;
        importedAccount.UpdatedAt = DateTimeOffset.UtcNow;
        targetAccount.IncludeInNetWorth = true;
        targetAccount.UpdatedAt = DateTimeOffset.UtcNow;

        audit.Record(fullWorthSpaceId, userId, "finanzguru.account.linked_explicitly", "FinanceAccount", targetAccount.Id);
        await db.SaveChangesAsync(ct);

        return new FinanzguruExplicitLinkResult(
            importedAccount.Id,
            targetAccount.Id,
            result.Moved,
            result.Merged,
            importedRowsOnTarget.Count,
            balanceAdded);
    }

    public async Task<FinanzguruExplicitLinkResult?> ConfirmAttachedHistoryAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        Guid targetAccountId,
        decimal? currentBalance,
        string? currentBalanceCurrency,
        CancellationToken ct)
    {
        var targetAccount = await db.Accounts
            .Where(account =>
                account.Id == targetAccountId &&
                account.FullWorthSpaceId == fullWorthSpaceId &&
                account.Provider != ImportProvider &&
                account.Owners.Any(owner => owner.UserId == userId && owner.OwnershipType == AccountOwnershipTypes.Owner))
            .SingleOrDefaultAsync(ct);
        if (targetAccount is null) return null;

        var balanceAdded = await EnsureCurrentBalanceAsync(targetAccount, currentBalance, currentBalanceCurrency, ct);
        var rows = await db.Transactions
            .Where(transaction =>
                transaction.AccountId == targetAccount.Id &&
                transaction.ExternalKey.StartsWith("finanzguru:") &&
                !transaction.UseForBalanceHistory)
            .ToListAsync(ct);
        foreach (var row in rows)
        {
            row.UseForBalanceHistory = true;
            row.UpdatedAt = DateTimeOffset.UtcNow;
        }

        targetAccount.IncludeInNetWorth = true;
        targetAccount.UpdatedAt = DateTimeOffset.UtcNow;
        audit.Record(fullWorthSpaceId, userId, "finanzguru.history.confirmed", "FinanceAccount", targetAccount.Id);
        await db.SaveChangesAsync(ct);

        return new FinanzguruExplicitLinkResult(
            Guid.Empty,
            targetAccount.Id,
            0,
            0,
            rows.Count,
            balanceAdded);
    }

    private async Task<bool> EnsureCurrentBalanceAsync(
        FinanceAccount targetAccount,
        decimal? currentBalance,
        string? currentBalanceCurrency,
        CancellationToken ct)
    {
        var hasCurrentBalance = await db.BalanceSnapshots.AsNoTracking()
            .AnyAsync(balance => balance.AccountId == targetAccount.Id, ct);
        if (!hasCurrentBalance && !currentBalance.HasValue)
            throw new ArgumentException("The target account has no balance. Enter the current balance to anchor the imported history.");

        if (!currentBalance.HasValue) return false;
        if (Math.Abs(currentBalance.Value) >= 1_000_000_000_000m)
            throw new ArgumentException("Current balance must be less than 1,000,000,000,000.");

        var currency = NormalizeCurrency(currentBalanceCurrency ?? targetAccount.Currency);
        if (!string.Equals(currency, targetAccount.Currency, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Current balance currency must match the target account currency.");

        var now = DateTimeOffset.UtcNow;
        db.BalanceSnapshots.Add(new BalanceSnapshot
        {
            AccountId = targetAccount.Id,
            Amount = currentBalance.Value,
            Currency = currency,
            BalanceType = "manualCurrent",
            ReferenceDate = DateOnly.FromDateTime(now.UtcDateTime),
            CapturedAt = now
        });
        return true;
    }

    private async Task<(int Moved, int Merged)> ReconcileAccountAsync(
        FinanceAccount importedAccount,
        FinanceAccount liveAccount,
        bool trustMovedHistory,
        CancellationToken ct)
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
                historical.UseForBalanceHistory = trustMovedHistory;
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

        live.UseForBalanceHistory = true;
        live.UpdatedAt = DateTimeOffset.UtcNow;
        db.Transactions.Remove(historical);
    }

    private static TransactionSignature Signature(FinanceTransaction transaction) => new(
        transaction.BookingDate ?? transaction.ValueDate,
        transaction.Amount,
        transaction.Currency.ToUpperInvariant(),
        transaction.NormalizedCounterparty ?? MerchantNormalization.Normalize(transaction.Counterparty) ?? NormalizeDescription(transaction.Description));

    private static string NormalizeCurrency(string value)
    {
        var normalized = value.Trim().ToUpperInvariant();
        if (normalized.Length != 3 || normalized.Any(character => character is < 'A' or > 'Z'))
            throw new ArgumentException("Currency must be a three-letter code.");
        return normalized;
    }

    private static string? NormalizeDescription(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
    }

    private sealed record TransactionSignature(DateOnly? Date, decimal Amount, string Currency, string? Party);
}
