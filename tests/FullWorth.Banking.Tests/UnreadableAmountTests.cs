using System.Net;

namespace FullWorth.Banking.Tests;

/// <summary>
/// Absent is not zero. The provider JSON reader returned <c>0m</c> for a missing or unparseable amount, and
/// that 0 was persisted as a genuine balance — indistinguishable from a real zero, so an account the bank
/// never reported a number for looked empty, and the sync looked successful.
/// </summary>
public sealed class UnreadableAmountTests
{
    [Fact]
    public async Task A_balance_without_a_readable_amount_is_not_stored_as_zero()
    {
        using var environment = new TestBankingEnvironment();
        var backend = new FakeBackendHandler();
        backend.Connections.Add(TestBankingEnvironment.AuthorizedConnection(
            lastAttemptAt: DateTimeOffset.UtcNow.AddDays(-1)));
        var provider = Provider(
            balances: "{\"balances\":[{\"balance_amount\":{\"currency\":\"EUR\"},\"balance_type\":\"CLBD\"}]}",
            transactions: "{\"transactions\":[]}");
        var service = environment.CreateSyncService(provider, backend);

        var result = await service.SyncAllAsync(CancellationToken.None);

        Assert.Empty(backend.Ingests.SelectMany(batch => batch.Balances));
        // ...and it must not look like a clean sync that simply found no money. A partial outcome is
        // reported the same way as every other one: an error code on the connection, so its health leaves
        // the authorized state and the UI shows it.
        var upsert = backend.Upserts.Last();
        Assert.Equal("BALANCE_UNREADABLE", upsert.LastError);
        Assert.Equal(0, result.Synced);
        Assert.Equal(1, result.Failed);
    }

    [Fact]
    public async Task An_unparseable_balance_amount_is_treated_the_same_way()
    {
        using var environment = new TestBankingEnvironment();
        var backend = new FakeBackendHandler();
        backend.Connections.Add(TestBankingEnvironment.AuthorizedConnection(
            lastAttemptAt: DateTimeOffset.UtcNow.AddDays(-1)));
        var provider = Provider(
            balances: "{\"balances\":[{\"balance_amount\":{\"amount\":\"n/a\",\"currency\":\"EUR\"},\"balance_type\":\"CLBD\"}]}",
            transactions: "{\"transactions\":[]}");
        var service = environment.CreateSyncService(provider, backend);

        await service.SyncAllAsync(CancellationToken.None);

        Assert.Empty(backend.Ingests.SelectMany(batch => batch.Balances));
        Assert.Equal("BALANCE_UNREADABLE", backend.Upserts.Last().LastError);
    }

    [Fact]
    public async Task A_readable_balance_still_syncs_clean()
    {
        using var environment = new TestBankingEnvironment();
        var backend = new FakeBackendHandler();
        backend.Connections.Add(TestBankingEnvironment.AuthorizedConnection(
            lastAttemptAt: DateTimeOffset.UtcNow.AddDays(-1)));
        var provider = Provider(
            balances: "{\"balances\":[{\"balance_amount\":{\"amount\":12.5,\"currency\":\"EUR\"},\"balance_type\":\"CLBD\"}]}",
            transactions: "{\"transactions\":[]}");
        var service = environment.CreateSyncService(provider, backend);

        await service.SyncAllAsync(CancellationToken.None);

        var balance = Assert.Single(backend.Ingests.SelectMany(batch => batch.Balances));
        Assert.Equal(12.5m, balance.Amount);
        Assert.Null(backend.Upserts.Last().LastError);
    }

    // The same reader fed transactions. A booking whose amount could not be read became a 0 booking in the
    // ledger - and its fingerprint key made that 0 permanent, so the real value could never replace it.
    [Fact]
    public async Task A_transaction_without_a_readable_amount_is_skipped_instead_of_booked_as_zero()
    {
        using var environment = new TestBankingEnvironment();
        var backend = new FakeBackendHandler();
        backend.Connections.Add(TestBankingEnvironment.AuthorizedConnection(
            lastAttemptAt: DateTimeOffset.UtcNow.AddDays(-1)));
        var provider = Provider(
            balances: "{\"balances\":[{\"balance_amount\":{\"amount\":12.5,\"currency\":\"EUR\"},\"balance_type\":\"CLBD\"}]}",
            transactions: "{\"transactions\":[" +
                          "{\"entry_reference\":\"tx-broken\",\"transaction_amount\":{\"currency\":\"EUR\"},\"booking_date\":\"2026-09-01\"}," +
                          "{\"entry_reference\":\"tx-ok\",\"transaction_amount\":{\"amount\":-9.99,\"currency\":\"EUR\"},\"booking_date\":\"2026-09-01\"}]}");
        var service = environment.CreateSyncService(provider, backend);

        await service.SyncAllAsync(CancellationToken.None);

        var transaction = Assert.Single(backend.Ingests.SelectMany(batch => batch.Transactions));
        Assert.Equal(-9.99m, transaction.Amount);
    }

    private static RecordingHttpMessageHandler Provider(string balances, string transactions) =>
        new((request, _, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/sessions/session-1")
                return Task.FromResult(TestBankingEnvironment.JsonResponse(
                    "{\"status\":\"AUTHORIZED\",\"accounts\":[\"account-1\"]," +
                    "\"accounts_data\":[{\"uid\":\"account-1\",\"identification_hash\":\"hash-1\"}]}"));
            if (path == "/accounts/account-1/details")
                return Task.FromResult(TestBankingEnvironment.JsonResponse("{\"detail\":\"not found\"}", HttpStatusCode.NotFound));
            if (path == "/accounts/account-1/balances")
                return Task.FromResult(TestBankingEnvironment.JsonResponse(balances));
            if (path == "/accounts/account-1/transactions")
                return Task.FromResult(TestBankingEnvironment.JsonResponse(transactions));
            throw new Xunit.Sdk.XunitException($"Unexpected provider request: {request.RequestUri}");
        });
}
