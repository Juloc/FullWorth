using System.Net;
using FullWorth.Banking.Backend;
using FullWorth.Banking.Services;

namespace FullWorth.Banking.Tests;

/// <summary>
/// P0.2: connect and manual sync must be bound to the authenticated user + a server-verified space.
/// The Banking service asks the backend (the authority) for owner authorization, derives the redirect
/// URL from server config (never the browser), and mints a random, expiring, one-time state bound to
/// user + space + connection.
/// </summary>
public sealed class BankConnectTenancyTests
{
    private static readonly BankingCaller Owner = new(Guid.NewGuid(), Guid.NewGuid());

    private static RecordingHttpMessageHandler InstitutionsThenAuth() => new((request, _, _) =>
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path == "/aspsps")
            return Task.FromResult(TestBankingEnvironment.JsonResponse(
                "{\"aspsps\":[{\"name\":\"Test Bank\",\"country\":\"DE\",\"maximum_consent_validity\":7776000}]}"));
        if (path == "/auth")
            return Task.FromResult(TestBankingEnvironment.JsonResponse(
                "{\"url\":\"https://bank.example/authorize?x=1\",\"authorization_id\":\"auth-1\"}"));
        throw new Xunit.Sdk.XunitException($"Unexpected provider request: {request.RequestUri}");
    });

    private static ConnectBankRequest Request() => new("Test Bank", "DE", 180, null, null, null);

    [Fact]
    public async Task Connect_persists_a_random_expiring_state_bound_to_user_and_space()
    {
        using var environment = new TestBankingEnvironment();
        var backend = new FakeBackendHandler(); // default authorize = 204 (owner)
        var service = environment.CreateSyncService(InstitutionsThenAuth(), backend);

        var url = await service.StartConnectionAsync(Request(), Owner, CancellationToken.None);

        Assert.Equal("https://bank.example/authorize?x=1", url);
        Assert.Equal(1, backend.AuthorizeCalls);
        var write = Assert.Single(backend.Upserts);
        Assert.Equal(Owner.FullWorthSpaceId, write.FullWorthSpaceId);
        Assert.Equal(Owner.UserId, write.AuthorizationUserId);
        Assert.NotNull(write.AuthorizationStateExpiresAt);
        Assert.True(write.AuthorizationStateExpiresAt > DateTimeOffset.UtcNow);
        // Random 256-bit state, not a guessable Guid.
        Assert.NotNull(write.AuthorizationState);
        Assert.Equal(64, write.AuthorizationState!.Length);
        Assert.DoesNotContain('-', write.AuthorizationState);
    }

    // §17: reconnecting an expired/errored connection must re-authorize IN PLACE, not create a
    // duplicate row for the same institution.
    [Fact]
    public async Task Reconnect_updates_the_existing_connection_instead_of_creating_a_duplicate()
    {
        using var environment = new TestBankingEnvironment();
        var backend = new FakeBackendHandler();
        var service = environment.CreateSyncService(InstitutionsThenAuth(), backend);
        var existingId = Guid.NewGuid();

        var url = await service.StartConnectionAsync(new("Test Bank", "DE", 180, null, null, null, existingId), Owner, CancellationToken.None);

        Assert.Equal("https://bank.example/authorize?x=1", url);
        var write = Assert.Single(backend.Upserts);
        Assert.Equal(existingId, write.Id);
    }

    [Fact]
    public async Task Connect_uses_the_server_redirect_url_not_any_browser_value()
    {
        using var environment = new TestBankingEnvironment();
        var backend = new FakeBackendHandler();
        string? sentRedirect = null;
        var provider = new RecordingHttpMessageHandler(async (request, _, ct) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/aspsps")
                return TestBankingEnvironment.JsonResponse("{\"aspsps\":[{\"name\":\"Test Bank\",\"country\":\"DE\"}]}");
            if (path == "/auth")
            {
                var body = await request.Content!.ReadAsStringAsync(ct);
                using var doc = System.Text.Json.JsonDocument.Parse(body);
                sentRedirect = doc.RootElement.GetProperty("redirect_url").GetString();
                return TestBankingEnvironment.JsonResponse("{\"url\":\"https://bank.example/a\",\"authorization_id\":\"auth-1\"}");
            }
            throw new Xunit.Sdk.XunitException($"Unexpected provider request: {request.RequestUri}");
        });
        var service = environment.CreateSyncService(provider, backend);

        await service.StartConnectionAsync(Request(), Owner, CancellationToken.None);

        Assert.Equal("https://finance.test/connect/enable-banking/callback", sentRedirect);
    }

    [Fact]
    public async Task Connect_is_forbidden_for_a_member_who_is_not_an_owner()
    {
        using var environment = new TestBankingEnvironment();
        var backend = new FakeBackendHandler { AuthorizeResponse = HttpStatusCode.Forbidden };
        var service = environment.CreateSyncService(InstitutionsThenAuth(), backend);

        var exception = await Assert.ThrowsAsync<BankAccessException>(() =>
            service.StartConnectionAsync(Request(), Owner, CancellationToken.None));
        Assert.True(exception.Forbidden);
        Assert.Empty(backend.Upserts); // never reached the provider/persist
    }

    [Fact]
    public async Task Connect_is_not_found_for_a_foreign_or_unknown_space()
    {
        using var environment = new TestBankingEnvironment();
        var backend = new FakeBackendHandler { AuthorizeResponse = HttpStatusCode.NotFound };
        var service = environment.CreateSyncService(InstitutionsThenAuth(), backend);

        var exception = await Assert.ThrowsAsync<BankAccessException>(() =>
            service.StartConnectionAsync(Request(), Owner, CancellationToken.None));
        Assert.False(exception.Forbidden);
    }

    [Fact]
    public async Task Manual_sync_of_a_foreign_connection_is_not_found_and_never_calls_the_bank()
    {
        using var environment = new TestBankingEnvironment();
        var backend = new FakeBackendHandler { AuthorizeResponse = HttpStatusCode.NotFound };
        var connection = TestBankingEnvironment.AuthorizedConnection();
        backend.Connections.Add(connection);
        var provider = new RecordingHttpMessageHandler((request, _, _) =>
            throw new Xunit.Sdk.XunitException($"the bank must not be called: {request.RequestUri}"));
        var service = environment.CreateSyncService(provider, backend);

        var result = await service.RequestManualSyncAsync(connection.Id, Owner, force: true, CancellationToken.None);

        Assert.Equal(ManualSyncStatus.NotFound, result.Status);
        Assert.Empty(provider.Requests);
    }

    [Fact]
    public async Task Authorization_state_is_consumed_exactly_once()
    {
        using var environment = new TestBankingEnvironment();
        var backend = new FakeBackendHandler();
        var connection = TestBankingEnvironment.AuthorizedConnection() with { AuthorizationState = "state-xyz", ProviderSessionId = null, Status = "PENDING_AUTHORIZATION" };
        backend.Connections.Add(connection);
        var provider = new RecordingHttpMessageHandler((request, _, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/sessions") return Task.FromResult(TestBankingEnvironment.JsonResponse("{\"session_id\":\"s-1\"}"));
            if (path == "/sessions/s-1") return Task.FromResult(TestBankingEnvironment.JsonResponse("{\"status\":\"AUTHORIZED\",\"accounts\":[]}"));
            throw new Xunit.Sdk.XunitException($"Unexpected provider request: {request.RequestUri}");
        });
        var service = environment.CreateSyncService(provider, backend);

        await service.CompleteConnectionAsync("state-xyz", "code-1", CancellationToken.None);
        Assert.Contains("state-xyz", backend.ConsumedStates);

        // Replay with the same state now finds nothing (one-time).
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CompleteConnectionAsync("state-xyz", "code-2", CancellationToken.None));
    }
}
