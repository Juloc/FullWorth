using System.Diagnostics;
using FullWorth.Backend.Modules.Intelligence;
using FullWorth.Backend.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace FullWorth.Backend.Tests.Intelligence;

/// <summary>
/// Six read endpoints registered with the Cloud inline, inside the GET a page load is waiting on, and
/// registration goes through the Cloud client's 45-second HTTP timeout. So an unreachable Cloud stalled
/// the page for up to 45 seconds — and every further request started its own attempt and stalled again.
/// </summary>
public sealed class CloudCredentialAcquisitionTests
{
    [Fact]
    public async Task A_stored_credential_is_returned_without_contacting_the_cloud()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Cloud.Behaviour = CloudBehaviour.Succeed;
        _ = await fixture.Acquisition.TryGetAsync(fixture.InstanceId, CancellationToken.None);

        var (secret, error) = await fixture.Acquisition.TryGetAsync(fixture.InstanceId, CancellationToken.None);

        Assert.Equal("test-secret", secret);
        Assert.Null(error);
        Assert.Equal(1, fixture.Cloud.RegisterCalls);
    }

    // This is the stall. The fake never answers, so without a budget the call would hang.
    [Fact]
    public async Task An_unreachable_cloud_gives_up_inside_the_budget_instead_of_hanging()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Cloud.Behaviour = CloudBehaviour.Hang;
        var stopwatch = Stopwatch.StartNew();

        var (secret, error) = await fixture.Acquisition.TryGetAsync(fixture.InstanceId, CancellationToken.None);

        Assert.Null(secret);
        Assert.Equal("cloud_timeout", error);
        Assert.True(
            stopwatch.Elapsed < CloudCredentialAcquisition.Budget + TimeSpan.FromSeconds(5),
            $"gave up after {stopwatch.Elapsed}, budget is {CloudCredentialAcquisition.Budget}");
    }

    // And the second page load must not pay the budget again.
    [Fact]
    public async Task After_a_failure_the_next_request_answers_immediately()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Cloud.Behaviour = CloudBehaviour.Fail;
        var (_, first) = await fixture.Acquisition.TryGetAsync(fixture.InstanceId, CancellationToken.None);
        Assert.Equal("cloud_refused", first);

        var stopwatch = Stopwatch.StartNew();
        var (secret, error) = await fixture.Acquisition.TryGetAsync(fixture.InstanceId, CancellationToken.None);

        Assert.Null(secret);
        Assert.Equal("cloud_refused", error);
        Assert.Equal(1, fixture.Cloud.RegisterCalls);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), $"took {stopwatch.Elapsed}");
    }

    // A failure must not become permanent: the cooldown expires and the reason is reported meanwhile.
    [Fact]
    public void The_cooldown_reports_the_reason_and_then_expires()
    {
        var cooldown = new CloudRegistrationCooldown();
        var now = DateTimeOffset.UtcNow;

        Assert.Null(cooldown.ActiveReason(now));
        cooldown.Fail(now.AddMinutes(1), "cloud_refused");
        Assert.Equal("cloud_refused", cooldown.ActiveReason(now));
        Assert.Null(cooldown.ActiveReason(now.AddMinutes(2)));

        cooldown.Fail(now.AddMinutes(1), "cloud_refused");
        cooldown.Clear();
        Assert.Null(cooldown.ActiveReason(now));
    }

    // The budget only helps if it is well below the timeout it exists to avoid.
    [Fact]
    public void The_budget_is_far_below_the_clients_http_timeout()
    {
        Assert.True(CloudCredentialAcquisition.Budget < TimeSpan.FromSeconds(15));
        Assert.True(CloudCredentialAcquisition.Budget > TimeSpan.Zero);
    }

    private enum CloudBehaviour { Succeed, Fail, Hang }

    private sealed class Fixture : IAsyncDisposable
    {
        private SqliteConnection connection = null!;
        private IntelligenceDbContext db = null!;

        public Guid InstanceId { get; private set; }
        public FakeCloud Cloud { get; } = new();
        public CloudCredentialAcquisition Acquisition { get; private set; } = null!;

        public static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture();
            fixture.connection = new SqliteConnection("Data Source=:memory:");
            await fixture.connection.OpenAsync();
            var options = new DbContextOptionsBuilder<IntelligenceDbContext>().UseSqlite(fixture.connection).Options;
            fixture.db = new IntelligenceDbContext(options);
            await fixture.db.Database.EnsureCreatedAsync();

            var state = new CloudIntelligenceStateService(fixture.db);
            var enabled = await state.EnableAsync(
                Guid.NewGuid(),
                new EnableCloudIntelligenceRequest(CloudIntelligencePolicy.CurrentVersion, "de", "test"),
                CancellationToken.None);
            fixture.InstanceId = enabled.InstanceId;
            fixture.Acquisition = new CloudCredentialAcquisition(
                new CloudInstanceCredentialStore(fixture.db, FieldCipher.Null),
                fixture.Cloud,
                state,
                new CloudRegistrationCooldown(),
                NullLogger<CloudCredentialAcquisition>.Instance);
            return fixture;
        }

        public async ValueTask DisposeAsync()
        {
            await db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class FakeCloud : IFullWorthCloudClient
    {
        public Uri BaseUri => new("https://cloud.test/");
        public CloudBehaviour Behaviour { get; set; } = CloudBehaviour.Succeed;
        public int RegisterCalls { get; private set; }

        public async Task<FullWorthCloudRegistrationResult> RegisterAsync(
            Guid instanceId,
            string policyVersion,
            string clientVersion,
            string? currentCredential,
            CancellationToken ct)
        {
            RegisterCalls++;
            switch (Behaviour)
            {
                case CloudBehaviour.Fail:
                    throw new FullWorthCloudException("cloud_refused");
                case CloudBehaviour.Hang:
                    // Never answers on its own; only the caller's budget ends this.
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                    break;
            }

            return new FullWorthCloudRegistrationResult(
                instanceId, "test-secret", DateTimeOffset.UtcNow.AddDays(30), "active");
        }

        public Task<FullWorthCloudRegistrationResult> RotateCredentialAsync(
            Guid instanceId, string currentCredential, CancellationToken ct) =>
            Task.FromResult(new FullWorthCloudRegistrationResult(
                instanceId, "rotated-secret", DateTimeOffset.UtcNow.AddDays(30), "active"));

        public Task<FullWorthCloudBatchResult> SubmitBatchAsync(
            Guid instanceId,
            string instanceCredential,
            IReadOnlyList<FullWorthCloudSubmissionEvent> events,
            CancellationToken ct) =>
            Task.FromResult(new FullWorthCloudBatchResult("batch", 0, 0, 0, []));

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
            CancellationToken ct) => Task.FromResult<FullWorthCloudBenchmark?>(null);

        public Task<KnowledgePackManifest?> GetLatestKnowledgePackManifestAsync(
            string instanceCredential,
            string? currentVersion,
            string? region,
            CancellationToken ct) => Task.FromResult<KnowledgePackManifest?>(null);

        public Task<byte[]> DownloadKnowledgePackAsync(
            string instanceCredential,
            string packId,
            string version,
            CancellationToken ct) => Task.FromResult(Array.Empty<byte>());
    }
}
