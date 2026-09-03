using System.Net;
using System.Net.Http.Headers;
using FullWorth.Banking.Backend;
using FullWorth.Banking.Services;

namespace FullWorth.Banking.Tests;

public sealed class BankSyncFlowTests
{
    [Fact]
    public async Task Rate_limit_failure_applies_at_least_the_360_minute_local_cooldown()
    {
        using var environment = new TestBankingEnvironment();
        var backend = new FakeBackendHandler();
        backend.Connections.Add(TestBankingEnvironment.AuthorizedConnection(
            lastAttemptAt: DateTimeOffset.UtcNow.AddDays(-1)));
        var provider = new RecordingHttpMessageHandler((_, _, _) => Task.FromResult(
            TestBankingEnvironment.JsonResponse(
                "{\"error_code\":\"ASPSP_RATE_LIMIT_EXCEEDED\"}",
                HttpStatusCode.TooManyRequests)));
        var service = environment.CreateSyncService(provider, backend, new BankingSyncOptions
        {
            MinimumBackgroundSyncIntervalMinutes = 1,
            RateLimitCooldownMinutes = 1
        });
        var started = DateTimeOffset.UtcNow;

        var result = await service.SyncAllAsync(CancellationToken.None);

        Assert.Equal(1, result.Failed);
        Assert.Single(provider.Requests);
        var failure = backend.Upserts.Last();
        Assert.Equal("rate_limit", failure.LastError);
        Assert.NotNull(failure.NextSyncAllowedAt);
        Assert.True(failure.NextSyncAllowedAt >= started.AddMinutes(359));
    }

    [Fact]
    public async Task RetryAfter_later_than_local_cooldown_is_persisted()
    {
        using var environment = new TestBankingEnvironment();
        var backend = new FakeBackendHandler();
        backend.Connections.Add(TestBankingEnvironment.AuthorizedConnection(
            lastAttemptAt: DateTimeOffset.UtcNow.AddDays(-1)));
        var retryAt = DateTimeOffset.UtcNow.AddMinutes(500);
        var provider = new RecordingHttpMessageHandler((_, _, _) =>
        {
            var response = TestBankingEnvironment.JsonResponse(
                "{\"error_code\":\"ASPSP_RATE_LIMIT_EXCEEDED\"}",
                HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(retryAt);
            return Task.FromResult(response);
        });
        var service = environment.CreateSyncService(provider, backend, new BankingSyncOptions
        {
            MinimumBackgroundSyncIntervalMinutes = 365,
            RateLimitCooldownMinutes = 365
        });

        var result = await service.SyncAllAsync(CancellationToken.None);

        Assert.Equal(1, result.Failed);
        Assert.Single(provider.Requests);
        var failure = backend.Upserts.Last();
        Assert.NotNull(failure.NextSyncAllowedAt);
        Assert.True(failure.NextSyncAllowedAt >= retryAt.AddSeconds(-1));
    }

    [Fact]
    public async Task Simultaneous_sync_attempt_is_skipped_not_queued_into_a_second_bank_run()
    {
        using var environment = new TestBankingEnvironment();
        var backend = new FakeBackendHandler();
        backend.Connections.Add(TestBankingEnvironment.AuthorizedConnection(
            lastAttemptAt: DateTimeOffset.UtcNow.AddDays(-1)));
        var enteredProvider = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseProvider = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new RecordingHttpMessageHandler(async (request, _, ct) =>
        {
            Assert.Equal("/sessions/session-1", request.RequestUri!.AbsolutePath);
            enteredProvider.TrySetResult();
            await releaseProvider.Task.WaitAsync(ct);
            return TestBankingEnvironment.JsonResponse("{\"status\":\"AUTHORIZED\",\"accounts\":[]}");
        });
        var gate = new BankSyncConcurrencyGate();
        var firstService = environment.CreateSyncService(provider, backend, gate: gate);
        var secondService = environment.CreateSyncService(provider, backend, gate: gate);

        var first = firstService.SyncAllAsync(CancellationToken.None);
        await enteredProvider.Task;
        var second = await secondService.SyncAllAsync(CancellationToken.None);

        Assert.True(second.AlreadyRunning);
        Assert.Equal(0, second.Synced);
        Assert.Single(provider.Requests);

        releaseProvider.TrySetResult();
        var completedFirst = await first;
        Assert.Equal(1, completedFirst.Synced);
        Assert.Single(provider.Requests);
    }

    [Fact]
    public async Task Continuation_pages_keep_date_range_and_continue_after_an_empty_intermediate_page()
    {
        using var environment = new TestBankingEnvironment();
        var backend = new FakeBackendHandler
        {
            SyncState = new AccountSyncState(DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-3))
        };
        backend.Connections.Add(TestBankingEnvironment.AuthorizedConnection(
            lastAttemptAt: DateTimeOffset.UtcNow.AddDays(-1)));

        var transactionPage = 0;
        var provider = new RecordingHttpMessageHandler((request, _, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/sessions/session-1")
                return Task.FromResult(TestBankingEnvironment.JsonResponse(
                    "{\"status\":\"AUTHORIZED\",\"accounts\":[{\"uid\":\"account-1\",\"identification_hash\":\"hash-1\",\"name\":\"Konto 1\",\"currency\":\"EUR\"}]}"));
            if (path == "/accounts/account-1/balances")
                return Task.FromResult(TestBankingEnvironment.JsonResponse("{\"balances\":[]}"));
            if (path == "/accounts/account-1/transactions")
            {
                transactionPage++;
                return Task.FromResult(transactionPage == 1
                    ? TestBankingEnvironment.JsonResponse("{\"transactions\":[],\"continuation_key\":\"next-page\"}")
                    : TestBankingEnvironment.JsonResponse("{\"transactions\":[{\"transaction_id\":\"tx-1\",\"status\":\"BOOK\",\"booking_date\":\"2026-08-10\",\"credit_debit_indicator\":\"CRDT\",\"transaction_amount\":{\"amount\":12.34,\"currency\":\"EUR\"}}]}"));
            }
            throw new Xunit.Sdk.XunitException($"Unexpected provider request: {request.RequestUri}");
        });
        var service = environment.CreateSyncService(provider, backend, new BankingSyncOptions
        {
            MinimumBackgroundSyncIntervalMinutes = 360,
            OverlapDays = 7,
            MaxPagesPerAccount = 10
        });

        var result = await service.SyncAllAsync(CancellationToken.None);

        Assert.Equal(1, result.Synced);
        var transactionRequests = provider.Requests
            .Where(x => x.Uri.AbsolutePath == "/accounts/account-1/transactions")
            .ToList();
        Assert.Equal(2, transactionRequests.Count);

        var firstQuery = TestBankingEnvironment.Query(transactionRequests[0].Uri);
        var secondQuery = TestBankingEnvironment.Query(transactionRequests[1].Uri);
        Assert.Equal(firstQuery["date_from"], secondQuery["date_from"]);
        Assert.Equal(firstQuery["date_to"], secondQuery["date_to"]);
        Assert.Equal("default", firstQuery["strategy"]);
        Assert.Equal("default", secondQuery["strategy"]);
        Assert.False(firstQuery.ContainsKey("continuation_key"));
        Assert.Equal("next-page", secondQuery["continuation_key"]);
        Assert.Contains(backend.Ingests, batch => batch.Transactions.Any(x => x.ExternalKey == "tx-1"));
    }

    [Fact]
    public async Task Ongoing_sync_starts_from_latest_booking_date_minus_overlap_not_full_history()
    {
        using var environment = new TestBankingEnvironment();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var latest = today.AddDays(-2);
        var backend = new FakeBackendHandler { SyncState = new AccountSyncState(latest) };
        backend.Connections.Add(TestBankingEnvironment.AuthorizedConnection(
            lastAttemptAt: DateTimeOffset.UtcNow.AddDays(-1)));
        var provider = StandardAccountProvider(includeAccountDetails: false);
        var service = environment.CreateSyncService(provider, backend, new BankingSyncOptions
        {
            OverlapDays = 7,
            InitialHistoryDays = 180
        });

        var result = await service.SyncAllAsync(CancellationToken.None);

        Assert.Equal(1, result.Synced);
        Assert.DoesNotContain(provider.Requests, x => x.Uri.AbsolutePath == "/accounts/account-1");
        var transactionRequest = provider.Requests.Single(x => x.Uri.AbsolutePath == "/accounts/account-1/transactions");
        var query = TestBankingEnvironment.Query(transactionRequest.Uri);
        Assert.Equal(latest.AddDays(-7).ToString("yyyy-MM-dd"), query["date_from"]);
        Assert.Equal(today.ToString("yyyy-MM-dd"), query["date_to"]);
        Assert.Equal("default", query["strategy"]);
    }

    [Fact]
    public async Task First_import_uses_initial_history_and_longest_strategy()
    {
        using var environment = new TestBankingEnvironment();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var backend = new FakeBackendHandler { SyncState = null };
        backend.Connections.Add(TestBankingEnvironment.AuthorizedConnection(
            lastAttemptAt: DateTimeOffset.UtcNow.AddDays(-1)));
        var provider = StandardAccountProvider(includeAccountDetails: true);
        var service = environment.CreateSyncService(provider, backend, new BankingSyncOptions
        {
            InitialHistoryDays = 30,
            OverlapDays = 7
        });

        var result = await service.SyncAllAsync(CancellationToken.None);

        Assert.Equal(1, result.Synced);
        Assert.Contains(provider.Requests, x => x.Uri.AbsolutePath == "/accounts/account-1");
        var transactionRequest = provider.Requests.Single(x => x.Uri.AbsolutePath == "/accounts/account-1/transactions");
        var query = TestBankingEnvironment.Query(transactionRequest.Uri);
        Assert.Equal(today.AddDays(-30).ToString("yyyy-MM-dd"), query["date_from"]);
        Assert.Equal(today.ToString("yyyy-MM-dd"), query["date_to"]);
        Assert.Equal("longest", query["strategy"]);
    }

    private static RecordingHttpMessageHandler StandardAccountProvider(bool includeAccountDetails) => new((request, _, _) =>
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path == "/sessions/session-1")
            return Task.FromResult(TestBankingEnvironment.JsonResponse(
                "{\"status\":\"AUTHORIZED\",\"accounts\":[{\"uid\":\"account-1\",\"identification_hash\":\"hash-1\",\"name\":\"Konto 1\",\"currency\":\"EUR\"}]}"));
        if (path == "/accounts/account-1" && includeAccountDetails)
            return Task.FromResult(TestBankingEnvironment.JsonResponse(
                "{\"details\":\"Checking\",\"currency\":\"EUR\"}"));
        if (path == "/accounts/account-1/balances")
            return Task.FromResult(TestBankingEnvironment.JsonResponse("{\"balances\":[]}"));
        if (path == "/accounts/account-1/transactions")
            return Task.FromResult(TestBankingEnvironment.JsonResponse("{\"transactions\":[]}"));
        throw new Xunit.Sdk.XunitException($"Unexpected provider request: {request.RequestUri}");
    });
}
