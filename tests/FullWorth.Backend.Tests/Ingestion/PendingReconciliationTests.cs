using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Ingestion;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FullWorth.Backend.Tests.Ingestion;

public sealed class PendingReconciliationTests
{
    [Fact]
    public async Task PendingTransactionIsReconciledToItsBookedFormWithoutDuplicating()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var service = new IngestionService(db);

        await service.IngestAsync(Batch("pending-key", "PDNG", -49.99m, new DateOnly(2026, 8, 10), "ACME STORE"), CancellationToken.None);
        // Books a day later under a different external key, same amount/currency/counterparty.
        await service.IngestAsync(Batch("booked-key", "BOOK", -49.99m, new DateOnly(2026, 8, 11), "ACME STORE"), CancellationToken.None);

        db.ChangeTracker.Clear();
        var transaction = Assert.Single(await db.Transactions.AsNoTracking().ToListAsync());
        Assert.Equal("BOOK", transaction.Status);
        Assert.Equal("booked-key", transaction.ExternalKey);
        Assert.Equal(new DateOnly(2026, 8, 11), transaction.BookingDate);
    }

    [Fact]
    public async Task BookedTransactionThatDoesNotMatchAPendingRowIsInsertedSeparately()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var service = new IngestionService(db);

        await service.IngestAsync(Batch("pending-key", "PDNG", -49.99m, new DateOnly(2026, 8, 10), "ACME STORE"), CancellationToken.None);
        // Different amount and counterparty -> not the same transaction -> must not reconcile.
        await service.IngestAsync(Batch("booked-key", "BOOK", -12.00m, new DateOnly(2026, 8, 11), "OTHER SHOP"), CancellationToken.None);

        db.ChangeTracker.Clear();
        Assert.Equal(2, await db.Transactions.CountAsync());
    }

    private static FinanceIngestBatch Batch(string externalKey, string status, decimal amount, DateOnly bookingDate, string counterparty) =>
        new(
            new BankConnectionBatch(null, "enable-banking", "Bank", "DE", "session-1", "AUTHORIZED", null, null, null, FullWorthSpaceDefaults.LegacyId),
            [new AccountBatchItem("acct-hash", "acct-1", "Bank", "Main", null, null, "EUR", null, true)],
            [],
            [new TransactionBatchItem("acct-hash", externalKey, externalKey, status, bookingDate, bookingDate, amount, "EUR", counterparty, null, null, null, "{}")]);
}
