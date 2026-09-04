using FullWorth.Backend.Modules.Intelligence;
using FullWorth.Backend.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace FullWorth.Backend.Tests.Intelligence;

public sealed class KnowledgePackSyncConsentTests
{
    [Fact]
    public async Task Disabled_instance_performs_zero_cloud_calls()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<IntelligenceDbContext>().UseSqlite(connection).Options;
        await using var db = new IntelligenceDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var cloud = new CountingCloudClient();
        var packClient = new CountingPackClient();
        var config = new ConfigurationBuilder().Build();
        var installer = new KnowledgePackService(db, new KnowledgePackVerifier(config));
        var service = new KnowledgePackSyncService(
            db,
            new CloudInstanceCredentialStore(db, FieldCipher.Null),
            cloud,
            packClient,
            installer,
            config);

        var result = await service.SyncLatestAsync(CancellationToken.None);

        Assert.Equal(CloudIntelligenceModes.Disabled, result.Mode);
        Assert.False(result.Checked);
        Assert.Equal(0, cloud.Calls);
        Assert.Equal(0, packClient.Calls);
    }

    [Fact]
    public async Task Enabled_state_with_stale_consent_performs_zero_cloud_calls()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<IntelligenceDbContext>().UseSqlite(connection).Options;
        await using var db = new IntelligenceDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var state = new CloudConnectionState
        {
            ScopeKey = CloudConnectionState.InstanceScopeKey,
            Mode = CloudIntelligenceModes.Enabled,
            SetupDecisionAt = DateTimeOffset.UtcNow
        };
        db.CloudConnectionStates.Add(state);
        db.CloudIntelligenceConsents.Add(new CloudIntelligenceConsent
        {
            InstanceId = state.InstanceId,
            AcceptedByUserId = Guid.NewGuid(),
            PolicyVersion = "old-policy",
            AcceptedAt = DateTimeOffset.UtcNow,
            ClientVersion = "1.0.0",
            Locale = "de-DE"
        });
        await db.SaveChangesAsync();

        var cloud = new CountingCloudClient();
        var packClient = new CountingPackClient();
        var config = new ConfigurationBuilder().Build();
        var service = new KnowledgePackSyncService(
            db,
            new CloudInstanceCredentialStore(db, FieldCipher.Null),
            cloud,
            packClient,
            new KnowledgePackService(db, new KnowledgePackVerifier(config)),
            config);

        await service.SyncLatestAsync(CancellationToken.None);

        Assert.Equal(0, cloud.Calls);
        Assert.Equal(0, packClient.Calls);
    }

    private sealed class CountingCloudClient : IFullWorthCloudClient
    {
        public int Calls { get; private set; }
        public Uri BaseUri => new("https://cloud.fullworth.de/");
        public Task<FullWorthCloudRegistrationResult> RegisterAsync(Guid instanceId, string policyVersion, string clientVersion, CancellationToken ct)
        {
            Calls++;
            throw new InvalidOperationException("Cloud must not be called without current consent.");
        }
        public Task<FullWorthCloudRegistrationResult> RotateCredentialAsync(Guid instanceId, string currentCredential, CancellationToken ct)
        {
            Calls++;
            throw new InvalidOperationException("Cloud must not be called without current consent.");
        }
        public Task<FullWorthCloudBatchResult> SubmitBatchAsync(Guid instanceId, string instanceCredential, IReadOnlyList<FullWorthCloudSubmissionEvent> events, CancellationToken ct)
        {
            Calls++;
            throw new InvalidOperationException("Cloud must not be called without current consent.");
        }
    }

    private sealed class CountingPackClient : IFullWorthKnowledgePackClient
    {
        public int Calls { get; private set; }
        public Uri BaseUri => new("https://cloud.fullworth.de/");
        public Task<KnowledgePackManifest?> GetLatestManifestAsync(Guid instanceId, string instanceCredential, string? currentVersion, string? region, CancellationToken ct)
        {
            Calls++;
            throw new InvalidOperationException("Pack API must not be called without current consent.");
        }
        public Task<byte[]> DownloadPackAsync(Guid instanceId, string instanceCredential, string packId, string version, CancellationToken ct)
        {
            Calls++;
            throw new InvalidOperationException("Pack API must not be called without current consent.");
        }
    }
}
