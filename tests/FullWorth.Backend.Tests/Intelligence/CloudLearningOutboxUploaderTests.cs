using System.Net;
using FullWorth.Backend.Modules.Intelligence;
using FullWorth.Backend.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace FullWorth.Backend.Tests.Intelligence;

public sealed class CloudLearningOutboxUploaderTests
{
    [Fact]
    public async Task Registers_submits_and_marks_accepted_event_sent()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<IntelligenceDbContext>().UseSqlite(connection).Options;
        await using var db = new IntelligenceDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var stateService = new CloudIntelligenceStateService(db);
        var enabled = await stateService.EnableAsync(
            Guid.NewGuid(),
            new EnableCloudIntelligenceRequest(CloudIntelligencePolicy.CurrentVersion, "de", "test"),
            CancellationToken.None);

        var row = new CloudSubmissionOutbox
        {
            InstanceId = enabled.InstanceId,
            IdempotencyKey = "feedback:" + Guid.NewGuid().ToString("N"),
            EventType = "merchant_mapping",
            PayloadJson = """{"alias":"REWE","mapping":{"categoryKey":"food.groceries","categoryAlias":"Lebensmittel","categoryLocale":"de","categoryIsCustom":false},"direction":"expense","action":"corrected","confidence":1,"observedMonth":"2026-09"}"""
        };
        db.CloudSubmissionOutbox.Add(row);
        await db.SaveChangesAsync();

        var cloud = new FakeCloudClient();
        var uploader = new CloudLearningOutboxUploader(
            db,
            stateService,
            new CloudInstanceCredentialStore(db, FieldCipher.Null),
            cloud,
            NullLogger<CloudLearningOutboxUploader>.Instance);

        var sent = await uploader.UploadOnceAsync(CancellationToken.None);

        Assert.Equal(1, sent);
        Assert.Equal(1, cloud.RegisterCalls);
        Assert.Equal(1, cloud.SubmitCalls);
        var saved = await db.CloudSubmissionOutbox.SingleAsync();
        Assert.Equal(CloudSubmissionStatuses.Sent, saved.Status);
        Assert.NotNull(saved.SentAt);

        Assert.Equal(0, await uploader.UploadOnceAsync(CancellationToken.None));
        Assert.Equal(1, cloud.SubmitCalls);
    }

    private sealed class FakeCloudClient : IFullWorthCloudClient
    {
        public Uri BaseUri => new("https://cloud.test/");
        public int RegisterCalls { get; private set; }
        public int SubmitCalls { get; private set; }

        public Task<FullWorthCloudRegistrationResult> RegisterAsync(
            Guid instanceId,
            string policyVersion,
            string clientVersion,
            CancellationToken ct)
        {
            RegisterCalls++;
            return Task.FromResult(new FullWorthCloudRegistrationResult(
                instanceId,
                "test-secret",
                DateTimeOffset.UtcNow.AddDays(30),
                "active"));
        }

        public Task<FullWorthCloudRegistrationResult> RotateCredentialAsync(
            Guid instanceId,
            string currentCredential,
            CancellationToken ct) =>
            Task.FromResult(new FullWorthCloudRegistrationResult(
                instanceId,
                "rotated-secret",
                DateTimeOffset.UtcNow.AddDays(30),
                "active"));

        public Task<FullWorthCloudBatchResult> SubmitBatchAsync(
            Guid instanceId,
            string instanceCredential,
            IReadOnlyList<FullWorthCloudSubmissionEvent> events,
            CancellationToken ct)
        {
            SubmitCalls++;
            return Task.FromResult(new FullWorthCloudBatchResult(
                "batch-test",
                events.Count,
                0,
                0,
                events.Select(x => new FullWorthCloudBatchEventResult(
                    x.IdempotencyKey, "accepted", null)).ToList()));
        }

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
    }
}
