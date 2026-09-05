using System.Text.Json;
using FullWorth.Banking.Services;

namespace FullWorth.Banking.Tests;

public sealed class BankSyncSafetyTests
{
    [Fact]
    public void Configured_defaults_are_above_the_360_minute_floor()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "banking-appsettings.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var sync = document.RootElement.GetProperty("Sync");

        Assert.InRange(sync.GetProperty("IntervalMinutes").GetInt32(), 5, 60);
        Assert.True(sync.GetProperty("MinimumBackgroundSyncIntervalMinutes").GetInt32() >= 360);
        Assert.True(sync.GetProperty("RateLimitCooldownMinutes").GetInt32() >= 360);
    }

    [Fact]
    public async Task Unsafe_configuration_cannot_reduce_background_attempt_floor_below_360_minutes()
    {
        using var environment = new TestBankingEnvironment();
        var backend = new FakeBackendHandler();
        backend.Connections.Add(TestBankingEnvironment.AuthorizedConnection(
            lastAttemptAt: DateTimeOffset.UtcNow.AddMinutes(-300)));
        var provider = NeverCallProvider();
        var service = environment.CreateSyncService(provider, backend, new BankingSyncOptions
        {
            MinimumBackgroundSyncIntervalMinutes = 1,
            RateLimitCooldownMinutes = 1,
            IntervalMinutes = 1
        });

        var result = await service.SyncAllAsync(CancellationToken.None);

        Assert.Equal(0, result.Synced);
        Assert.Equal(1, result.Skipped);
        Assert.False(result.AlreadyRunning);
        Assert.Empty(provider.Requests);
    }

    [Fact]
    public async Task NextSyncAllowedAt_prevents_an_immediate_repeat_even_when_LastAttemptAt_is_old()
    {
        using var environment = new TestBankingEnvironment();
        var backend = new FakeBackendHandler();
        backend.Connections.Add(TestBankingEnvironment.AuthorizedConnection(
            lastAttemptAt: DateTimeOffset.UtcNow.AddDays(-2),
            nextSyncAllowedAt: DateTimeOffset.UtcNow.AddMinutes(30)));
        var provider = NeverCallProvider();
        var service = environment.CreateSyncService(provider, backend);

        var result = await service.SyncAllAsync(CancellationToken.None);

        Assert.Equal(1, result.Skipped);
        Assert.Empty(provider.Requests);
    }

    [Fact]
    public async Task LastAttemptAt_prevents_an_immediate_repeat_when_NextSyncAllowedAt_is_missing()
    {
        using var environment = new TestBankingEnvironment();
        var backend = new FakeBackendHandler();
        backend.Connections.Add(TestBankingEnvironment.AuthorizedConnection(
            lastAttemptAt: DateTimeOffset.UtcNow.AddMinutes(-30)));
        var provider = NeverCallProvider();
        var service = environment.CreateSyncService(provider, backend);

        var result = await service.SyncAllAsync(CancellationToken.None);

        Assert.Equal(1, result.Skipped);
        Assert.Empty(provider.Requests);
    }

    [Fact]
    public async Task Persisted_cooldown_survives_a_new_service_and_gate_instance()
    {
        using var environment = new TestBankingEnvironment();
        var backend = new FakeBackendHandler();
        backend.Connections.Add(TestBankingEnvironment.AuthorizedConnection(
            lastAttemptAt: DateTimeOffset.UtcNow.AddDays(-1)));

        var firstProvider = new RecordingHttpMessageHandler((request, _, _) =>
        {
            Assert.Equal("/sessions/session-1", request.RequestUri!.AbsolutePath);
            return Task.FromResult(TestBankingEnvironment.JsonResponse("{\"status\":\"AUTHORIZED\",\"accounts\":[]}"));
        });
        var firstService = environment.CreateSyncService(firstProvider, backend);

        var first = await firstService.SyncAllAsync(CancellationToken.None);
        Assert.Equal(1, first.Synced);
        Assert.NotNull(backend.Connections.Single().LastAttemptAt);
        Assert.NotNull(backend.Connections.Single().NextSyncAllowedAt);

        var restartedProvider = NeverCallProvider();
        var restartedService = environment.CreateSyncService(
            restartedProvider,
            backend,
            gate: new BankSyncConcurrencyGate());

        var afterRestart = await restartedService.SyncAllAsync(CancellationToken.None);

        Assert.Equal(1, afterRestart.Skipped);
        Assert.Empty(restartedProvider.Requests);
    }

    // Background sync always obeys the persisted cooldown; only a user-initiated force (RequestManualSync
    // with force:true, covered in ManualSyncTests) may bypass the cadence, and even then never the
    // provider rate-limit. The automatic path must stay conservative.
    [Fact]
    public async Task Background_sync_always_obeys_the_persisted_cooldown()
    {
        using var environment = new TestBankingEnvironment();
        var backend = new FakeBackendHandler();
        backend.Connections.Add(TestBankingEnvironment.AuthorizedConnection(
            nextSyncAllowedAt: DateTimeOffset.UtcNow.AddHours(1)));
        var provider = NeverCallProvider();
        var service = environment.CreateSyncService(provider, backend);

        var result = await service.SyncAllAsync(CancellationToken.None);

        Assert.Equal(1, result.Skipped);
        Assert.Empty(provider.Requests);
    }

    private static RecordingHttpMessageHandler NeverCallProvider() => new((request, _, _) =>
        throw new Xunit.Sdk.XunitException($"Provider must not be called: {request.Method} {request.RequestUri}"));
}
