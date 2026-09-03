using FullWorth.Backend.Modules.Intelligence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Intelligence;

public sealed class CloudOptOutPurgeTests
{
    [Fact]
    public async Task Disable_revokes_consent_and_drops_instance_credential()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<IntelligenceDbContext>().UseSqlite(connection).Options;
        await using var db = new IntelligenceDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var service = new CloudIntelligenceStateService(db);
        var enabled = await service.EnableAsync(
            Guid.NewGuid(),
            new EnableCloudIntelligenceRequest(CloudIntelligencePolicy.CurrentVersion, "de-DE", "test"),
            CancellationToken.None);

        db.CloudInstanceCredentials.Add(new CloudInstanceCredential
        {
            InstanceId = enabled.InstanceId,
            ProtectedSecret = "protected",
            SecretFingerprint = "sha256:deadbeefdeadbeef"
        });
        await db.SaveChangesAsync();

        await service.DisableAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Empty(await db.CloudInstanceCredentials.ToListAsync());
        Assert.False(await service.HasCurrentActiveConsentAsync(CancellationToken.None));
        var consent = await db.CloudIntelligenceConsents.SingleAsync();
        Assert.NotNull(consent.RevokedAt);
    }
}
