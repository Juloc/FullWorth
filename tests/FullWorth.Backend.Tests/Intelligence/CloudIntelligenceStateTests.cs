using FullWorth.Backend.Modules.Intelligence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Intelligence;

public sealed class CloudIntelligenceStateTests
{
    [Fact]
    public async Task Fresh_instance_requires_explicit_setup_decision()
    {
        await using var fixture = await CreateAsync();
        var service = new CloudIntelligenceStateService(fixture.Db);

        var state = await service.GetAsync(CancellationToken.None);

        Assert.Equal(CloudIntelligenceModes.Disabled, state.Mode);
        Assert.True(state.RequiresSetupDecision);
        Assert.Null(state.SetupDecisionAt);
        Assert.Null(state.SetupDecisionByUserId);
        Assert.Null(state.AcceptedPolicyVersion);
        Assert.False(await service.HasCurrentActiveConsentAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Local_only_choice_is_recorded_without_creating_cloud_consent()
    {
        await using var fixture = await CreateAsync();
        var service = new CloudIntelligenceStateService(fixture.Db);
        var userId = Guid.NewGuid();

        var state = await service.DisableAsync(userId, CancellationToken.None);

        Assert.Equal(CloudIntelligenceModes.Disabled, state.Mode);
        Assert.False(state.RequiresSetupDecision);
        Assert.NotNull(state.SetupDecisionAt);
        Assert.Equal(userId, state.SetupDecisionByUserId);
        Assert.Empty(await fixture.Db.CloudIntelligenceConsents.ToListAsync());
        Assert.False(await service.HasCurrentActiveConsentAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Enable_requires_current_policy_and_disable_revokes_active_consent()
    {
        await using var fixture = await CreateAsync();
        var service = new CloudIntelligenceStateService(fixture.Db);

        await Assert.ThrowsAsync<ArgumentException>(() => service.EnableAsync(
            Guid.NewGuid(),
            new EnableCloudIntelligenceRequest("stale-policy", "de-DE", "test"),
            CancellationToken.None));

        var userId = Guid.NewGuid();
        var enabled = await service.EnableAsync(
            userId,
            new EnableCloudIntelligenceRequest(CloudIntelligencePolicy.CurrentVersion, "de-DE", "test"),
            CancellationToken.None);

        Assert.Equal(CloudIntelligenceModes.Enabled, enabled.Mode);
        Assert.False(enabled.RequiresSetupDecision);
        Assert.Equal(userId, enabled.SetupDecisionByUserId);
        Assert.Equal(CloudIntelligencePolicy.CurrentVersion, enabled.AcceptedPolicyVersion);
        var consent = await fixture.Db.CloudIntelligenceConsents.SingleAsync();
        Assert.Equal(userId, consent.AcceptedByUserId);
        Assert.Null(consent.RevokedAt);
        Assert.Equal("de-DE", consent.Locale);

        var disablingUserId = Guid.NewGuid();
        var disabled = await service.DisableAsync(disablingUserId, CancellationToken.None);

        Assert.Equal(CloudIntelligenceModes.Disabled, disabled.Mode);
        Assert.False(disabled.RequiresSetupDecision);
        Assert.Equal(disablingUserId, disabled.SetupDecisionByUserId);
        consent = await fixture.Db.CloudIntelligenceConsents.SingleAsync();
        Assert.NotNull(consent.RevokedAt);
        Assert.False(await service.HasCurrentActiveConsentAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Disable_discards_unsent_outbox_rows()
    {
        await using var fixture = await CreateAsync();
        var service = new CloudIntelligenceStateService(fixture.Db);
        var userId = Guid.NewGuid();
        var enabled = await service.EnableAsync(
            userId,
            new EnableCloudIntelligenceRequest(CloudIntelligencePolicy.CurrentVersion, "de", "test"),
            CancellationToken.None);

        fixture.Db.CloudSubmissionOutbox.Add(new CloudSubmissionOutbox
        {
            InstanceId = enabled.InstanceId,
            IdempotencyKey = "test:" + Guid.NewGuid().ToString("N"),
            EventType = "merchant_mapping",
            PayloadJson = "{}",
            Status = CloudSubmissionStatuses.Queued
        });
        await fixture.Db.SaveChangesAsync();

        await service.DisableAsync(userId, CancellationToken.None);

        Assert.Empty(await fixture.Db.CloudSubmissionOutbox.ToListAsync());
    }

    [Fact]
    public async Task Enabled_instance_with_stale_policy_requires_reconsent_and_is_not_upload_eligible()
    {
        await using var fixture = await CreateAsync();
        var userId = Guid.NewGuid();
        var state = new CloudConnectionState
        {
            ScopeKey = CloudConnectionState.InstanceScopeKey,
            Mode = CloudIntelligenceModes.Enabled,
            SetupDecisionAt = DateTimeOffset.UtcNow.AddDays(-7),
            SetupDecisionByUserId = userId,
            EnabledAt = DateTimeOffset.UtcNow.AddDays(-7)
        };
        fixture.Db.CloudConnectionStates.Add(state);
        fixture.Db.CloudIntelligenceConsents.Add(new CloudIntelligenceConsent
        {
            InstanceId = state.InstanceId,
            AcceptedByUserId = userId,
            PolicyVersion = "previous-policy",
            AcceptedAt = DateTimeOffset.UtcNow.AddDays(-7),
            Locale = "de-DE",
            ClientVersion = "test"
        });
        await fixture.Db.SaveChangesAsync();
        var service = new CloudIntelligenceStateService(fixture.Db);

        var view = await service.GetAsync(CancellationToken.None);

        Assert.Equal(CloudIntelligenceModes.Enabled, view.Mode);
        Assert.True(view.RequiresSetupDecision);
        Assert.Equal("previous-policy", view.AcceptedPolicyVersion);
        Assert.Equal(CloudIntelligencePolicy.CurrentVersion, view.CurrentPolicyVersion);
        Assert.False(await service.HasCurrentActiveConsentAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Reenabling_current_policy_is_idempotent()
    {
        await using var fixture = await CreateAsync();
        var service = new CloudIntelligenceStateService(fixture.Db);
        var userId = Guid.NewGuid();
        var request = new EnableCloudIntelligenceRequest(CloudIntelligencePolicy.CurrentVersion, "de", "test");

        await service.EnableAsync(userId, request, CancellationToken.None);
        await service.EnableAsync(userId, request, CancellationToken.None);

        Assert.Equal(1, await fixture.Db.CloudConnectionStates.CountAsync());
        Assert.Equal(1, await fixture.Db.CloudIntelligenceConsents.CountAsync());
        Assert.True(await service.HasCurrentActiveConsentAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Resolved_cloud_endpoint_falls_back_to_official_url_without_a_client()
    {
        // The link-health surface must degrade cleanly when nothing wires a real cloud client in -
        // this mirrors what a self-hoster who set no FullWorthCloud:BaseUrl actually resolves to.
        await using var fixture = await CreateAsync();
        var service = new CloudIntelligenceStateService(fixture.Db);

        var state = await service.GetAsync(CancellationToken.None);

        Assert.Equal(FullWorthCloudClient.OfficialBaseUrl, state.CloudEndpoint);
    }

    [Fact]
    public async Task Resolved_cloud_endpoint_reflects_the_configured_client()
    {
        // A self-hoster who points FullWorthCloud:BaseUrl at their own Cloud has no other way to
        // confirm it took effect - this is the field the diagnostics page reads.
        await using var fixture = await CreateAsync();
        var service = new CloudIntelligenceStateService(fixture.Db, new FakeCloudClient("https://cloud.example.org/"));

        var state = await service.GetAsync(CancellationToken.None);

        Assert.Equal("https://cloud.example.org/", state.CloudEndpoint);
    }

    [Fact]
    public async Task Outbox_health_is_empty_when_nothing_is_queued()
    {
        await using var fixture = await CreateAsync();
        var service = new CloudIntelligenceStateService(fixture.Db);

        var state = await service.GetAsync(CancellationToken.None);

        Assert.Equal(0, state.Outbox.WaitingCount);
        Assert.Equal(0, state.Outbox.DeadLetterCount);
        Assert.Null(state.Outbox.OldestWaitingCreatedAt);
    }

    [Fact]
    public async Task Outbox_health_counts_waiting_and_dead_letter_rows_and_reports_the_oldest_waiting_age()
    {
        // This is the number that tells an operator whether the link is working at all: consent and
        // entitlement can look fine while every submission sits stuck because the Cloud is unreachable.
        await using var fixture = await CreateAsync();
        var service = new CloudIntelligenceStateService(fixture.Db);
        var enabled = await service.EnableAsync(
            Guid.NewGuid(),
            new EnableCloudIntelligenceRequest(CloudIntelligencePolicy.CurrentVersion, "de", "test"),
            CancellationToken.None);

        var oldest = DateTimeOffset.UtcNow.AddHours(-3);
        var newer = DateTimeOffset.UtcNow.AddMinutes(-5);
        fixture.Db.CloudSubmissionOutbox.AddRange(
            NewRow(enabled.InstanceId, CloudSubmissionStatuses.Queued, oldest),
            NewRow(enabled.InstanceId, CloudSubmissionStatuses.Failed, newer),
            NewRow(enabled.InstanceId, CloudSubmissionStatuses.Sending, newer),
            NewRow(enabled.InstanceId, CloudSubmissionStatuses.Sent, newer),
            NewRow(enabled.InstanceId, CloudSubmissionStatuses.DeadLetter, newer),
            NewRow(enabled.InstanceId, CloudSubmissionStatuses.DeadLetter, newer));
        await fixture.Db.SaveChangesAsync();

        var state = await service.GetAsync(CancellationToken.None);

        Assert.Equal(3, state.Outbox.WaitingCount);
        Assert.Equal(2, state.Outbox.DeadLetterCount);
        Assert.NotNull(state.Outbox.OldestWaitingCreatedAt);
        // Sqlite round-trips DateTimeOffset with less precision than the in-memory value, so compare
        // with a small tolerance rather than requiring bit-for-bit equality.
        Assert.True((oldest - state.Outbox.OldestWaitingCreatedAt!.Value).Duration() < TimeSpan.FromSeconds(1));
    }

    private static CloudSubmissionOutbox NewRow(Guid instanceId, string status, DateTimeOffset createdAt) => new()
    {
        InstanceId = instanceId,
        IdempotencyKey = "test:" + Guid.NewGuid().ToString("N"),
        EventType = "merchant_mapping",
        PayloadJson = "{}",
        Status = status,
        CreatedAt = createdAt
    };

    private static async Task<Fixture> CreateAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<IntelligenceDbContext>().UseSqlite(connection).Options;
        var db = new IntelligenceDbContext(options);
        await db.Database.EnsureCreatedAsync();
        return new Fixture(connection, db);
    }

    private sealed class Fixture(SqliteConnection connection, IntelligenceDbContext db) : IAsyncDisposable
    {
        public IntelligenceDbContext Db { get; } = db;

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class FakeCloudClient(string baseUri) : IFullWorthCloudClient
    {
        public Uri BaseUri { get; } = new(baseUri);

        public Task<FullWorthCloudRegistrationResult> RegisterAsync(
            Guid instanceId, string policyVersion, string clientVersion, string? currentCredential, CancellationToken ct) =>
            Task.FromResult(new FullWorthCloudRegistrationResult(instanceId, "test-secret", null, "active"));

        public Task<FullWorthCloudRegistrationResult> RotateCredentialAsync(
            Guid instanceId, string currentCredential, CancellationToken ct) =>
            Task.FromResult(new FullWorthCloudRegistrationResult(instanceId, "test-secret", null, "active"));

        public Task<FullWorthCloudBatchResult> SubmitBatchAsync(
            Guid instanceId, string instanceCredential, IReadOnlyList<FullWorthCloudSubmissionEvent> events, CancellationToken ct) =>
            Task.FromResult(new FullWorthCloudBatchResult("batch-test", 0, 0, 0, []));

        public Task<FullWorthCloudBenchmark?> GetBenchmarkAsync(
            string instanceCredential, string metricKey, string? currency, string? country, string? regionBucket,
            string? householdSizeBand, string? incomeBand, string? ageBand, string? observedMonth, CancellationToken ct) =>
            Task.FromResult<FullWorthCloudBenchmark?>(null);

        public Task<KnowledgePackManifest?> GetLatestKnowledgePackManifestAsync(
            string instanceCredential, string? currentVersion, string? region, CancellationToken ct) =>
            Task.FromResult<KnowledgePackManifest?>(null);

        public Task<byte[]> DownloadKnowledgePackAsync(
            string instanceCredential, string packId, string version, CancellationToken ct) =>
            Task.FromResult(Array.Empty<byte>());
    }
}
