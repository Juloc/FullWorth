using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Merchants;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Intelligence;

/// <summary>Ein Betrag in seiner Waehrung - mehr braucht ein Ausgabenvergleich nicht.</summary>
public sealed record MerchantAmount(decimal Amount, string Currency);

/// <summary>
/// Was dieser Haushalt bei einem Haendler ausgegeben hat.
///
/// Die Buchungen werden ueber die Konten des Space gefiltert und ueber den normalisierten
/// Gegenpartnamen dem Haendler zugeordnet - genau deshalb braucht es vorher alle Schreibweisen, unter
/// denen dieser Haendler auftritt.
/// </summary>
public sealed class MerchantSpendStore(FullWorthDbContext db)
{
    public Task<Merchant?> FindMerchantAsync(Guid fullWorthSpaceId, Guid merchantId, CancellationToken ct) =>
        db.Set<Merchant>().AsNoTracking()
            .SingleOrDefaultAsync(merchant => merchant.Id == merchantId
                                           && merchant.FullWorthSpaceId == fullWorthSpaceId, ct);

    /// <summary>Alle Schreibweisen dieses Haendlers - ohne sie findet die Zuordnung nichts.</summary>
    public Task<List<string>> AliasesAsync(Guid fullWorthSpaceId, Guid merchantId, CancellationToken ct) =>
        db.Set<MerchantAlias>().AsNoTracking()
            .Where(alias => alias.MerchantId == merchantId && alias.FullWorthSpaceId == fullWorthSpaceId)
            .Select(alias => alias.NormalizedAlias)
            .ToListAsync(ct);

    /// <summary>Ausgaben bei diesem Haendler im Zeitfenster. Ignorierte und Umbuchungen zaehlen nicht.</summary>
    public Task<List<MerchantAmount>> ExpensesAsync(
        Guid fullWorthSpaceId, IReadOnlySet<string> aliases, DateOnly from, DateOnly toExclusive,
        CancellationToken ct) =>
        SpaceTransactions(fullWorthSpaceId)
            .Where(transaction =>
                (transaction.BookingDate ?? transaction.ValueDate) >= from &&
                (transaction.BookingDate ?? transaction.ValueDate) < toExclusive &&
                transaction.Amount < 0m &&
                !transaction.IsIgnored &&
                !transaction.IsTransfer &&
                transaction.NormalizedCounterparty != null &&
                aliases.Contains(transaction.NormalizedCounterparty))
            .Select(transaction => new MerchantAmount(transaction.Amount, transaction.Currency))
            .ToListAsync(ct);

    /// <summary>
    /// Alle Ausgaben bei diesem Haendler, ohne Zeitgrenze - sie sind die Bezugspunkte, an denen eine
    /// Erstattung haengen kann. Eine Erstattung im Maerz kann einen Kauf vom Januar betreffen.
    /// </summary>
    public Task<List<Guid>> AllExpenseIdsAsync(
        Guid fullWorthSpaceId, IReadOnlySet<string> aliases, CancellationToken ct) =>
        SpaceTransactions(fullWorthSpaceId)
            .Where(transaction =>
                transaction.Amount < 0m &&
                transaction.NormalizedCounterparty != null &&
                aliases.Contains(transaction.NormalizedCounterparty))
            .Select(transaction => transaction.Id)
            .ToListAsync(ct);

    /// <summary>Erstattungen im Zeitfenster, die zu einer dieser Ausgaben gehoeren.</summary>
    public async Task<List<MerchantAmount>> RefundsAsync(
        Guid fullWorthSpaceId, IReadOnlyCollection<Guid> expenseIds, DateOnly from, DateOnly toExclusive,
        CancellationToken ct)
    {
        if (expenseIds.Count == 0) return [];

        return await SpaceTransactions(fullWorthSpaceId)
            .Where(transaction =>
                (transaction.BookingDate ?? transaction.ValueDate) >= from &&
                (transaction.BookingDate ?? transaction.ValueDate) < toExclusive &&
                transaction.Amount > 0m &&
                transaction.RefundOfTransactionId != null &&
                expenseIds.Contains(transaction.RefundOfTransactionId.Value) &&
                !transaction.IsIgnored &&
                !transaction.IsTransfer)
            .Select(transaction => new MerchantAmount(transaction.Amount, transaction.Currency))
            .ToListAsync(ct);
    }

    /// <summary>Nur Buchungen auf Konten dieses Space - die Grenze, die in allen drei Abfragen gilt.</summary>
    private IQueryable<Transactions.FinanceTransaction> SpaceTransactions(Guid fullWorthSpaceId) =>
        db.Transactions.AsNoTracking()
            .Join(db.Accounts.AsNoTracking().Where(account => account.FullWorthSpaceId == fullWorthSpaceId),
                transaction => transaction.AccountId,
                account => account.Id,
                (transaction, _) => transaction);
}
