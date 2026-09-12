using System.Security.Cryptography;
using System.Text;
using FullWorth.Backend.Modules.Intelligence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace FullWorth.Backend.Tests.Intelligence;

/// <summary>
/// A database and a Cloud that answers one question: which key do you sign packs with?
/// </summary>
internal sealed class KnowledgePackTrustFixture(SqliteConnection connection, IntelligenceDbContext db)
    : IAsyncDisposable
{
    public IntelligenceDbContext Db { get; } = db;

    public static async Task<KnowledgePackTrustFixture> CreateAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<IntelligenceDbContext>().UseSqlite(connection).Options;
        var db = new IntelligenceDbContext(options);
        await db.Database.EnsureCreatedAsync();
        return new KnowledgePackTrustFixture(connection, db);
    }

    public TrustFakeCloudClient Cloud(Uri? baseUri = null) =>
        new(baseUri ?? new Uri("https://cloud.test/"));

    public TrustFakeCloudClient CloudOffering(RSA rsa, Uri? baseUri = null) =>
        new(baseUri ?? new Uri("https://cloud.test/")) { OfferedPublicKey = KeyOf(rsa) };

    public static FullWorthCloudPublicKey KeyOf(RSA rsa)
    {
        var pem = rsa.ExportSubjectPublicKeyInfoPem();
        return new FullWorthCloudPublicKey(
            "RSA-PSS-SHA256",
            KnowledgePackTrustStore.FingerprintOf(pem)!,
            pem);
    }

    /// <param name="configured">
    /// An explicitly configured key, as a deployment that states one would have. Null is the normal
    /// case now: nothing configured, so the installation has to obtain a key by itself.
    /// </param>
    public KnowledgePackTrustStore Store(IFullWorthCloudClient cloud, RSA? configured = null)
    {
        var settings = new Dictionary<string, string?>();
        if (configured is not null)
            settings["FullWorthCloud:KnowledgePackPublicKeyBase64"] = Convert.ToBase64String(
                Encoding.UTF8.GetBytes(configured.ExportSubjectPublicKeyInfoPem()));

        return new KnowledgePackTrustStore(
            Db,
            cloud,
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build(),
            NullLogger<KnowledgePackTrustStore>.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        await Db.DisposeAsync();
        await connection.DisposeAsync();
    }
}

/// <summary>
/// Answers the key question and nothing else. Every other call throws rather than returning a polite
/// empty value — a test that reaches one of them is testing something this fake does not model.
/// </summary>
internal sealed class TrustFakeCloudClient(Uri baseUri) : IFullWorthCloudClient
{
    public Uri BaseUri { get; } = baseUri;
    public FullWorthCloudPublicKey? OfferedPublicKey { get; set; }
    public int PublicKeyRequestCount { get; private set; }

    public Task<FullWorthCloudPublicKey?> GetKnowledgePackPublicKeyAsync(
        string instanceCredential,
        CancellationToken ct)
    {
        PublicKeyRequestCount++;
        return Task.FromResult(OfferedPublicKey);
    }

    public Task<FullWorthCloudRegistrationResult> RegisterAsync(
        Guid instanceId, string policyVersion, string clientVersion, string? currentCredential, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<FullWorthCloudRegistrationResult> RotateCredentialAsync(
        Guid instanceId, string currentCredential, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<FullWorthCloudBatchResult> SubmitBatchAsync(
        Guid instanceId,
        string instanceCredential,
        IReadOnlyList<FullWorthCloudSubmissionEvent> events,
        CancellationToken ct) =>
        throw new NotSupportedException();

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
        throw new NotSupportedException();

    public Task<KnowledgePackManifest?> GetLatestKnowledgePackManifestAsync(
        string instanceCredential, string? currentVersion, string? region, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<byte[]> DownloadKnowledgePackAsync(
        string instanceCredential, string packId, string version, CancellationToken ct) =>
        throw new NotSupportedException();
}
