using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Ingestion;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FullWorth.Backend.Tests.Ingestion;

public sealed class PendingReconciliationTests
{
    [Fact]
    public async Task SameEntryReferenceReconcilesHistoricalTransactionIdKeyWithoutDuplicating()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var service = new IngestionService(db);

        // Simulates a row imported by an older FullWorth build whose ExternalKey was transaction_id.
        await service.IngestAsync(
            Batch("old-provider-id", "PDNG", -49.99m, new DateOnly(2026, 8, 10), "ACME STORE", "stable-entry-1"),
            CancellationToken.None);
        await service.IngestAsync(
            Batch("er:stable-entry-1", "BOOK", -49.99m, new DateOnly(2026, 8, 11), "ACME STORE", "stable-entry-1"),
            CancellationToken.None);

        db.ChangeTracker.Clear();
        var transaction = Assert.Single(await db.Transactions.AsNoTracking().ToListAsync());
        Assert.Equal("BOOK", transaction.Status);
        Assert.Equal("er:stable-entry-1", transaction.ExternalKey);
        Assert.Equal("stable-entry-1", transaction.EntryReference);
    }

    [Fact]
    public async Task SimilarPendingAndBookedRowsWithoutStableEntryReferenceRemainSeparate()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var service = new IngestionService(db);

        await service.IngestAsync(
            Batch("fp:pending", "PDNG", -49.99m, new DateOnly(2026, 8, 10), "ACME STORE", null),
            CancellationToken.None);
        await service.IngestAsync(
            Batch("fp:booked", "BOOK", -49.99m, new DateOnly(2026, 8, 11), "ACME STORE", null),
            CancellationToken.None);

        db.ChangeTracker.Clear();
        Assert.Equal(2, await db.Transactions.CountAsync());
    }

    [Fact]
    public async Task AmbiguousDuplicateEntryReferenceIsNeverAutoMerged()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var service = new IngestionService(db);

        await service.IngestAsync(
            Batch("legacy-a", "PDNG", -10m, new DateOnly(2026, 8, 10), "SHOP", "ambiguous"),
            CancellationToken.None);
        await service.IngestAsync(
            Batch("legacy-b", "PDNG", -11m, new DateOnly(2026, 8, 10), "SHOP", "ambiguous"),
            CancellationToken.None);
        await service.IngestAsync(
            Batch("er:ambiguous", "BOOK", -10m, new DateOnly(2026, 8, 11), "SHOP", "ambiguous"),
            CancellationToken.None);

        db.ChangeTracker.Clear();
        Assert.Equal(3, await db.Transactions.CountAsync());
    }

    private static FinanceIngestBatch Batch(
        string externalKey,
        string status,
        decimal amount,
        DateOnly bookingDate,
        string counterparty,
        string? entryReference) =>
        new(
            new BankConnectionBatch(null, "enable-banking", "Bank", "DE", "session-1", "AUTHORIZED", null, null, null, FullWorthSpaceDefaults.LegacyId),
            [new AccountBatchItem("acct-hash", "acct-1", "Bank", "Main", null, null, "EUR", null, true)],
            [],
            [new TransactionBatchItem(
                "acct-hash",
                externalKey,
                "provider-pointer",
                status,
                bookingDate,
                bookingDate,
                amount,
                "EUR",
                counterparty,
                null,
                null,
                entryReference,
                "{}")]);
}
