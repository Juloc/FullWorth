using System.Security.Cryptography;
using System.Text.Json;
using FullWorth.Backend.Modules.Intelligence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace FullWorth.Backend.Tests.Intelligence;

public sealed class KnowledgePackInstallationTests
{
    [Fact]
    public async Task Invalid_update_keeps_last_good_pack_and_verified_archive_can_rollback()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<IntelligenceDbContext>().UseSqlite(connection).Options;
        await using var db = new IntelligenceDbContext(options);
        await db.Database.EnsureCreatedAsync();
        using var signer = RSA.Create(2048);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FullWorthCloud:KnowledgePackPublicKeyBase64"] = Convert.ToBase64String(signer.ExportSubjectPublicKeyInfo())
            })
            .Build();
        var service = new KnowledgePackService(db, new KnowledgePackVerifier(configuration));

        var v1 = SignedPack(signer, "1.0.0", "shopping.v1");
        await service.InstallAsync(v1.Manifest, v1.Payload, CancellationToken.None);
        var v2 = SignedPack(signer, "2.0.0", "shopping.v2");
        await service.InstallAsync(v2.Manifest, v2.Payload, CancellationToken.None);

        var invalidV3 = SignedPack(signer, "3.0.0", "shopping.v3");
        var badManifest = invalidV3.Manifest with { ContentSha256 = new string('0', 64) };
        var ex = await Assert.ThrowsAsync<KnowledgePackVerificationException>(() =>
            service.InstallAsync(badManifest, invalidV3.Payload, CancellationToken.None));
        Assert.Equal("knowledge_pack_hash_mismatch", ex.ErrorCode);

        var installed = await db.KnowledgePackInstallations.SingleAsync();
        var mapping = await db.OfficialMerchantMappings.IgnoreQueryFilters().SingleAsync();
        Assert.Equal("2.0.0", installed.Version);
        Assert.Equal("shopping.v2", mapping.CategoryKey);
        Assert.Equal(2, await db.KnowledgePackArchives.CountAsync());

        var rolledBack = await service.RollbackAsync("merchant-de", "1.0.0", CancellationToken.None);

        Assert.True(rolledBack.RolledBack);
        installed = await db.KnowledgePackInstallations.SingleAsync();
        mapping = await db.OfficialMerchantMappings.IgnoreQueryFilters().SingleAsync();
        Assert.Equal("1.0.0", installed.Version);
        Assert.Equal("shopping.v1", mapping.CategoryKey);
        var archivedV1 = await db.KnowledgePackArchives.SingleAsync(x => x.Version == "1.0.0");
        Assert.Equal(Convert.ToBase64String(v1.Payload), archivedV1.PayloadBase64);
    }

    private static (KnowledgePackManifest Manifest, byte[] Payload) SignedPack(RSA signer, string version, string category)
    {
        var payload = new KnowledgePackPayload(
            "merchant-de",
            version,
            KnowledgePackPolicy.CurrentSchemaVersion,
            "DE",
            [new KnowledgePackMerchantPayload(
                "AMZN MKTP DE",
                "expense",
                "AMAZON",
                "Amazon",
                category,
                "DE",
                0.98m,
                "amazon.de",
                "amazon")]);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var signature = Convert.ToBase64String(signer.SignData(bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
        return (new KnowledgePackManifest(
            payload.PackId,
            payload.Version,
            payload.SchemaVersion,
            payload.Region,
            hash,
            KnowledgePackVerifier.SupportedSignatureAlgorithm,
            signature,
            null), bytes);
    }
}
