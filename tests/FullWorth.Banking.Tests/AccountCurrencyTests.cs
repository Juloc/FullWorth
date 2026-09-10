using System.Net;

namespace FullWorth.Banking.Tests;

/// <summary>
/// A provider that does not state an account currency has not stated one. Reading that silence as EUR
/// labelled real accounts with a currency nobody reported, and every amount on them was then converted
/// with the wrong rate. The currency the money actually arrived in is the answer.
/// </summary>
public sealed class AccountCurrencyTests
{
    [Fact]
    public async Task An_account_without_a_stated_currency_takes_it_from_the_balance_that_arrived()
    {
        using var environment = new TestBankingEnvironment();
        var backend = new FakeBackendHandler();
        backend.Connections.Add(TestBankingEnvironment.AuthorizedConnection(
            lastAttemptAt: DateTimeOffset.UtcNow.AddDays(-1)));
        var provider = Provider(
            accountDetails: "{\"uid\":\"account-1\",\"identification_hash\":\"hash-1\",\"details\":\"Wise USD\"}",
            balances: "{\"balances\":[{\"balance_amount\":{\"amount\":120.5,\"currency\":\"USD\"},\"balance_type\":\"CLBD\"}]}");
        var service = environment.CreateSyncService(provider, backend);

        await service.SyncAllAsync(CancellationToken.None);

        var account = Assert.Single(backend.Ingests.SelectMany(batch => batch.Accounts).DistinctBy(x => x.IdentificationHash));
        Assert.Equal("USD", account.Currency);
        var balance = Assert.Single(backend.Ingests.SelectMany(batch => batch.Balances));
        Assert.Equal("USD", balance.Currency);
    }

    [Fact]
    public async Task A_stated_currency_still_wins_over_the_balance()
    {
        using var environment = new TestBankingEnvironment();
        var backend = new FakeBackendHandler();
        backend.Connections.Add(TestBankingEnvironment.AuthorizedConnection(
            lastAttemptAt: DateTimeOffset.UtcNow.AddDays(-1)));
        // A settlement account held in EUR that reports a USD balance: the account is still EUR.
        var provider = Provider(
            accountDetails: "{\"uid\":\"account-1\",\"identification_hash\":\"hash-1\",\"details\":\"Giro\",\"currency\":\"EUR\"}",
            balances: "{\"balances\":[{\"balance_amount\":{\"amount\":120.5,\"currency\":\"USD\"},\"balance_type\":\"CLBD\"}]}");
        var service = environment.CreateSyncService(provider, backend);

        await service.SyncAllAsync(CancellationToken.None);

        var account = Assert.Single(backend.Ingests.SelectMany(batch => batch.Accounts).DistinctBy(x => x.IdentificationHash));
        Assert.Equal("EUR", account.Currency);
    }

    // Nothing to go on anywhere: the account still has to be storable, so EUR remains the last resort -
    // but it is only reached here, and it is logged.
    [Fact]
    public async Task With_no_currency_anywhere_the_account_still_syncs()
    {
        using var environment = new TestBankingEnvironment();
        var backend = new FakeBackendHandler();
        backend.Connections.Add(TestBankingEnvironment.AuthorizedConnection(
            lastAttemptAt: DateTimeOffset.UtcNow.AddDays(-1)));
        var provider = Provider(
            accountDetails: "{\"uid\":\"account-1\",\"identification_hash\":\"hash-1\",\"details\":\"Mystery\"}",
            balances: "{\"balances\":[]}");
        var service = environment.CreateSyncService(provider, backend);

        await service.SyncAllAsync(CancellationToken.None);

        var account = Assert.Single(backend.Ingests.SelectMany(batch => batch.Accounts).DistinctBy(x => x.IdentificationHash));
        Assert.Equal("EUR", account.Currency);
        Assert.Empty(backend.Ingests.SelectMany(batch => batch.Balances));
    }

    private static RecordingHttpMessageHandler Provider(string accountDetails, string balances) =>
        new((request, _, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/sessions/session-1")
                return Task.FromResult(TestBankingEnvironment.JsonResponse(
                    "{\"status\":\"AUTHORIZED\",\"accounts\":[\"account-1\"],\"accounts_data\":[" + accountDetails + "]}"));
            if (path == "/accounts/account-1/details")
                return Task.FromResult(TestBankingEnvironment.JsonResponse("{\"detail\":\"not found\"}", HttpStatusCode.NotFound));
            if (path == "/accounts/account-1/balances")
                return Task.FromResult(TestBankingEnvironment.JsonResponse(balances));
            if (path == "/accounts/account-1/transactions")
                return Task.FromResult(TestBankingEnvironment.JsonResponse("{\"transactions\":[]}"));
            throw new Xunit.Sdk.XunitException($"Unexpected provider request: {request.RequestUri}");
        });
}
