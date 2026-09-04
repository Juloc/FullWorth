using FullWorth.Backend.Modules.Intelligence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Intelligence;

public sealed class CloudOptOutPurgeTests
{
    [Fact]
    public async Task Disable_removes_active_and_archived_cloud_knowledge()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<IntelligenceDbContext>().UseSqlite(connection).Options;
        await using var db = new IntelligenceDbContext(options);
        await db.Database.EnsureCreatedAsync();

        db.KnowledgePackInstallations.Add(new KnowledgePackInstallation
        {
            PackId = "merchant-de",
            Version = "1.0.0",
            SchemaVersion = "1",
            Region = "DE",
            ContentSha256 = new string('a', 64),
            SignatureAlgorithm = KnowledgePackVerifier.SupportedSignatureAlgorithm,
            MerchantMappingCount = 1
        });
        db.KnowledgePackArchives.Add(new KnowledgePackArchive
        {
            PackId = "merchant-de",
            Version = "1.0.0",
            SchemaVersion = "1",
            Region = "DE",
            ContentSha256 = new string('a', 64),
            SignatureAlgorithm = KnowledgePackVerifier.SupportedSignatureAlgorithm,
            SignatureBase64 = "AA==",
            PayloadBase64 = "e30="
        });
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

        Assert.Single(await db.OfficialMerchantMappings.IgnoreQueryFilters().ToListAsync());

        var service = new CloudIntelligenceStateService(db);
        await service.DisableAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Empty(await db.KnowledgePackInstallations.ToListAsync());
        Assert.Empty(await db.KnowledgePackArchives.ToListAsync());
        Assert.Empty(await db.OfficialMerchantMappings.IgnoreQueryFilters().ToListAsync());
    }
}
