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
}
