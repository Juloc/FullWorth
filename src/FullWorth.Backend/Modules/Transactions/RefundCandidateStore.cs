using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Audit;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Transactions;

/// <summary>Ein Kauf, sofern er an einer Buchung haengt - mehr braucht die Erstattungssuche nicht.</summary>
public sealed record RefundPurchaseRef(Guid TransactionId, Guid Id, string? ExternalOrderId, string? Merchant);

/// <summary>
/// Die Rohdaten der Erstattungssuche: die Buchung selbst, die Ausgaben davor, die schon verworfenen
/// Vorschlaege und die Kaeufe dahinter.
///
/// Gerechnet wird hier nichts - welche Ausgabe wie gut zu einer Erstattung passt, entscheidet
/// <see cref="RefundCandidateScoring"/>.
/// </summary>
public sealed class RefundCandidateStore(FullWorthDbContext db, AuditService audit)
{
    public Task<FinanceTransaction?> FindVisibleAsync(
        Guid transactionId, IReadOnlySet<Guid> visibleAccountIds, CancellationToken ct) =>
        db.Transactions.AsNoTracking().SingleOrDefaultAsync(
            transaction => transaction.Id == transactionId && visibleAccountIds.Contains(transaction.AccountId), ct);

    /// <summary>Ausgaben im Suchfenster, in derselben Waehrung, ohne Umbuchungen und ohne Verworfene.</summary>
    public Task<List<FinanceTransaction>> ListCandidateExpensesAsync(
        IReadOnlySet<Guid> visibleAccountIds, string currency, DateOnly from, DateOnly to,
        IReadOnlySet<Guid> dismissed, CancellationToken ct) =>
        db.Transactions.AsNoTracking()
            .Where(transaction =>
                visibleAccountIds.Contains(transaction.AccountId)
                && transaction.Amount < 0
                && !transaction.IsTransfer
                && (transaction.BookingDate ?? transaction.ValueDate) >= from
                && (transaction.BookingDate ?? transaction.ValueDate) <= to
                && transaction.Currency == currency
                && !dismissed.Contains(transaction.Id))
            .ToListAsync(ct);

    public async Task<Dictionary<Guid, RefundPurchaseRef>> PurchasesByTransactionAsync(
        Guid fullWorthSpaceId, CancellationToken ct)
    {
        var rows = await db.Purchases.AsNoTracking()
            .Where(purchase => purchase.FullWorthSpaceId == fullWorthSpaceId && purchase.TransactionId != null)
            .Select(purchase => new RefundPurchaseRef(
                purchase.TransactionId!.Value, purchase.Id, purchase.ExternalOrderId, purchase.Merchant))
            .ToListAsync(ct);

        return rows.GroupBy(row => row.TransactionId).ToDictionary(group => group.Key, group => group.First());
    }

    public async Task<HashSet<Guid>> DismissedAsync(Guid fullWorthSpaceId, Guid refundId, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        await using var command = RawSql.Command(connection,
            "SELECT \"OriginalTransactionId\" FROM \"RefundSuggestionDismissals\" WHERE \"FullWorthSpaceId\"=@space AND \"RefundTransactionId\"=@refund",
            ("@space", fullWorthSpaceId), ("@refund", refundId));
        await using var reader = await command.ExecuteReaderAsync(ct);

        var dismissed = new HashSet<Guid>();
        while (await reader.ReadAsync(ct)) dismissed.Add(RawSql.Guid(reader, "OriginalTransactionId"));
        return dismissed;
    }

    public async Task DismissAsync(
        Guid userId, Guid fullWorthSpaceId, Guid refundId, Guid originalTransactionId, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        await using var command = RawSql.Command(connection,
            "INSERT INTO \"RefundSuggestionDismissals\" (\"FullWorthSpaceId\",\"RefundTransactionId\",\"OriginalTransactionId\",\"DismissedAt\") VALUES (@space,@refund,@original,@now) ON CONFLICT (\"RefundTransactionId\",\"OriginalTransactionId\") DO UPDATE SET \"DismissedAt\"=EXCLUDED.\"DismissedAt\"",
            ("@space", fullWorthSpaceId), ("@refund", refundId), ("@original", originalTransactionId),
            ("@now", DateTimeOffset.UtcNow));
        await command.ExecuteNonQueryAsync(ct);

        audit.Record(fullWorthSpaceId, userId, "refund.suggestion.dismissed", "FinanceTransaction", refundId);
        await db.SaveChangesAsync(ct);
    }
}
