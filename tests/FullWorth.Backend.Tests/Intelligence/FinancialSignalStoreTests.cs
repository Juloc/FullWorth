using FullWorth.Backend.Modules.Intelligence;
using FullWorth.Backend.Modules.Intelligence.Signals;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Intelligence;

public sealed class FinancialSignalStoreTests
{
    [Fact]
    public async Task Same_semantic_signal_updates_rank_without_creating_new_version()
    {
        await using var harness = await Harness.CreateAsync();
        var store = new FinancialSignalStore(harness.Db);
        var detected = Signal(rank: 10m);

        var first = await store.UpsertAsync(detected, CancellationToken.None);
        var second = await store.UpsertAsync(detected with { RankScore = 25m }, CancellationToken.None);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(1, second.Version);
        Assert.Equal(25m, second.RankScore);
        Assert.Equal(1, await harness.Db.FinancialSignals.CountAsync());
    }

    [Fact]
    public async Task Meaningful_change_reopens_dismissed_signal_and_increments_version()
    {
        await using var harness = await Harness.CreateAsync();
        var store = new FinancialSignalStore(harness.Db);
        var detected = Signal(rank: 10m);

        var first = await store.UpsertAsync(detected, CancellationToken.None);
        Assert.True(await store.DismissAsync(
            detected.UserId,
            detected.FullWorthSpaceId,
            first.Id,
            CancellationToken.None));

        var dismissed = await store.GetAsync(
            detected.UserId,
            detected.FullWorthSpaceId,
            first.Id,
            DateTimeOffset.UtcNow,
            CancellationToken.None);
        Assert.Equal(FinancialSignalStates.Dismissed, dismissed!.State);

        var updated = await store.UpsertAsync(
            detected with
            {
                ImpactAmount = 42m,
                PayloadJson = "{\"delta\":42}",
                EvidenceJson = "{\"baseline\":100,\"current\":142}"
            },
            CancellationToken.None);

        Assert.Equal(2, updated.Version);
        var reopened = await store.GetAsync(
            detected.UserId,
            detected.FullWorthSpaceId,
            first.Id,
            DateTimeOffset.UtcNow,
            CancellationToken.None);
        Assert.Equal(FinancialSignalStates.Unread, reopened!.State);
        Assert.Null(reopened.SnoozedUntil);
    }

    [Fact]
    public async Task Resolve_is_idempotent()
    {
        await using var harness = await Harness.CreateAsync();
        var store = new FinancialSignalStore(harness.Db);
        var detected = Signal(rank: 10m);
        await store.UpsertAsync(detected, CancellationToken.None);
        var resolvedAt = DateTimeOffset.UtcNow;

        Assert.True(await store.ResolveAsync(
            detected.UserId,
            detected.FullWorthSpaceId,
            detected.SemanticKey,
            resolvedAt,
            CancellationToken.None));
        Assert.True(await store.ResolveAsync(
            detected.UserId,
            detected.FullWorthSpaceId,
            detected.SemanticKey,
            resolvedAt.AddMinutes(1),
            CancellationToken.None));

        var row = await harness.Db.FinancialSignals.SingleAsync();
        Assert.Equal(resolvedAt, row.ResolvedAt);
    }

    [Fact]
    public async Task Store_rejects_invalid_json_and_confidence()
    {
        await using var harness = await Harness.CreateAsync();
        var store = new FinancialSignalStore(harness.Db);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.UpsertAsync(Signal(10m) with { PayloadJson = "not-json" }, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            store.UpsertAsync(Signal(10m) with { Confidence = 1.1m }, CancellationToken.None));
    }

    private static DetectedFinancialSignal Signal(decimal rank)
    {
        var now = new DateTimeOffset(2026, 9, 7, 20, 0, 0, TimeSpan.Zero);
        return new DetectedFinancialSignal(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "spending-shift",
            "merchant",
            "REWE",
            "merchant-spike:REWE:2026-09",
            "deterministic",
            FinancialSignalSeverities.Attention,
            .9m,
            25m,
            "EUR",
            "insights.spendingShift",
            "{\"delta\":25}",
            "{\"baseline\":100,\"current\":125}",
            rank,
            now,
            now.AddDays(7));
    }

    private sealed class Harness : IAsyncDisposable
    {
        private Harness(SqliteConnection connection, IntelligenceDbContext db)
        {
            Connection = connection;
            Db = db;
        }

        private SqliteConnection Connection { get; }
        public IntelligenceDbContext Db { get; }

        public static async Task<Harness> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<IntelligenceDbContext>()
                .UseSqlite(connection)
                .Options;
            var db = new IntelligenceDbContext(options);
            await db.Database.EnsureCreatedAsync();
            return new Harness(connection, db);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }
}
