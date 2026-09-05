using FullWorth.Banking.Backend;
using FullWorth.Banking.Services;

namespace FullWorth.Banking.Tests;

/// <summary>
/// Regressions for live failures around Enable Banking session shapes. Current API versions return
/// the session's "accounts" as uid STRINGS with the objects in "accounts_data" — and those objects
/// may carry ONLY uid + identification_hash. The sync must (a) never crash on any shape, (b) never
/// ingest placeholder metadata over real account names/currencies, and (c) never persist the
/// session-scoped uid as the account key (it changes with every session and would duplicate accounts).
/// </summary>
public sealed class EnableBankingSessionShapeTests
{
    [Fact]
    public async Task Sparse_accounts_data_syncs_with_details_fetched_from_account_resource()
    {
        using var environment = new TestBankingEnvironment();
        var backend = new FakeBackendHandler();
        backend.Connections.Add(TestBankingEnvironment.AuthorizedConnection(
            lastAttemptAt: DateTimeOffset.UtcNow.AddDays(-1)));
        var provider = new RecordingHttpMessageHandler((request, _, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/sessions/session-1")
                return Task.FromResult(TestBankingEnvironment.JsonResponse(
                    "{\"status\":\"AUTHORIZED\",\"accounts\":[\"account-1\"],\"accounts_data\":[{\"uid\":\"account-1\",\"identification_hash\":\"hash-1\"}]}"));
            if (path == "/accounts/account-1/details")
                return Task.FromResult(TestBankingEnvironment.JsonResponse("{\"name\":\"Max Mustermann\",\"details\":\"Girokonto\",\"currency\":\"EUR\"}"));
            if (path == "/accounts/account-1/balances")
                return Task.FromResult(TestBankingEnvironment.JsonResponse("{\"balances\":[]}"));
            if (path == "/accounts/account-1/transactions")
                return Task.FromResult(TestBankingEnvironment.JsonResponse("{\"transactions\":[]}"));
            throw new Xunit.Sdk.XunitException($"Unexpected provider request: {request.RequestUri}");
        });
        var service = environment.CreateSyncService(provider, backend);

        var result = await service.SyncAllAsync(CancellationToken.None);

        Assert.Equal(1, result.Synced);
        Assert.Equal(0, result.Failed);
        var account = Assert.Single(backend.Ingests.SelectMany(batch => batch.Accounts).DistinctBy(x => x.IdentificationHash));
        Assert.Equal("hash-1", account.IdentificationHash);
        Assert.Equal("account-1", account.ProviderAccountId);
        Assert.Equal("Girokonto", account.DisplayName);
    }

    [Fact]
    public async Task Sparse_accounts_data_on_subsequent_sync_does_not_overwrite_details_with_placeholders()
    {
        using var environment = new TestBankingEnvironment();
        var backend = new FakeBackendHandler
        {
            // A previous sync exists: without the details-less guard the sparse session entry would
            // be ingested as-is and clobber the stored name/currency with InstitutionName/"EUR".
            SyncState = new AccountSyncState(DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-3))
        };
        backend.Connections.Add(TestBankingEnvironment.AuthorizedConnection(
            lastAttemptAt: DateTimeOffset.UtcNow.AddDays(-1)));
        var provider = new RecordingHttpMessageHandler((request, _, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/sessions/session-1")
                return Task.FromResult(TestBankingEnvironment.JsonResponse(
                    "{\"status\":\"AUTHORIZED\",\"accounts\":[\"account-1\"],\"accounts_data\":[{\"uid\":\"account-1\",\"identification_hash\":\"hash-1\"}]}"));
            if (path == "/accounts/account-1/details")
                throw new Xunit.Sdk.XunitException("Ongoing sync must not refetch account details.");
            if (path == "/accounts/account-1/balances")
                return Task.FromResult(TestBankingEnvironment.JsonResponse("{\"balances\":[]}"));
            if (path == "/accounts/account-1/transactions")
                return Task.FromResult(TestBankingEnvironment.JsonResponse("{\"transactions\":[]}"));
            throw new Xunit.Sdk.XunitException($"Unexpected provider request: {request.RequestUri}");
        });
        var service = environment.CreateSyncService(provider, backend);

        var result = await service.SyncAllAsync(CancellationToken.None);

        Assert.Equal(1, result.Synced);
        var account = Assert.Single(backend.Ingests.SelectMany(batch => batch.Accounts).DistinctBy(x => x.IdentificationHash));
        Assert.Equal("Test Bank", account.DisplayName);
        Assert.Equal("EUR", account.Currency);
        Assert.Null(account.IbanLast4);
        Assert.False(account.HasDetails);
        Assert.DoesNotContain(provider.Requests, x => x.Uri.AbsolutePath == "/accounts/account-1/details");
    }

    [Fact]
    public async Task AccountHolderNameIsNotPersistedAsAccountDisplayName()
    {
        using var environment = new TestBankingEnvironment();
        var backend = new FakeBackendHandler();
        backend.Connections.Add(TestBankingEnvironment.AuthorizedConnection(
            lastAttemptAt: DateTimeOffset.UtcNow.AddDays(-1)));
        var provider = new RecordingHttpMessageHandler((request, _, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/sessions/session-1")
                return Task.FromResult(TestBankingEnvironment.JsonResponse(
                    "{\"status\":\"AUTHORIZED\",\"accounts_data\":[{\"uid\":\"account-1\",\"identification_hash\":\"hash-1\"}]}"));
            if (path == "/accounts/account-1/details")
                return Task.FromResult(TestBankingEnvironment.JsonResponse(
                    "{\"identification_hash\":\"hash-1\",\"name\":\"Max Mustermann\",\"product\":\"Privatkonto\",\"currency\":\"EUR\"}"));
            if (path == "/accounts/account-1/balances")
                return Task.FromResult(TestBankingEnvironment.JsonResponse("{\"balances\":[]}"));
            if (path == "/accounts/account-1/transactions")
                return Task.FromResult(TestBankingEnvironment.JsonResponse("{\"transactions\":[]}"));
            throw new Xunit.Sdk.XunitException($"Unexpected provider request: {request.RequestUri}");
        });
        var service = environment.CreateSyncService(provider, backend);

        await service.SyncAllAsync(CancellationToken.None);

        var account = Assert.Single(
            backend.Ingests.SelectMany(batch => batch.Accounts).DistinctBy(x => x.IdentificationHash));
        Assert.Equal("Privatkonto", account.DisplayName);
        Assert.DoesNotContain("Max Mustermann", account.DisplayName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Uid_only_session_resolves_the_real_identification_hash_before_ingesting()
    {
        using var environment = new TestBankingEnvironment();
        var backend = new FakeBackendHandler();
        backend.Connections.Add(TestBankingEnvironment.AuthorizedConnection(
            lastAttemptAt: DateTimeOffset.UtcNow.AddDays(-1)));
        var provider = new RecordingHttpMessageHandler((request, _, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/sessions/session-1")
                return Task.FromResult(TestBankingEnvironment.JsonResponse(
                    "{\"status\":\"AUTHORIZED\",\"accounts\":[\"account-1\"]}"));
            if (path == "/accounts/account-1/details")
                return Task.FromResult(TestBankingEnvironment.JsonResponse(
                    "{\"identification_hash\":\"hash-real\",\"name\":\"Max Mustermann\",\"details\":\"Tagesgeld\",\"currency\":\"EUR\",\"product\":\"Sparen\"}"));
            if (path == "/accounts/account-1/balances")
                return Task.FromResult(TestBankingEnvironment.JsonResponse("{\"balances\":[]}"));
            if (path == "/accounts/account-1/transactions")
                return Task.FromResult(TestBankingEnvironment.JsonResponse("{\"transactions\":[]}"));
            throw new Xunit.Sdk.XunitException($"Unexpected provider request: {request.RequestUri}");
        });
        var service = environment.CreateSyncService(provider, backend);

        var result = await service.SyncAllAsync(CancellationToken.None);

        Assert.Equal(1, result.Synced);
        var account = Assert.Single(backend.Ingests.SelectMany(batch => batch.Accounts).DistinctBy(x => x.IdentificationHash));
        // The session-scoped uid must NOT be the persisted key — a new session reassigns uids.
        Assert.Equal("hash-real", account.IdentificationHash);
        Assert.Equal("Tagesgeld", account.DisplayName);
    }

    [Fact]
    public async Task Uid_only_account_is_skipped_when_the_real_hash_cannot_be_resolved()
    {
        using var environment = new TestBankingEnvironment();
        var backend = new FakeBackendHandler();
        backend.Connections.Add(TestBankingEnvironment.AuthorizedConnection(
            lastAttemptAt: DateTimeOffset.UtcNow.AddDays(-1)));
        var provider = new RecordingHttpMessageHandler((request, _, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/sessions/session-1")
                return Task.FromResult(TestBankingEnvironment.JsonResponse(
                    "{\"status\":\"AUTHORIZED\",\"accounts\":[\"account-1\"]}"));
            if (path == "/accounts/account-1/details")
                return Task.FromResult(TestBankingEnvironment.JsonResponse("{\"name\":\"Ohne Hash\"}"));
            throw new Xunit.Sdk.XunitException($"Unexpected provider request: {request.RequestUri}");
        });
        var service = environment.CreateSyncService(provider, backend);

        var result = await service.SyncAllAsync(CancellationToken.None);

        Assert.Equal(0, result.Synced);
        Assert.Equal(1, result.Failed);
        Assert.Equal("ACCOUNT_RESOLUTION_FAILED", backend.Connections.Single().LastError);
        Assert.Empty(backend.Ingests.SelectMany(batch => batch.Accounts));
    }

    [Fact]
    public async Task Partially_parseable_accounts_data_recovers_remaining_accounts_from_the_uid_list()
    {
        using var environment = new TestBankingEnvironment();
        var backend = new FakeBackendHandler();
        backend.Connections.Add(TestBankingEnvironment.AuthorizedConnection(
            lastAttemptAt: DateTimeOffset.UtcNow.AddDays(-1)));
        var provider = new RecordingHttpMessageHandler((request, _, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/sessions/session-1")
                return Task.FromResult(TestBankingEnvironment.JsonResponse(
                    "{\"status\":\"AUTHORIZED\",\"accounts\":[\"account-1\",\"account-2\"],\"accounts_data\":[{\"uid\":\"account-1\",\"identification_hash\":\"hash-1\"},{\"uid\":\"account-2\"}]}"));
            if (path == "/accounts/account-1/details")
                return Task.FromResult(TestBankingEnvironment.JsonResponse("{\"details\":\"Giro\",\"currency\":\"EUR\"}"));
            if (path == "/accounts/account-2/details")
                return Task.FromResult(TestBankingEnvironment.JsonResponse("{\"identification_hash\":\"hash-2\",\"details\":\"Depot\",\"currency\":\"EUR\"}"));
            if (path.StartsWith("/accounts/") && path.EndsWith("/balances"))
                return Task.FromResult(TestBankingEnvironment.JsonResponse("{\"balances\":[]}"));
            if (path.StartsWith("/accounts/") && path.EndsWith("/transactions"))
                return Task.FromResult(TestBankingEnvironment.JsonResponse("{\"transactions\":[]}"));
            throw new Xunit.Sdk.XunitException($"Unexpected provider request: {request.RequestUri}");
        });
        var service = environment.CreateSyncService(provider, backend);

        var result = await service.SyncAllAsync(CancellationToken.None);

        Assert.Equal(1, result.Synced);
        var hashes = backend.Ingests.SelectMany(batch => batch.Accounts).Select(x => x.IdentificationHash).Distinct().OrderBy(x => x).ToList();
        Assert.Equal(["hash-1", "hash-2"], hashes);
    }

    [Fact]
    public async Task Missing_details_endpoint_does_not_fail_the_sync()
    {
        using var environment = new TestBankingEnvironment();
        var backend = new FakeBackendHandler();
        backend.Connections.Add(TestBankingEnvironment.AuthorizedConnection(
            lastAttemptAt: DateTimeOffset.UtcNow.AddDays(-1)));
        var provider = new RecordingHttpMessageHandler((request, _, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/sessions/session-1")
                return Task.FromResult(TestBankingEnvironment.JsonResponse(
                    "{\"status\":\"AUTHORIZED\",\"accounts\":[\"account-1\"],\"accounts_data\":[{\"uid\":\"account-1\",\"identification_hash\":\"hash-1\"}]}"));
            // The sandbox "Mock ASPSP" does not implement the account-details resource at all.
            if (path == "/accounts/account-1/details")
                return Task.FromResult(TestBankingEnvironment.JsonResponse("{\"detail\":\"not found\"}", System.Net.HttpStatusCode.NotFound));
            if (path == "/accounts/account-1/balances")
                return Task.FromResult(TestBankingEnvironment.JsonResponse("{\"balances\":[{\"balance_amount\":{\"amount\":12.5,\"currency\":\"EUR\"},\"balance_type\":\"CLBD\"}]}"));
            if (path == "/accounts/account-1/transactions")
                return Task.FromResult(TestBankingEnvironment.JsonResponse("{\"transactions\":[]}"));
            throw new Xunit.Sdk.XunitException($"Unexpected provider request: {request.RequestUri}");
        });
        var service = environment.CreateSyncService(provider, backend);

        var result = await service.SyncAllAsync(CancellationToken.None);

        Assert.Equal(1, result.Synced);
        Assert.Equal(0, result.Failed);
        var account = Assert.Single(backend.Ingests.SelectMany(batch => batch.Accounts).DistinctBy(x => x.IdentificationHash));
        Assert.Equal("hash-1", account.IdentificationHash);
        // Details are unavailable: the account falls back to the connection's institution name and
        // is flagged details-less, so the backend will not overwrite previously stored metadata.
        Assert.Equal("Test Bank", account.DisplayName);
        Assert.False(account.HasDetails);
        var balance = Assert.Single(backend.Ingests.SelectMany(batch => batch.Balances));
        Assert.Equal(12.5m, balance.Amount);
    }

    [Fact]
    public async Task Skipped_accounts_surface_as_a_connection_error_instead_of_a_healthy_sync()
    {
        using var environment = new TestBankingEnvironment();
        var backend = new FakeBackendHandler();
        backend.Connections.Add(TestBankingEnvironment.AuthorizedConnection(
            lastAttemptAt: DateTimeOffset.UtcNow.AddDays(-1)) with
        { ConsecutiveFailures = 2 });
        var provider = new RecordingHttpMessageHandler((request, _, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/sessions/session-1")
                return Task.FromResult(TestBankingEnvironment.JsonResponse(
                    "{\"status\":\"AUTHORIZED\",\"accounts\":[\"account-1\"]}"));
            // No accounts_data AND no details resource: the account cannot be keyed and is skipped.
            if (path == "/accounts/account-1/details")
                return Task.FromResult(TestBankingEnvironment.JsonResponse("{}", System.Net.HttpStatusCode.NotFound));
            throw new Xunit.Sdk.XunitException($"Unexpected provider request: {request.RequestUri}");
        });
        var service = environment.CreateSyncService(provider, backend);

        var result = await service.SyncAllAsync(CancellationToken.None);

        Assert.Equal(0, result.Synced);
        Assert.Equal(1, result.Failed);
        var final = backend.Upserts.Last();
        Assert.Equal("ACCOUNT_RESOLUTION_FAILED", final.LastError);
        Assert.Equal(3, final.ConsecutiveFailures);
        Assert.Empty(backend.Ingests);
    }

    [Fact]
    public async Task Connect_completion_survives_a_failing_initial_sync()
    {
        using var environment = new TestBankingEnvironment();
        var backend = new FakeBackendHandler();
        backend.Connections.Add(TestBankingEnvironment.AuthorizedConnection() with
        {
            AuthorizationState = "state-1",
            Status = "PENDING",
            ProviderSessionId = null
        });
        var provider = new RecordingHttpMessageHandler((request, _, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post && path == "/sessions")
                return Task.FromResult(TestBankingEnvironment.JsonResponse(
                    "{\"session_id\":\"session-9\",\"access\":{\"valid_until\":\"2026-12-01T12:00:00Z\"}}"));
            if (path == "/sessions/session-9")
                return Task.FromResult(TestBankingEnvironment.JsonResponse(
                    "{\"status\":\"AUTHORIZED\",\"accounts_data\":[{\"uid\":\"account-1\",\"identification_hash\":\"hash-1\"}]}"));
            // A hard provider failure mid-initial-sync (not a tolerated 404 on details).
            return Task.FromResult(TestBankingEnvironment.JsonResponse("{\"error\":\"boom\"}", System.Net.HttpStatusCode.Forbidden));
        });
        var service = environment.CreateSyncService(provider, backend);

        // The user-visible contract: authorization succeeded, so completing must NOT throw — the
        // connection stays AUTHORIZED and the background worker retries the sync later.
        var connection = await service.CompleteConnectionAsync("state-1", "auth-code", CancellationToken.None);

        Assert.Equal("AUTHORIZED", connection.Status);
        Assert.Contains(backend.Upserts, upsert => upsert.Status == "AUTHORIZED" && upsert.ProviderSessionId == "session-9");
    }

    [Fact]
    public async Task Malformed_session_entries_are_skipped_instead_of_crashing()
    {
        using var environment = new TestBankingEnvironment();
        var backend = new FakeBackendHandler();
        backend.Connections.Add(TestBankingEnvironment.AuthorizedConnection(
            lastAttemptAt: DateTimeOffset.UtcNow.AddDays(-1)));
        var provider = new RecordingHttpMessageHandler((request, _, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/sessions/session-1")
                return Task.FromResult(TestBankingEnvironment.JsonResponse(
                    "{\"status\":\"AUTHORIZED\",\"accounts\":[42,null,{\"uid\":\"no-hash\"},\"\"]}"));
            throw new Xunit.Sdk.XunitException($"Unexpected provider request: {request.RequestUri}");
        });
        var service = environment.CreateSyncService(provider, backend);

        var result = await service.SyncAllAsync(CancellationToken.None);

        Assert.Equal(1, result.Synced);
        Assert.Equal(0, result.Failed);
        Assert.Empty(backend.Ingests.SelectMany(batch => batch.Accounts));
    }
}
