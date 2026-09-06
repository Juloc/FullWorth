using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FullWorth.Backend.Modules.Intelligence;
using FullWorth.Backend.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace FullWorth.Backend.Tests.Intelligence;

public sealed class KnowledgePackSyncServiceTests
{
    [Fact]
    public async Task Valid_signed_pack_is_verified_and_installed()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var rsa = RSA.Create(2048);
        var pack = BuildPack(rsa, "2026.09.06-1", "REWE", "food.groceries");
        var cloud = new FakeCloudClient(pack.Manifest, pack.Payload);
        var service = fixture.CreateService(cloud, rsa);

        var result = await service.SyncOnceAsync(CancellationToken.None);

        Assert.Equal("installed", result.Status);
        Assert.Equal(pack.Manifest.Version, result.Version);
        Assert.Equal(1, result.MerchantMappings);

        var installation = await fixture.Db.KnowledgePackInstallations.SingleAsync();
        Assert.Equal(pack.Manifest.Version, installation.Version);
        Assert.Null(installation.LastErrorCode);

        var mapping = await fixture.Db.OfficialMerchantMappings.SingleAsync();
        Assert.Equal("REWE", mapping.AliasKey);
        Assert.Equal("expense", mapping.Direction);
        Assert.Equal("DE", mapping.Country);
        Assert.Equal("food.groceries", mapping.CategoryKey);
        Assert.Equal(pack.Manifest.PackId, mapping.PackId);
        Assert.Equal(pack.Manifest.Version, mapping.PackVersion);
        Assert.Single(await fixture.Db.KnowledgePackArchives.ToListAsync());
    }

    [Fact]
    public async Task Invalid_new_signature_keeps_previous_verified_pack_active()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var rsa = RSA.Create(2048);
        var first = BuildPack(rsa, "2026.09.06-1", "REWE", "food.groceries");
        var cloud = new FakeCloudClient(first.Manifest, first.Payload);
        var service = fixture.CreateService(cloud, rsa);

        Assert.Equal("installed", (await service.SyncOnceAsync(CancellationToken.None)).Status);

        var second = BuildPack(rsa, "2026.09.06-2", "ALDI", "food.groceries");
        cloud.SetPack(
            second.Manifest with { SignatureBase64 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(256)) },
            second.Payload);

        var failed = await service.SyncOnceAsync(CancellationToken.None);

        Assert.Equal("failed", failed.Status);
        Assert.Equal("knowledge_pack_signature_invalid", failed.ErrorCode);

        var installation = await fixture.Db.KnowledgePackInstallations.SingleAsync();
        Assert.Equal(first.Manifest.Version, installation.Version);
        Assert.Equal("knowledge_pack_signature_invalid", installation.LastErrorCode);

        var mappings = await fixture.Db.OfficialMerchantMappings.AsNoTracking().ToListAsync();
        Assert.Single(mappings);
        Assert.Equal("REWE", mappings[0].AliasKey);
        Assert.DoesNotContain(mappings, x => x.AliasKey == "ALDI");
    }

    [Fact]
    public async Task Older_validly_signed_pack_is_rejected_as_downgrade()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var rsa = RSA.Create(2048);
        var current = BuildPack(rsa, "2026.09.06-2", "REWE", "food.groceries");
        var cloud = new FakeCloudClient(current.Manifest, current.Payload);
        var service = fixture.CreateService(cloud, rsa);

        Assert.Equal("installed", (await service.SyncOnceAsync(CancellationToken.None)).Status);

        var older = BuildPack(rsa, "2026.09.06-1", "ALDI", "food.groceries");
        cloud.SetPack(older.Manifest, older.Payload);

        var failed = await service.SyncOnceAsync(CancellationToken.None);

        Assert.Equal("failed", failed.Status);
        Assert.Equal("knowledge_pack_downgrade_rejected", failed.ErrorCode);
        Assert.Equal(current.Manifest.Version, (await fixture.Db.KnowledgePackInstallations.SingleAsync()).Version);
        Assert.Equal("REWE", (await fixture.Db.OfficialMerchantMappings.SingleAsync()).AliasKey);
    }

    private static PackData BuildPack(
        RSA rsa,
        string version,
        string alias,
        string categoryKey)
    {
        var payload = new KnowledgePackPayload(
            "fullworth-official",
            version,
            KnowledgePackProtocol.SchemaVersion,
            "GLOBAL",
            [
                new KnowledgePackMerchantPayload(
                    alias,
                    "expense",
                    "merchant." + alias.ToLowerInvariant(),
                    alias,
                    categoryKey,
                    "DE",
                    0.95m,
                    null,
                    null)
            ]);

        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var signature = rsa.SignData(bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        var manifest = new KnowledgePackManifest(
            payload.PackId,
            payload.Version,
            payload.SchemaVersion,
            payload.Region,
            hash,
            KnowledgePackProtocol.SignatureAlgorithm,
            Convert.ToBase64String(signature),
            null);
        return new PackData(manifest, bytes);
    }

    private sealed record PackData(KnowledgePackManifest Manifest, byte[] Payload);

    private sealed class Fixture(
        SqliteConnection connection,
        IntelligenceDbContext db,
        CloudIntelligenceStateService stateService) : IAsyncDisposable
    {
        public IntelligenceDbContext Db { get; } = db;

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<IntelligenceDbContext>().UseSqlite(connection).Options;
            var db = new IntelligenceDbContext(options);
            await db.Database.EnsureCreatedAsync();

            var stateService = new CloudIntelligenceStateService(db);
            await stateService.EnableAsync(
                Guid.NewGuid(),
                new EnableCloudIntelligenceRequest(
                    CloudIntelligencePolicy.CurrentVersion,
                    "de",
                    "test"),
                CancellationToken.None);

            return new Fixture(connection, db, stateService);
        }

        public KnowledgePackSyncService CreateService(FakeCloudClient cloud, RSA rsa)
        {
            var pem = rsa.ExportSubjectPublicKeyInfoPem();
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["FullWorthCloud:KnowledgePackPublicKeyBase64"] =
                        Convert.ToBase64String(Encoding.UTF8.GetBytes(pem)),
                    ["FullWorthCloud:KnowledgePackId"] = "fullworth-official",
                    ["FullWorthCloud:KnowledgePackRegion"] = "GLOBAL"
                })
                .Build();

            return new KnowledgePackSyncService(
                Db,
                stateService,
                new CloudInstanceCredentialStore(Db, FieldCipher.Null),
                cloud,
                config,
                NullLogger<KnowledgePackSyncService>.Instance);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class FakeCloudClient(
        KnowledgePackManifest manifest,
        byte[] payload) : IFullWorthCloudClient
    {
        private KnowledgePackManifest currentManifest = manifest;
        private byte[] currentPayload = payload;

        public Uri BaseUri => new("https://cloud.test/");

        public void SetPack(KnowledgePackManifest nextManifest, byte[] nextPayload)
        {
            currentManifest = nextManifest;
            currentPayload = nextPayload;
        }

        public Task<FullWorthCloudRegistrationResult> RegisterAsync(
            Guid instanceId,
            string policyVersion,
            string clientVersion,
            CancellationToken ct) =>
            Task.FromResult(new FullWorthCloudRegistrationResult(
                instanceId,
                "test-secret",
                DateTimeOffset.UtcNow.AddDays(30),
                "active"));

        public Task<FullWorthCloudRegistrationResult> RotateCredentialAsync(
            Guid instanceId,
            string currentCredential,
            CancellationToken ct) =>
            Task.FromResult(new FullWorthCloudRegistrationResult(
                instanceId,
                "rotated",
                DateTimeOffset.UtcNow.AddDays(30),
                "active"));

        public Task<FullWorthCloudBatchResult> SubmitBatchAsync(
            Guid instanceId,
            string instanceCredential,
            IReadOnlyList<FullWorthCloudSubmissionEvent> events,
            CancellationToken ct) =>
            Task.FromResult(new FullWorthCloudBatchResult("unused", 0, 0, 0, []));

        public Task<FullWorthCloudBenchmark?> GetBenchmarkAsync(
            string instanceCredential,
            string metricKey,
            string? currency,
            string? country,
            string? regionBucket,
            string? householdSizeBand,
            string? incomeBand,
            string? ageBand,
            string? observedMonth,
            CancellationToken ct) =>
            Task.FromResult<FullWorthCloudBenchmark?>(null);

        public Task<KnowledgePackManifest?> GetLatestKnowledgePackManifestAsync(
            string instanceCredential,
            string? currentVersion,
            string? region,
            CancellationToken ct) =>
            Task.FromResult<KnowledgePackManifest?>(
                string.Equals(currentVersion, currentManifest.Version, StringComparison.Ordinal)
                    ? null
                    : currentManifest);

        public Task<byte[]> DownloadKnowledgePackAsync(
            string instanceCredential,
            string packId,
            string version,
            CancellationToken ct) =>
            Task.FromResult(currentPayload);
    }
}
