using System.Net;
using FullWorth.Banking.Backend;
using FullWorth.Banking.Services;

namespace FullWorth.Banking.Tests;

public sealed class ManualSyncTests
{
    [Fact]
    public async Task Non_force_returns_cooldown_and_does_not_call_the_bank_when_next_sync_is_in_the_future()
    {
        using var environment = new TestBankingEnvironment();
        var backend = new FakeBackendHandler();
        var connection = TestBankingEnvironment.AuthorizedConnection(nextSyncAllowedAt: DateTimeOffset.UtcNow.AddMinutes(45));
        backend.Connections.Add(connection);
        var provider = NeverCalledProvider();
        var service = environment.CreateSyncService(provider, backend);

        var result = await service.RequestManualSyncAsync(connection.Id, Caller, force: false, CancellationToken.None);

        Assert.Equal(ManualSyncStatus.Cooldown, result.Status);
        Assert.Equal(connection.NextSyncAllowedAt, result.NextSyncAllowedAt);
        Assert.Empty(provider.Requests);
    }

    // The whole point of "sync now": a user-initiated force bypasses our own background cadence and
    // actually hits the bank even though NextSyncAllowedAt is still in the future.
    [Fact]
    public async Task Force_bypasses_the_cadence_cooldown_and_calls_the_bank()
    {
        using var environment = new TestBankingEnvironment();
        var backend = new FakeBackendHandler();
        var connection = TestBankingEnvironment.AuthorizedConnection(nextSyncAllowedAt: DateTimeOffset.UtcNow.AddMinutes(45));
        backend.Connections.Add(connection);
        var provider = new RecordingHttpMessageHandler((request, _, _) =>
        {
            Assert.Equal("/sessions/session-1", request.RequestUri!.AbsolutePath);
            return Task.FromResult(TestBankingEnvironment.JsonResponse("{\"status\":\"AUTHORIZED\",\"accounts\":[]}"));
        });
        var service = environment.CreateSyncService(provider, backend);

        var result = await service.RequestManualSyncAsync(connection.Id, Caller, force: true, CancellationToken.None);

        Assert.Equal(ManualSyncStatus.Started, result.Status);
        Assert.Single(provider.Requests);
        Assert.Contains(backend.Upserts, x => x.LastSyncedAt is not null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reauthorization_required_when_consent_has_expired_regardless_of_force(bool force)
    {
        using var environment = new TestBankingEnvironment();
        var backend = new FakeBackendHandler();
        var connection = new BankConnectionDto(
            Guid.NewGuid(), "enable-banking", "Test Bank", "DE", null, null, "session-1",
            "AUTHORIZED", DateTimeOffset.UtcNow.AddDays(-1), null, null, null, 0, null);
        backend.Connections.Add(connection);
        var provider = NeverCalledProvider();
        var service = environment.CreateSyncService(provider, backend);

        var result = await service.RequestManualSyncAsync(connection.Id, Caller, force, CancellationToken.None);

        Assert.Equal(ManualSyncStatus.ReauthorizationRequired, result.Status);
        Assert.Empty(provider.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Already_running_when_another_sync_holds_the_gate_regardless_of_force(bool force)
    {
        using var environment = new TestBankingEnvironment();
        var backend = new FakeBackendHandler();
        var connection = TestBankingEnvironment.AuthorizedConnection();
        backend.Connections.Add(connection);
        var provider = NeverCalledProvider();
        var gate = new BankSyncConcurrencyGate();
        var service = environment.CreateSyncService(provider, backend, gate: gate);

        using (await gate.EnterAsync(CancellationToken.None))
        {
            var result = await service.RequestManualSyncAsync(connection.Id, Caller, force, CancellationToken.None);
            Assert.Equal(ManualSyncStatus.AlreadyRunning, result.Status);
        }

        Assert.Empty(provider.Requests);
    }

    // Force tries the provider now, but a provider rate-limit still wins: the caller gets a Cooldown
    // carrying the provider's retry time, never a green "started".
    [Fact]
    public async Task Force_still_reports_a_provider_rate_limit_as_cooldown()
    {
        using var environment = new TestBankingEnvironment();
        var backend = new FakeBackendHandler();
        var connection = TestBankingEnvironment.AuthorizedConnection(nextSyncAllowedAt: DateTimeOffset.UtcNow.AddMinutes(45));
        backend.Connections.Add(connection);
        var provider = new RecordingHttpMessageHandler((_, _, _) => Task.FromResult(
            TestBankingEnvironment.JsonResponse("{\"error_code\":\"ASPSP_RATE_LIMIT_EXCEEDED\"}", HttpStatusCode.TooManyRequests)));
        var service = environment.CreateSyncService(provider, backend, new BankingSyncOptions
        {
            MinimumBackgroundSyncIntervalMinutes = 1,
            RateLimitCooldownMinutes = 1
        });

        var result = await service.RequestManualSyncAsync(connection.Id, Caller, force: true, CancellationToken.None);

        Assert.Equal(ManualSyncStatus.Cooldown, result.Status);
        Assert.NotNull(result.NextSyncAllowedAt);
        Assert.Single(provider.Requests);
    }

    [Fact]
    public async Task GenericUnauthorizedProviderResponseIsErrorNotReauthorizationOrRateLimit()
    {
        using var environment = new TestBankingEnvironment();
        var backend = new FakeBackendHandler();
        var connection = TestBankingEnvironment.AuthorizedConnection();
        backend.Connections.Add(connection);
        var provider = new RecordingHttpMessageHandler((_, _, _) => Task.FromResult(
            TestBankingEnvironment.JsonResponse(
                "{\"error_code\":\"NOT_ALLOWED\"}",
                HttpStatusCode.Unauthorized)));
        var service = environment.CreateSyncService(provider, backend);

        var result = await service.RequestManualSyncAsync(
            connection.Id,
            Caller,
            force: true,
            CancellationToken.None);

        Assert.Equal(ManualSyncStatus.Error, result.Status);
        var final = backend.Connections.Single();
        Assert.Equal("AUTHORIZED", final.Status);
        Assert.Equal("ENABLE_BANKING_AUTH_FAILED", final.LastError);
    }

    [Fact]
    public async Task RevokedProviderConsentReturnsReauthorizationAndClearsCooldown()
    {
        using var environment = new TestBankingEnvironment();
        var backend = new FakeBackendHandler();
        var connection = TestBankingEnvironment.AuthorizedConnection();
        backend.Connections.Add(connection);
        var provider = new RecordingHttpMessageHandler((_, _, _) => Task.FromResult(
            TestBankingEnvironment.JsonResponse(
                "{\"error_code\":\"SESSION_REVOKED\"}",
                HttpStatusCode.Unauthorized)));
        var service = environment.CreateSyncService(provider, backend);

        var result = await service.RequestManualSyncAsync(
            connection.Id,
            Caller,
            force: true,
            CancellationToken.None);

        Assert.Equal(ManualSyncStatus.ReauthorizationRequired, result.Status);
        var final = backend.Connections.Single();
        Assert.Equal("REVOKED", final.Status);
        Assert.Equal("SESSION_REVOKED", final.LastError);
        Assert.Null(final.NextSyncAllowedAt);
    }

    [Fact]
    public async Task Returns_not_found_for_an_unknown_connection()
    {
        using var environment = new TestBankingEnvironment();
        var backend = new FakeBackendHandler();
        var service = environment.CreateSyncService(NeverCalledProvider(), backend);

        var result = await service.RequestManualSyncAsync(Guid.NewGuid(), Caller, force: true, CancellationToken.None);

        Assert.Equal(ManualSyncStatus.NotFound, result.Status);
    }

    [Fact]
    public async Task Non_force_starts_and_records_a_fresh_cooldown_when_permitted()
    {
        using var environment = new TestBankingEnvironment();
        var backend = new FakeBackendHandler();
        var connection = TestBankingEnvironment.AuthorizedConnection();
        backend.Connections.Add(connection);
        var provider = new RecordingHttpMessageHandler((request, _, _) =>
        {
            Assert.Equal("/sessions/session-1", request.RequestUri!.AbsolutePath);
            return Task.FromResult(TestBankingEnvironment.JsonResponse("{\"status\":\"AUTHORIZED\",\"accounts\":[]}"));
        });
        var service = environment.CreateSyncService(provider, backend);

        var result = await service.RequestManualSyncAsync(connection.Id, Caller, force: false, CancellationToken.None);

        Assert.Equal(ManualSyncStatus.Started, result.Status);
        Assert.NotNull(result.NextSyncAllowedAt);
        Assert.Single(provider.Requests);
        Assert.Contains(backend.Upserts, x => x.LastSyncedAt is not null);
    }

    private static readonly BankingCaller Caller = new(Guid.NewGuid(), Guid.NewGuid());

    private static RecordingHttpMessageHandler NeverCalledProvider() =>
        new((request, _, _) => throw new Xunit.Sdk.XunitException($"the bank should not be called: {request.RequestUri}"));
}
