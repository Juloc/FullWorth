using FullWorth.Backend.Modules.Intelligence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Intelligence;

public sealed class CloudConsentRenewalTests
{
    [Fact]
    public async Task New_policy_consent_discards_unsent_rows_from_previous_policy()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var db = new IntelligenceDbContext(
            new DbContextOptionsBuilder<IntelligenceDbContext>()
                .UseSqlite(connection)
                .Options);
        await db.Database.EnsureCreatedAsync();

        var instanceId = Guid.NewGuid();
        var oldConsent = new CloudIntelligenceConsent
        {
            InstanceId = instanceId,
            AcceptedByUserId = Guid.NewGuid(),
            PolicyVersion = "2026-09-06.1",
            AcceptedAt = DateTimeOffset.UtcNow.AddDays(-1)
        };
        db.CloudConnectionStates.Add(new CloudConnectionState
        {
            ScopeKey = CloudConnectionState.InstanceScopeKey,
            InstanceId = instanceId,
            Mode = CloudIntelligenceModes.Enabled,
            SetupDecisionAt = DateTimeOffset.UtcNow.AddDays(-1)
        });
        db.CloudIntelligenceConsents.Add(oldConsent);
        db.CloudSubmissionOutbox.AddRange(
            new CloudSubmissionOutbox
            {
                InstanceId = instanceId,
                IdempotencyKey = "old:queued",
                EventType = "benchmark_observation",
                PayloadJson = "{}",
                Status = CloudSubmissionStatuses.Queued
            },
            new CloudSubmissionOutbox
            {
                InstanceId = instanceId,
                IdempotencyKey = "old:failed",
                EventType = "merchant_mapping",
                PayloadJson = "{}",
                Status = CloudSubmissionStatuses.Failed
            },
            new CloudSubmissionOutbox
            {
                InstanceId = instanceId,
                IdempotencyKey = "old:sent",
                EventType = "merchant_mapping",
                PayloadJson = "{}",
                Status = CloudSubmissionStatuses.Sent
            });
        await db.SaveChangesAsync();

        var service = new CloudIntelligenceStateService(db);
        var before = await service.GetAsync(CancellationToken.None);
        Assert.True(before.RequiresSetupDecision);
        Assert.False(await service.HasCurrentActiveConsentAsync(CancellationToken.None));

        var result = await service.EnableAsync(
            Guid.NewGuid(),
            new EnableCloudIntelligenceRequest(
                CloudIntelligencePolicy.CurrentVersion,
                "de",
                "test"),
            CancellationToken.None);

        Assert.False(result.RequiresSetupDecision);
        Assert.Equal(CloudIntelligencePolicy.CurrentVersion, result.AcceptedPolicyVersion);
        Assert.True(await service.HasCurrentActiveConsentAsync(CancellationToken.None));

        var old = await db.CloudIntelligenceConsents.SingleAsync(x => x.Id == oldConsent.Id);
        Assert.NotNull(old.RevokedAt);

        var outbox = await db.CloudSubmissionOutbox.AsNoTracking().ToListAsync();
        Assert.Single(outbox);
        Assert.Equal("old:sent", outbox[0].IdempotencyKey);
    }
}
