using System.Collections.Concurrent;
using System.Data.Common;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.Budgets;
using FullWorth.Backend.Modules.Contracts;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Portfolio;
using FullWorth.Backend.Modules.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace FullWorth.Backend.Data;

/// <summary>
/// Describes source-data changes that can invalidate derived financial views. Analytics are queried
/// directly from the source rows, but net-worth history is materialized and therefore needs an
/// explicit refresh whenever balances, balance-affecting transactions, account visibility/ownership,
/// assets or liabilities change.
/// </summary>
public sealed class FinancialDataChangeSet
{
    public HashSet<Guid> FullWorthSpaceIds { get; } = [];
    public HashSet<Guid> AccountIds { get; } = [];
    public DateOnly? EarliestDate { get; private set; }
    public bool FullHistory { get; private set; }
    public bool NetWorthAffected { get; private set; }
    public bool SignalsAffected { get; private set; }
    public bool HasChanges => FullWorthSpaceIds.Count > 0 || AccountIds.Count > 0;

    public void MarkSpace(
        Guid fullWorthSpaceId,
        DateOnly? from = null,
        bool fullHistory = false,
        bool netWorth = true,
        bool signals = true)
    {
        if (fullWorthSpaceId != Guid.Empty) FullWorthSpaceIds.Add(fullWorthSpaceId);
        MarkDate(from);
        if (fullHistory) FullHistory = true;
        NetWorthAffected |= netWorth;
        SignalsAffected |= signals;
    }

    public void MarkAccount(
        Guid accountId,
        DateOnly? from = null,
        bool fullHistory = false,
        bool netWorth = true,
        bool signals = true)
    {
        if (accountId != Guid.Empty) AccountIds.Add(accountId);
        MarkDate(from);
        if (fullHistory) FullHistory = true;
        NetWorthAffected |= netWorth;
        SignalsAffected |= signals;
    }

    public void Merge(FinancialDataChangeSet other)
    {
        FullWorthSpaceIds.UnionWith(other.FullWorthSpaceIds);
        AccountIds.UnionWith(other.AccountIds);
        MarkDate(other.EarliestDate);
        FullHistory |= other.FullHistory;
        NetWorthAffected |= other.NetWorthAffected;
        SignalsAffected |= other.SignalsAffected;
    }

    private void MarkDate(DateOnly? date)
    {
        if (!date.HasValue) return;
        if (!EarliestDate.HasValue || date.Value < EarliestDate.Value) EarliestDate = date.Value;
    }
}

/// <summary>
/// Keeps invalidations attached to the DbContext until its transaction is committed. This is
/// important for bank sync/import: both use an explicit transaction and perform several SaveChanges
/// calls before CommitAsync. Rebuilding after an intermediate SaveChanges would only see stale data
/// from a second DbContext.
/// </summary>
public sealed class FinancialDataConsistencyState
{
    private readonly ConcurrentDictionary<FullWorthDbContext, FinancialDataChangeSet> pending = new();

    public void Capture(FullWorthDbContext db)
    {
        var detected = FinancialDataChangeDetector.Detect(db);
        if (!detected.HasChanges) return;
        pending.AddOrUpdate(db, detected, (_, existing) =>
        {
            existing.Merge(detected);
            return existing;
        });
    }

    public bool TryTake(DbContext? context, out FinancialDataChangeSet changes)
    {
        if (context is FullWorthDbContext financeDb && pending.TryRemove(financeDb, out var found))
        {
            changes = found;
            return true;
        }
        changes = new FinancialDataChangeSet();
        return false;
    }

    public void Drop(DbContext? context)
    {
        if (context is FullWorthDbContext financeDb) pending.TryRemove(financeDb, out _);
    }
}

internal static class FinancialDataChangeDetector
{
    public static FinancialDataChangeSet Detect(FullWorthDbContext db)
    {
        var changes = new FinancialDataChangeSet();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        foreach (var entry in db.ChangeTracker.Entries<FinanceTransaction>())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted)) continue;
            var balanceChanged = entry.State is EntityState.Added or EntityState.Deleted || TransactionBalanceFieldsChanged(entry);
            var signalChanged = entry.State is EntityState.Added or EntityState.Deleted ||
                                balanceChanged || TransactionSignalFieldsChanged(entry);
            if (!balanceChanged && !signalChanged) continue;

            var currentDate = entry.Entity.BookingDate ?? entry.Entity.ValueDate;
            var originalDate = entry.State is EntityState.Modified or EntityState.Deleted
                ? entry.Property(x => x.BookingDate).OriginalValue ?? entry.Property(x => x.ValueDate).OriginalValue
                : null;
            var from = Min(currentDate, originalDate) ?? today;

            changes.MarkAccount(entry.Entity.AccountId, from, netWorth: balanceChanged, signals: signalChanged);
            if (entry.State is EntityState.Modified or EntityState.Deleted)
                changes.MarkAccount(entry.Property(x => x.AccountId).OriginalValue, from, netWorth: balanceChanged, signals: signalChanged);
        }

        foreach (var entry in db.ChangeTracker.Entries<BalanceSnapshot>())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted)) continue;
            if (entry.State == EntityState.Modified && !AnyModified(entry,
                    nameof(BalanceSnapshot.AccountId), nameof(BalanceSnapshot.Amount), nameof(BalanceSnapshot.Currency),
                    nameof(BalanceSnapshot.BalanceType), nameof(BalanceSnapshot.ReferenceDate), nameof(BalanceSnapshot.CapturedAt)))
                continue;

            // A pure balance refresh changes the current anchor, not historical cash-flow. Historical
            // transaction changes in the same sync carry their own earlier invalidation date.
            changes.MarkAccount(entry.Entity.AccountId, today);
            if (entry.State is EntityState.Modified or EntityState.Deleted)
                changes.MarkAccount(entry.Property(x => x.AccountId).OriginalValue, today);
        }

        foreach (var entry in db.ChangeTracker.Entries<FinanceAccount>())
        {
            if (entry.State == EntityState.Added)
            {
                changes.MarkSpace(entry.Entity.FullWorthSpaceId, today);
                continue;
            }
            if (entry.State == EntityState.Deleted)
            {
                changes.MarkSpace(entry.Entity.FullWorthSpaceId, fullHistory: true);
                continue;
            }
            if (entry.State == EntityState.Modified && AnyModified(entry,
                    nameof(FinanceAccount.FullWorthSpaceId), nameof(FinanceAccount.Currency),
                    nameof(FinanceAccount.IsActive), nameof(FinanceAccount.IncludeInNetWorth)))
            {
                changes.MarkSpace(entry.Entity.FullWorthSpaceId, fullHistory: true);
                changes.MarkSpace(entry.Property(x => x.FullWorthSpaceId).OriginalValue, fullHistory: true);
            }
        }

        foreach (var entry in db.ChangeTracker.Entries<AccountOwner>())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted)) continue;
            if (entry.State == EntityState.Modified && !AnyModified(entry,
                    nameof(AccountOwner.AccountId), nameof(AccountOwner.UserId), nameof(AccountOwner.OwnershipType)))
                continue;
            changes.MarkAccount(entry.Entity.AccountId, fullHistory: true);
            if (entry.State is EntityState.Modified or EntityState.Deleted)
                changes.MarkAccount(entry.Property(x => x.AccountId).OriginalValue, fullHistory: true);
        }

        foreach (var entry in db.ChangeTracker.Entries<FullWorthSpaceMember>())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted)) continue;
            changes.MarkSpace(entry.Entity.FullWorthSpaceId, fullHistory: true);
            if (entry.State is EntityState.Modified or EntityState.Deleted)
                changes.MarkSpace(entry.Property(x => x.FullWorthSpaceId).OriginalValue, fullHistory: true);
        }

        foreach (var entry in db.ChangeTracker.Entries<Asset>())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted)) continue;
            if (entry.State == EntityState.Modified && !AnyModified(entry,
                    nameof(Asset.FullWorthSpaceId), nameof(Asset.CurrentValue), nameof(Asset.Currency),
                    nameof(Asset.ValuedAt), nameof(Asset.IncludeInNetWorth)))
                continue;
            changes.MarkSpace(entry.Entity.FullWorthSpaceId, today);
            if (entry.State is EntityState.Modified or EntityState.Deleted)
                changes.MarkSpace(entry.Property(x => x.FullWorthSpaceId).OriginalValue, today);
        }

        foreach (var entry in db.ChangeTracker.Entries<Liability>())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted)) continue;
            if (entry.State == EntityState.Modified && !AnyModified(entry,
                    nameof(Liability.FullWorthSpaceId), nameof(Liability.CurrentBalance), nameof(Liability.Currency),
                    nameof(Liability.IncludeInNetWorth)))
                continue;
            changes.MarkSpace(entry.Entity.FullWorthSpaceId, today);
            if (entry.State is EntityState.Modified or EntityState.Deleted)
                changes.MarkSpace(entry.Property(x => x.FullWorthSpaceId).OriginalValue, today);
        }

        foreach (var entry in db.ChangeTracker.Entries<Budget>())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted)) continue;
            if (entry.State == EntityState.Modified && !AnyModified(entry,
                    nameof(Budget.FullWorthSpaceId), nameof(Budget.CategoryId), nameof(Budget.Amount),
                    nameof(Budget.Currency), nameof(Budget.Period), nameof(Budget.CarryOver),
                    nameof(Budget.CarryOverOverspend), nameof(Budget.IsActive), nameof(Budget.StartDate), nameof(Budget.EndDate)))
                continue;
            changes.MarkSpace(entry.Entity.FullWorthSpaceId, today, netWorth: false, signals: true);
            if (entry.State is EntityState.Modified or EntityState.Deleted)
                changes.MarkSpace(entry.Property(x => x.FullWorthSpaceId).OriginalValue, today, netWorth: false, signals: true);
        }

        foreach (var entry in db.ChangeTracker.Entries<RecurringContract>())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted)) continue;
            if (entry.State == EntityState.Modified && !AnyModified(entry,
                    nameof(RecurringContract.FullWorthSpaceId), nameof(RecurringContract.Name),
                    nameof(RecurringContract.ProviderName), nameof(RecurringContract.Kind),
                    nameof(RecurringContract.CategoryId), nameof(RecurringContract.AccountId),
                    nameof(RecurringContract.MergedIntoContractId), nameof(RecurringContract.Amount),
                    nameof(RecurringContract.Currency), nameof(RecurringContract.BillingCycle),
                    nameof(RecurringContract.Interval), nameof(RecurringContract.StartDate),
                    nameof(RecurringContract.EndDate), nameof(RecurringContract.NextDueDate),
                    nameof(RecurringContract.AutoDetected), nameof(RecurringContract.IsActive)))
                continue;
            changes.MarkSpace(entry.Entity.FullWorthSpaceId, today, netWorth: false, signals: true);
            if (entry.State is EntityState.Modified or EntityState.Deleted)
                changes.MarkSpace(entry.Property(x => x.FullWorthSpaceId).OriginalValue, today, netWorth: false, signals: true);
        }

        return changes;
    }

    private static bool TransactionBalanceFieldsChanged(EntityEntry<FinanceTransaction> entry) => AnyModified(entry,
        nameof(FinanceTransaction.AccountId), nameof(FinanceTransaction.Status), nameof(FinanceTransaction.BookingDate),
        nameof(FinanceTransaction.ValueDate), nameof(FinanceTransaction.Amount), nameof(FinanceTransaction.Currency));

    private static bool TransactionSignalFieldsChanged(EntityEntry<FinanceTransaction> entry) => AnyModified(entry,
        nameof(FinanceTransaction.CategoryId), nameof(FinanceTransaction.Counterparty),
        nameof(FinanceTransaction.NormalizedCounterparty), nameof(FinanceTransaction.Description),
        nameof(FinanceTransaction.MerchantCategoryCode), nameof(FinanceTransaction.IsIgnored),
        nameof(FinanceTransaction.IsTransfer), nameof(FinanceTransaction.CategorizationSource));

    private static bool AnyModified<TEntity>(EntityEntry<TEntity> entry, params string[] properties) where TEntity : class =>
        properties.Any(name => entry.Property(name).IsModified);

    private static DateOnly? Min(DateOnly? left, DateOnly? right)
    {
        if (!left.HasValue) return right;
        if (!right.HasValue) return left;
        return left.Value <= right.Value ? left : right;
    }
}

/// <summary>Runs the materialized-view refresh on a fresh DbContext after the source transaction committed.</summary>
public sealed class FinancialDataConsistencyCoordinator(
    IServiceScopeFactory scopes,
    ILogger<FinancialDataConsistencyCoordinator> logger)
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task ProcessAsync(FinancialDataChangeSet changes, CancellationToken ct)
    {
        if (!changes.HasChanges) return;
        await gate.WaitAsync(ct);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<FullWorthDbContext>();
            var spaces = changes.FullWorthSpaceIds.ToHashSet();
            if (changes.AccountIds.Count > 0)
            {
                var accountSpaces = await db.Accounts.AsNoTracking()
                    .Where(account => changes.AccountIds.Contains(account.Id))
                    .Select(account => account.FullWorthSpaceId)
                    .Distinct()
                    .ToListAsync(ct);
                spaces.UnionWith(accountSpaces);
            }

            if (spaces.Count == 0) return;

            if (changes.NetWorthAffected)
            {
                var snapshots = scope.ServiceProvider.GetRequiredService<NetWorthSnapshotService>();
                var from = changes.FullHistory ? (DateOnly?)null : changes.EarliestDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
                foreach (var fullWorthSpaceId in spaces)
                    await snapshots.RebuildHistoryForSpaceAsync(fullWorthSpaceId, from, ct);
            }

            if (changes.SignalsAffected)
            {
                var signalQueue = scope.ServiceProvider.GetRequiredService<FullWorth.Backend.Modules.Intelligence.Signals.FinancialSignalRefreshQueue>();
                var now = DateTimeOffset.UtcNow;
                foreach (var fullWorthSpaceId in spaces)
                    await signalQueue.EnqueueSpaceAsync(fullWorthSpaceId, now, "financial-data-commit", ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Source data has already committed at this point. Do not turn a successful bank import or
            // user edit into a false failure response; the startup repair pass will self-heal snapshots.
            logger.LogError(exception, "Derived financial data refresh failed after source data commit.");
        }
        finally
        {
            gate.Release();
        }
    }
}

public sealed class FinancialDataSaveChangesInterceptor(
    FinancialDataConsistencyState state,
    FinancialDataConsistencyCoordinator coordinator) : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (eventData.Context is FullWorthDbContext db) state.Capture(db);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is FullWorthDbContext db) state.Capture(db);
        return ValueTask.FromResult(result);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        if (eventData.Context?.Database.CurrentTransaction is null && state.TryTake(eventData.Context, out var changes))
            coordinator.ProcessAsync(changes, CancellationToken.None).GetAwaiter().GetResult();
        return result;
    }

    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context?.Database.CurrentTransaction is null && state.TryTake(eventData.Context, out var changes))
            await coordinator.ProcessAsync(changes, cancellationToken);
        return result;
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData) => state.Drop(eventData.Context);

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        state.Drop(eventData.Context);
        return Task.CompletedTask;
    }
}

public sealed class FinancialDataTransactionInterceptor(
    FinancialDataConsistencyState state,
    FinancialDataConsistencyCoordinator coordinator) : DbTransactionInterceptor
{
    public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData)
    {
        if (state.TryTake(eventData.Context, out var changes))
            coordinator.ProcessAsync(changes, CancellationToken.None).GetAwaiter().GetResult();
    }

    public override async Task TransactionCommittedAsync(
        DbTransaction transaction,
        TransactionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        if (state.TryTake(eventData.Context, out var changes))
            await coordinator.ProcessAsync(changes, cancellationToken);
    }

    public override void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData) => state.Drop(eventData.Context);

    public override Task TransactionRolledBackAsync(
        DbTransaction transaction,
        TransactionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        state.Drop(eventData.Context);
        return Task.CompletedTask;
    }

    public override void TransactionFailed(DbTransaction transaction, TransactionErrorEventData eventData) => state.Drop(eventData.Context);

    public override Task TransactionFailedAsync(
        DbTransaction transaction,
        TransactionErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        state.Drop(eventData.Context);
        return Task.CompletedTask;
    }
}
