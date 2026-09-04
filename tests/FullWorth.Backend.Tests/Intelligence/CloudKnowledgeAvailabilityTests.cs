using FullWorth.Backend.Modules.Intelligence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Intelligence;

public sealed class CloudKnowledgeAvailabilityTests
{
    [Fact]
    public async Task Official_mappings_are_visible_only_with_enabled_current_consent()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<IntelligenceDbContext>().UseSqlite(connection).Options;
        await using var db = new IntelligenceDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var instanceId = Guid.NewGuid();
        db.CloudConnectionStates.Add(new CloudConnectionState
        {
            ScopeKey = CloudConnectionState.InstanceScopeKey,
            InstanceId = instanceId,
            Mode = CloudIntelligenceModes.Enabled,
            SetupDecisionAt = DateTimeOffset.UtcNow
        });
        var consent = new CloudIntelligenceConsent
        {
            InstanceId = instanceId,
            AcceptedByUserId = Guid.NewGuid(),
            PolicyVersion = CloudIntelligencePolicy.CurrentVersion,
            AcceptedAt = DateTimeOffset.UtcNow,
            Locale = "de-DE",
            ClientVersion = "test"
        };
        db.CloudIntelligenceConsents.Add(consent);
        db.OfficialMerchantMappings.Add(new OfficialMerchantMapping
        {
            PackId = "merchant-de",
            PackVersion = "1.0.0",
            AliasKey = "AMAZON",
            Direction = "expense",
            CanonicalMerchantKey = "AMAZON",
            CanonicalName = "Amazon",
            CategoryKey = "shopping.online",
            Country = "DE",
            Confidence = 0.95m
        });
        await db.SaveChangesAsync();

        Assert.Single(await db.OfficialMerchantMappings.AsNoTracking().ToListAsync());

        consent.PolicyVersion = "stale-policy";
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        Assert.Empty(await db.OfficialMerchantMappings.AsNoTracking().ToListAsync());
        Assert.Single(await db.OfficialMerchantMappings.IgnoreQueryFilters().AsNoTracking().ToListAsync());

        var state = await db.CloudConnectionStates.SingleAsync();
        state.Mode = CloudIntelligenceModes.Disabled;
        var currentConsent = await db.CloudIntelligenceConsents.SingleAsync();
        currentConsent.PolicyVersion = CloudIntelligencePolicy.CurrentVersion;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        Assert.Empty(await db.OfficialMerchantMappings.AsNoTracking().ToListAsync());
    }
}
