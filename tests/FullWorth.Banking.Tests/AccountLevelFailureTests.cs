using System.Net;

namespace FullWorth.Banking.Tests;

/// <summary>
/// One account failing is not the connection failing. A provider error while reading one account used to
/// escape the account loop, so every account AFTER it was skipped entirely and kept showing its last known
/// balance — a single wallet the bank refuses silently froze all the others.
///
/// Connection-level problems still abort: continuing past an expired consent or a rate limit would hammer
/// the provider and every remaining account would fail the same way.
/// </summary>
public sealed class AccountLevelFailureTests
{
    [Fact]
    public async Task A_failing_account_does_not_stop_the_others()
    {
        using var environment = new TestBankingEnvironment();
        var backend = new FakeBackendHandler();
        backend.Connections.Add(TestBankingEnvironment.AuthorizedConnection(
            lastAttemptAt: DateTimeOffset.UtcNow.AddDays(-1)));
        var provider = TwoAccountProvider(firstBalancesStatus: HttpStatusCode.InternalServerError);
        var service = environment.CreateSyncService(provider, backend);

        var result = await service.SyncAllAsync(CancellationToken.None);

        // The second account still synced...
        var balance = Assert.Single(backend.Ingests.SelectMany(batch => batch.Balances));
        Assert.Equal("hash-2", balance.IdentificationHash);
        Assert.Equal(42m, balance.Amount);
        // ...and the failure is reported rather than hidden.
        Assert.Equal("ACCOUNT_SYNC_FAILED", backend.Upserts.Last().LastError);
        Assert.Equal(1, result.Failed);
    }

    // An expired consent affects every account, so it must still abort the run instead of walking the
    // whole list and failing once per account.
    [Fact]
    public async Task A_consent_problem_still_aborts_the_connection()
    {
        using var environment = new TestBankingEnvironment();
        var backend = new FakeBackendHandler();
        backend.Connections.Add(TestBankingEnvironment.AuthorizedConnection(
            lastAttemptAt: DateTimeOffset.UtcNow.AddDays(-1)));
        var provider = TwoAccountProvider(
            firstBalancesStatus: HttpStatusCode.Unauthorized,
            firstBalancesBody: "{\"error_code\":\"CONSENT_EXPIRED\"}");
        var service = environment.CreateSyncService(provider, backend);

        await service.SyncAllAsync(CancellationToken.None);

        // Nothing was ingested for the second account: the run stopped at the connection level.
        Assert.DoesNotContain(
            backend.Ingests.SelectMany(batch => batch.Balances),
            balance => balance.IdentificationHash == "hash-2");
    }

    [Fact]
    public async Task Both_accounts_sync_when_nothing_fails()
    {
        using var environment = new TestBankingEnvironment();
        var backend = new FakeBackendHandler();
        backend.Connections.Add(TestBankingEnvironment.AuthorizedConnection(
            lastAttemptAt: DateTimeOffset.UtcNow.AddDays(-1)));
        var service = environment.CreateSyncService(TwoAccountProvider(), backend);

        await service.SyncAllAsync(CancellationToken.None);

        Assert.Equal(2, backend.Ingests.SelectMany(batch => batch.Balances).Count());
        Assert.Null(backend.Upserts.Last().LastError);
    }

    private static RecordingHttpMessageHandler TwoAccountProvider(
        HttpStatusCode? firstBalancesStatus = null,
        string firstBalancesBody = "{\"error_code\":\"SERVER_ERROR\"}") =>
        new((request, _, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/sessions/session-1")
                return Task.FromResult(TestBankingEnvironment.JsonResponse(
                    "{\"status\":\"AUTHORIZED\",\"accounts\":[\"account-1\",\"account-2\"],\"accounts_data\":[" +
                    "{\"uid\":\"account-1\",\"identification_hash\":\"hash-1\",\"details\":\"One\",\"currency\":\"EUR\"}," +
                    "{\"uid\":\"account-2\",\"identification_hash\":\"hash-2\",\"details\":\"Two\",\"currency\":\"EUR\"}]}"));
            if (path.EndsWith("/details", StringComparison.Ordinal))
                return Task.FromResult(TestBankingEnvironment.JsonResponse("{\"detail\":\"not found\"}", HttpStatusCode.NotFound));
            if (path == "/accounts/account-1/balances")
                return Task.FromResult(firstBalancesStatus is { } status
                    ? TestBankingEnvironment.JsonResponse(firstBalancesBody, status)
                    : TestBankingEnvironment.JsonResponse(
                        "{\"balances\":[{\"balance_amount\":{\"amount\":7,\"currency\":\"EUR\"},\"balance_type\":\"CLBD\"}]}"));
            if (path == "/accounts/account-2/balances")
                return Task.FromResult(TestBankingEnvironment.JsonResponse(
                    "{\"balances\":[{\"balance_amount\":{\"amount\":42,\"currency\":\"EUR\"},\"balance_type\":\"CLBD\"}]}"));
            if (path.EndsWith("/transactions", StringComparison.Ordinal))
                return Task.FromResult(TestBankingEnvironment.JsonResponse("{\"transactions\":[]}"));
            throw new Xunit.Sdk.XunitException($"Unexpected provider request: {request.RequestUri}");
        });
}
