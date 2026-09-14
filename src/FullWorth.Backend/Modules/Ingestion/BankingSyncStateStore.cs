using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Ingestion;

/// <summary>Wie weit ist dieses Konto synchronisiert?</summary>
public sealed record AccountSyncState(Guid Id, string? IdentificationHash, DateOnly? LatestBookingDate, DateTimeOffset? LatestTransactionUpdatedAt);

/// <summary>Wo beim Anbieter eine Buchung herkommt.</summary>
public sealed record ProviderPointer(Guid ConnectionId, string? ProviderAccountId, string? ProviderTransactionId);

/// <summary>
/// Was der Banking-Dienst ueber die Loopback-Schnittstelle wissen muss: welches Konto zu einem
/// Identifikationshash gehoert, und wo eine Buchung beim Anbieter herkommt.
/// </summary>
public sealed class BankingSyncStateStore(FullWorthDbContext db)
{
    /// <summary>
    /// Nur der primaere <c>identification_hash</c> des Anbieters ist eine stabile Kontoidentitaet.
    /// Die unscharfen Aliase aus <c>identification_hashes</c> sind ausdruecklich nicht eindeutig -
    /// darum werden sie hier nicht mitgesucht.
    ///
    /// Mehrere Treffer sind ein echter Konflikt und keine Auswahlfrage; darum kommt die Liste
    /// zurueck und nicht der erste Treffer.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> AccountIdsWithHashAsync(
        Guid connectionId, string identificationHash, CancellationToken ct) =>
        await db.Accounts.AsNoTracking()
            .Where(account => account.BankConnectionId == connectionId
                           && account.IdentificationHash == identificationHash)
            .Select(account => account.Id)
            .Distinct()
            .ToListAsync(ct);

    public async Task<AccountSyncState?> ReadSyncStateAsync(Guid accountId, CancellationToken ct)
    {
        var hash = await db.Accounts.AsNoTracking()
            .Where(account => account.Id == accountId)
            .Select(account => account.IdentificationHash)
            .SingleOrDefaultAsync(ct);

        return new AccountSyncState(
            accountId,
            hash,
            await db.Transactions.AsNoTracking()
                .Where(transaction => transaction.AccountId == accountId
                                   && transaction.BookingDate != null
                                   && transaction.Status == "BOOK")
                .MaxAsync(transaction => (DateOnly?)transaction.BookingDate, ct),
            await db.Transactions.AsNoTracking()
                .Where(transaction => transaction.AccountId == accountId)
                .MaxAsync(transaction => (DateTimeOffset?)transaction.UpdatedAt, ct));
    }

    /// <summary>Nur fuer ein Konto, das diesem Benutzer gehoert und in diesem Space liegt.</summary>
    public Task<ProviderPointer?> FindProviderPointerAsync(
        Guid transactionId, Guid fullWorthSpaceId, Guid userId, CancellationToken ct) =>
        db.Transactions.AsNoTracking()
            .Where(transaction => transaction.Id == transactionId)
            .Join(db.Accounts.AsNoTracking(),
                transaction => transaction.AccountId,
                account => account.Id,
                (transaction, account) => new { transaction, account })
            .Where(row =>
                row.account.FullWorthSpaceId == fullWorthSpaceId &&
                row.account.BankConnectionId != null &&
                row.account.Owners.Any(owner => owner.UserId == userId) &&
                db.FullWorthSpaceMembers.Any(member =>
                    member.FullWorthSpaceId == fullWorthSpaceId && member.UserId == userId))
            .Select(row => new ProviderPointer(
                row.account.BankConnectionId!.Value,
                row.account.ProviderAccountId,
                row.transaction.ProviderTransactionId))
            .SingleOrDefaultAsync(ct);
}
