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
    public async Task Signed_pack_installs_self_contained_brand_assets_and_aliases()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var rsa = RSA.Create(2048);
        var svg = Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 10 10\"><path d=\"M0 0h10v10H0z\"/></svg>");
        var hash = Convert.ToHexString(SHA256.HashData(svg)).ToLowerInvariant();
        var pack = BuildPack(
            rsa,
            "2026.09.06-11",
            "VATTENFALL EUROPE SALES",
            "housing.electricity",
            brandAssets:
            [
                new KnowledgePackBrandAssetPayload(
                    "vattenfall", "Vattenfall", "vattenfall", "image/svg+xml",
                    Convert.ToBase64String(svg), hash,
                    "test", "https://example.test/vattenfall.svg", "test provenance")
            ],
            brandAliases:
            [
                new KnowledgePackBrandAliasPayload("VATTENFALL", "vattenfall", "DE")
            ]);

        var result = await fixture.CreateService(
                new FakeCloudClient(pack.Manifest, pack.Payload), rsa)
            .SyncOnceAsync(CancellationToken.None);

        Assert.Equal("installed", result.Status);
        var asset = await fixture.Db.OfficialBrandAssets.SingleAsync();
        Assert.Equal("vattenfall", asset.BrandKey);
        Assert.Equal(hash, asset.ContentSha256);
        Assert.Equal(Convert.ToBase64String(svg), asset.ContentBase64);
        var alias = await fixture.Db.OfficialBrandAliases.SingleAsync();
        Assert.Equal("VATTENFALL", alias.AliasKey);
        Assert.Equal("vattenfall", alias.BrandKey);
    }

    [Fact]
    public async Task Brand_asset_hash_mismatch_rejects_whole_pack()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var rsa = RSA.Create(2048);
        var svg = Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"/>");
        var pack = BuildPack(
            rsa,
            "2026.09.06-12",
            "TEST",
            "other",
            brandAssets:
            [
                new KnowledgePackBrandAssetPayload(
                    "test", "Test", "test", "image/svg+xml",
                    Convert.ToBase64String(svg), new string('0', 64),
                    null, null, null)
            ],
            brandAliases:
            [
                new KnowledgePackBrandAliasPayload("TEST", "test", null)
            ]);

        var result = await fixture.CreateService(
                new FakeCloudClient(pack.Manifest, pack.Payload), rsa)
            .SyncOnceAsync(CancellationToken.None);

        Assert.Equal("failed", result.Status);
        Assert.Equal("knowledge_pack_brand_asset_hash_mismatch", result.ErrorCode);
        Assert.Empty(await fixture.Db.OfficialBrandAssets.ToListAsync());
        Assert.Empty(await fixture.Db.OfficialBrandAliases.ToListAsync());
    }

    [Fact]
    public async Task Active_svg_content_in_brand_pack_is_rejected()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var rsa = RSA.Create(2048);
        var svg = Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>");
        var hash = Convert.ToHexString(SHA256.HashData(svg)).ToLowerInvariant();
        var pack = BuildPack(
            rsa,
            "2026.09.06-13",
            "TEST",
            "other",
            brandAssets:
            [
                new KnowledgePackBrandAssetPayload(
                    "test", "Test", "test", "image/svg+xml",
                    Convert.ToBase64String(svg), hash,
                    null, null, null)
            ],
            brandAliases:
            [
                new KnowledgePackBrandAliasPayload("TEST", "test", null)
            ]);

        var result = await fixture.CreateService(
                new FakeCloudClient(pack.Manifest, pack.Payload), rsa)
            .SyncOnceAsync(CancellationToken.None);

        Assert.Equal("failed", result.Status);
        Assert.Equal("knowledge_pack_brand_svg_unsafe", result.ErrorCode);
        Assert.Empty(await fixture.Db.OfficialBrandAssets.ToListAsync());
    }

    [Fact]
    public async Task Signed_pack_installs_ontology_and_applies_category_redirect()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var rsa = RSA.Create(2048);
        const string oldKey = "dynamic.category.strom.1234567890";
        var pack = BuildPack(
            rsa,
            "2026.09.06-3",
            "ENBW ENERGIE",
            oldKey,
            [
                new KnowledgePackOntologyEntityPayload(
                    "category", oldKey, "Strom", null, "merged", 2),
                new KnowledgePackOntologyEntityPayload(
                    "category", "housing.electricity", "Electricity", "housing", "active", 3)
            ],
            [
                new KnowledgePackOntologyAliasPayload(
                    "category", "housing.electricity", "Strom", "STROM", "de", "DE", 0.95m, 25, 2)
            ],
            [
                new KnowledgePackOntologyRedirectPayload(
                    "category", oldKey, "housing.electricity", 2)
            ]);
        var cloud = new FakeCloudClient(pack.Manifest, pack.Payload);

        var result = await fixture.CreateService(cloud, rsa)
            .SyncOnceAsync(CancellationToken.None);

        Assert.Equal("installed", result.Status);
        Assert.Equal("housing.electricity",
            (await fixture.Db.OfficialMerchantMappings.SingleAsync()).CategoryKey);
        Assert.Equal(2, await fixture.Db.OfficialOntologyEntities.CountAsync());
        Assert.Equal("STROM",
            (await fixture.Db.OfficialOntologyAliases.SingleAsync()).NormalizedAlias);
        Assert.Equal("housing.electricity",
            (await fixture.Db.OfficialOntologyRedirects.SingleAsync()).ToCanonicalKey);
    }

    [Fact]
    public async Task Signed_pack_installs_provider_product_ontology_without_cross_type_category_redirects()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var rsa = RSA.Create(2048);

        var pack = BuildPack(
            rsa,
            "2026.09.06-14",
            "REWE",
            "food.groceries",
            providerOntologyEntities:
            [
                new KnowledgePackOntologyEntityPayload(
                    "provider", "food.groceries", "Legacy provider key collision", null, "merged", 2),
                new KnowledgePackOntologyEntityPayload(
                    "provider", "provider.telekom", "Deutsche Telekom", null, "active", 3)
            ],
            providerOntologyAliases:
            [
                new KnowledgePackOntologyAliasPayload(
                    "provider", "provider.telekom", "Telekom", "TELEKOM", "de", "DE", 0.99m, 25, 2)
            ],
            providerOntologyRedirects:
            [
                new KnowledgePackOntologyRedirectPayload(
                    "provider", "food.groceries", "provider.telekom", 2)
            ],
            productOntologyEntities:
            [
                new KnowledgePackOntologyEntityPayload(
                    "product", "product.coca-cola-zero", "Coca-Cola Zero", null, "active", 2)
            ],
            productOntologyAliases:
            [
                new KnowledgePackOntologyAliasPayload(
                    "product", "product.coca-cola-zero", "Coca Cola Zero", "COCA COLA ZERO", "de", "DE", 0.98m, 20, 2)
            ],
            productOntologyRedirects: []);

        var result = await fixture.CreateService(
                new FakeCloudClient(pack.Manifest, pack.Payload), rsa)
            .SyncOnceAsync(CancellationToken.None);

        Assert.Equal("installed", result.Status);

        // Provider redirects are scoped by EntityType and can never rewrite merchant category keys.
        Assert.Equal(
            "food.groceries",
            (await fixture.Db.OfficialMerchantMappings.SingleAsync()).CategoryKey);

        var entities = await fixture.Db.OfficialOntologyEntities.AsNoTracking().ToListAsync();
        Assert.Equal(3, entities.Count);
        Assert.Equal(2, entities.Count(x => x.EntityType == "provider"));
        Assert.Single(entities.Where(x => x.EntityType == "product"));

        var aliases = await fixture.Db.OfficialOntologyAliases.AsNoTracking().ToListAsync();
        Assert.Contains(aliases, x =>
            x.EntityType == "provider" &&
            x.CanonicalKey == "provider.telekom" &&
            x.NormalizedAlias == "TELEKOM");
        Assert.Contains(aliases, x =>
            x.EntityType == "product" &&
            x.CanonicalKey == "product.coca-cola-zero" &&
            x.NormalizedAlias == "COCA COLA ZERO");

        var redirect = await fixture.Db.OfficialOntologyRedirects.SingleAsync();
        Assert.Equal("provider", redirect.EntityType);
        Assert.Equal("provider.telekom", redirect.ToCanonicalKey);
    }

    [Fact]
    public async Task Signed_pack_with_redirect_cycle_is_rejected()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var rsa = RSA.Create(2048);
        var pack = BuildPack(
            rsa,
            "2026.09.06-4",
            "TEST",
            "dynamic.category.a.1234567890",
            [
                new KnowledgePackOntologyEntityPayload(
                    "category", "dynamic.category.a.1234567890", "A", null, "merged", 2),
                new KnowledgePackOntologyEntityPayload(
                    "category", "dynamic.category.b.1234567890", "B", null, "merged", 2)
            ],
            null,
            [
                new KnowledgePackOntologyRedirectPayload(
                    "category", "dynamic.category.a.1234567890", "dynamic.category.b.1234567890", 2),
                new KnowledgePackOntologyRedirectPayload(
                    "category", "dynamic.category.b.1234567890", "dynamic.category.a.1234567890", 2)
            ]);

        var result = await fixture.CreateService(
                new FakeCloudClient(pack.Manifest, pack.Payload), rsa)
            .SyncOnceAsync(CancellationToken.None);

        Assert.Equal("failed", result.Status);
        Assert.Equal("knowledge_pack_ontology_redirect_cycle", result.ErrorCode);
        Assert.Empty(await fixture.Db.OfficialOntologyRedirects.ToListAsync());
    }

    [Fact]
    public async Task Signed_pack_with_redirect_to_missing_active_target_is_rejected()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var rsa = RSA.Create(2048);
        var pack = BuildPack(
            rsa,
            "2026.09.06-5",
            "TEST",
            "dynamic.category.a.1234567890",
            [
                new KnowledgePackOntologyEntityPayload(
                    "category", "dynamic.category.a.1234567890", "A", null, "merged", 2)
            ],
            null,
            [
                new KnowledgePackOntologyRedirectPayload(
                    "category", "dynamic.category.a.1234567890", "housing.electricity", 2)
            ]);

        var result = await fixture.CreateService(
                new FakeCloudClient(pack.Manifest, pack.Payload), rsa)
            .SyncOnceAsync(CancellationToken.None);

        Assert.Equal("failed", result.Status);
        Assert.Equal("knowledge_pack_ontology_redirect_target_invalid", result.ErrorCode);
        Assert.Empty(await fixture.Db.OfficialOntologyEntities.ToListAsync());
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
        string categoryKey,
        IReadOnlyList<KnowledgePackOntologyEntityPayload>? ontologyEntities = null,
        IReadOnlyList<KnowledgePackOntologyAliasPayload>? ontologyAliases = null,
        IReadOnlyList<KnowledgePackOntologyRedirectPayload>? ontologyRedirects = null,
        IReadOnlyList<KnowledgePackBrandAssetPayload>? brandAssets = null,
        IReadOnlyList<KnowledgePackBrandAliasPayload>? brandAliases = null,
        IReadOnlyList<KnowledgePackOntologyEntityPayload>? providerOntologyEntities = null,
        IReadOnlyList<KnowledgePackOntologyAliasPayload>? providerOntologyAliases = null,
        IReadOnlyList<KnowledgePackOntologyRedirectPayload>? providerOntologyRedirects = null,
        IReadOnlyList<KnowledgePackOntologyEntityPayload>? productOntologyEntities = null,
        IReadOnlyList<KnowledgePackOntologyAliasPayload>? productOntologyAliases = null,
        IReadOnlyList<KnowledgePackOntologyRedirectPayload>? productOntologyRedirects = null)
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
            ],
            ontologyEntities,
            ontologyAliases,
            ontologyRedirects,
            brandAssets,
            brandAliases,
            providerOntologyEntities,
            providerOntologyAliases,
            providerOntologyRedirects,
            productOntologyEntities,
            productOntologyAliases,
            productOntologyRedirects);

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
