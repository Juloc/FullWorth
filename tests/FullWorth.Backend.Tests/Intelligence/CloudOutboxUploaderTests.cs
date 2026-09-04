using System.Net;
using FullWorth.Backend.Modules.Intelligence;
using FullWorth.Backend.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Intelligence;

public sealed class CloudOutboxUploaderTests
{
    [Fact]
    public async Task Disabled_cloud_performs_zero_network_calls_and_keeps_outbox_frozen()
    {
        await using var fixture = await Fixture.CreateAsync(enabled: false);
        var row = fixture.AddOutbox("{\"subject\":{\"type\":\"product\",\"key\":\"gtin:5449000054539\"}}");
        await fixture.Db.SaveChangesAsync();

        var result = await fixture.Uploader.SyncOnceAsync(CancellationToken.None);

        Assert.Equal(CloudIntelligenceModes.Disabled, result.Mode);
        Assert.Equal(0, fixture.Cloud.RegisterCount);
        Assert.Equal(0, fixture.Cloud.RotateCount);
        Assert.Equal(0, fixture.Cloud.SubmitCount);
        var stored = await fixture.Db.CloudSubmissionOutbox.SingleAsync(x => x.Id == row.Id);
        Assert.Equal(CloudSubmissionStatuses.Queued, stored.Status);
        Assert.Equal(0, stored.AttemptCount);
    }

    [Fact]
    public async Task Enabled_cloud_registers_instance_and_marks_accepted_event_sent()
    {
        await using var fixture = await Fixture.CreateAsync(enabled: true);
        var row = fixture.AddOutbox("{\"subject\":{\"type\":\"product\",\"key\":\"gtin:5449000054539\"}}");
        await fixture.Db.SaveChangesAsync();
        fixture.Cloud.SubmitHandler = (_, _, events) => Task.FromResult(new FullWorthCloudBatchResult(
            "batch-1",
            events.Count,
            0,
            0,
            events.Select(x => new FullWorthCloudBatchEventResult(x.IdempotencyKey, "accepted", null)).ToList()));

        var result = await fixture.Uploader.SyncOnceAsync(CancellationToken.None);

        Assert.True(result.Registered);
        Assert.Equal(1, fixture.Cloud.RegisterCount);
        Assert.Equal(1, fixture.Cloud.SubmitCount);
        Assert.Equal(1, result.Sent);
        // MarkSentAsync updates the row via ExecuteUpdate (bypasses the change tracker), so read the
        // committed value with AsNoTracking rather than the stale tracked instance.
        var stored = await fixture.Db.CloudSubmissionOutbox.AsNoTracking().SingleAsync(x => x.Id == row.Id);
        Assert.Equal(CloudSubmissionStatuses.Sent, stored.Status);
        Assert.NotNull(stored.SentAt);
        Assert.Null(stored.LeaseOwner);
        Assert.Equal(1, stored.AttemptCount);
        Assert.NotNull(await fixture.Db.CloudInstanceCredentials.SingleOrDefaultAsync());
    }

    [Fact]
    public async Task Transient_server_failure_schedules_retry_instead_of_dead_letter()
    {
        await using var fixture = await Fixture.CreateAsync(enabled: true, withCredential: true);
        var row = fixture.AddOutbox("{\"subject\":{\"type\":\"product\",\"key\":\"gtin:5449000054539\"}}");
        await fixture.Db.SaveChangesAsync();
        fixture.Cloud.SubmitHandler = (_, _, _) => Task.FromException<FullWorthCloudBatchResult>(
            new FullWorthCloudException("cloud_server_error", HttpStatusCode.InternalServerError, transient: true));

        var before = DateTimeOffset.UtcNow;
        var result = await fixture.Uploader.SyncOnceAsync(CancellationToken.None);

        Assert.Equal(1, result.Retried);
        Assert.Equal(0, result.DeadLettered);
        var stored = await fixture.Db.CloudSubmissionOutbox.SingleAsync(x => x.Id == row.Id);
        Assert.Equal(CloudSubmissionStatuses.Failed, stored.Status);
        Assert.Equal("cloud_server_error", stored.ErrorCode);
        Assert.NotNull(stored.NextAttemptAt);
        Assert.True(stored.NextAttemptAt > before);
        Assert.Null(stored.LeaseOwner);
    }

    [Fact]
    public async Task Invalid_local_outbox_json_is_dead_lettered_without_submission()
    {
        await using var fixture = await Fixture.CreateAsync(enabled: true, withCredential: true);
        var row = fixture.AddOutbox("{not-json");
        await fixture.Db.SaveChangesAsync();

        var result = await fixture.Uploader.SyncOnceAsync(CancellationToken.None);

        Assert.Equal(0, fixture.Cloud.SubmitCount);
        Assert.Equal(1, result.DeadLettered);
        // MarkDeadLetterAsync updates the row via ExecuteUpdate (bypasses the change tracker), so read
        // the committed value with AsNoTracking rather than the stale tracked instance.
        var stored = await fixture.Db.CloudSubmissionOutbox.AsNoTracking().SingleAsync(x => x.Id == row.Id);
        Assert.Equal(CloudSubmissionStatuses.DeadLetter, stored.Status);
        Assert.Equal("cloud_outbox_invalid_json", stored.ErrorCode);
    }

    [Fact]
    public async Task Unauthorized_submission_rotates_instance_credential_once_and_retries_batch()
    {
        await using var fixture = await Fixture.CreateAsync(enabled: true, withCredential: true);
        fixture.AddOutbox("{\"subject\":{\"type\":\"product\",\"key\":\"gtin:5449000054539\"}}");
        await fixture.Db.SaveChangesAsync();
        var calls = 0;
        fixture.Cloud.SubmitHandler = (_, _, events) =>
        {
            calls++;
            if (calls == 1)
                return Task.FromException<FullWorthCloudBatchResult>(
                    new FullWorthCloudException("cloud_unauthorized", HttpStatusCode.Unauthorized));
            return Task.FromResult(new FullWorthCloudBatchResult(
                "batch-auth",
                events.Count,
                0,
                0,
                events.Select(x => new FullWorthCloudBatchEventResult(x.IdempotencyKey, "accepted", null)).ToList()));
        };

        var result = await fixture.Uploader.SyncOnceAsync(CancellationToken.None);

        Assert.Equal(1, fixture.Cloud.RotateCount);
        Assert.Equal(2, fixture.Cloud.SubmitCount);
        Assert.Equal(1, result.Sent);
        var credential = await fixture.Db.CloudInstanceCredentials.SingleAsync();
        Assert.Contains("rotated-secret", credential.ProtectedSecret, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Projected_outbox_contains_only_whitelisted_derived_fields()
    {
        await using var fixture = await Fixture.CreateAsync(enabled: true);
        var feedback = new IntelligenceFeedbackEvent
        {
            FullWorthSpaceId = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            EventType = "product_category_corrected",
            SubjectType = "purchase-item",
            SubjectId = Guid.NewGuid().ToString("N"),
            SubjectFingerprint = "local-fingerprint",
            OldValueJson = "{\"iban\":\"DE02120300000000202051\",\"description\":\"private raw text\"}",
            NewValueJson = "{\"category\":\"food.groceries\"}",
            Source = "user",
            CloudEligible = true,
            CreatedAt = new DateTimeOffset(2026, 9, 2, 7, 0, 0, TimeSpan.Zero)
        };
        fixture.Db.IntelligenceFeedbackEvents.Add(feedback);
        await fixture.Db.SaveChangesAsync();
        Assert.True(CloudSubmissionProjector.TryCreateGtinSubjectKey("5449000054531", out var subjectKey));

        var outbox = await CloudSubmissionProjector.TryCreateOutboxAsync(
            fixture.Db,
            feedback,
            new CloudFeedbackProjection("product", subjectKey!, "food.groceries"),
            CancellationToken.None);

        Assert.NotNull(outbox);
        Assert.Contains("gtin:5449000054531", outbox!.PayloadJson, StringComparison.Ordinal);
        Assert.Contains("food.groceries", outbox.PayloadJson, StringComparison.Ordinal);
        Assert.DoesNotContain(feedback.UserId.ToString(), outbox.PayloadJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(feedback.FullWorthSpaceId.ToString(), outbox.PayloadJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(feedback.SubjectId, outbox.PayloadJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("iban", outbox.PayloadJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private raw text", outbox.PayloadJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("local-fingerprint", outbox.PayloadJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Retry_backoff_honors_server_retry_after_and_is_bounded()
    {
        var rowId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        Assert.Equal(TimeSpan.FromSeconds(30),
            CloudOutboxUploader.ComputeRetryDelay(1, TimeSpan.FromSeconds(1), "cloud_rate_limited", rowId));
        Assert.Equal(TimeSpan.FromHours(24),
            CloudOutboxUploader.ComputeRetryDelay(1, TimeSpan.FromDays(3), "cloud_rate_limited", rowId));
        Assert.True(CloudOutboxUploader.ComputeRetryDelay(8, null, "cloud_server_error", rowId) <= TimeSpan.FromHours(6.01));
        Assert.True(CloudOutboxUploader.ComputeRetryDelay(1, null, "cloud_entitlement_denied", rowId) >= TimeSpan.FromHours(1));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;

        private Fixture(
            SqliteConnection connection,
            IntelligenceDbContext db,
            CloudConnectionState state,
            FakeCloudClient cloud,
            CloudInstanceCredentialStore credentialStore,
            CloudOutboxUploader uploader)
        {
            this.connection = connection;
            Db = db;
            State = state;
            Cloud = cloud;
            CredentialStore = credentialStore;
            Uploader = uploader;
        }

        public IntelligenceDbContext Db { get; }
        public CloudConnectionState State { get; }
        public FakeCloudClient Cloud { get; }
        public CloudInstanceCredentialStore CredentialStore { get; }
        public CloudOutboxUploader Uploader { get; }

        public static async Task<Fixture> CreateAsync(bool enabled, bool withCredential = false)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new IntelligenceDbContext(new DbContextOptionsBuilder<IntelligenceDbContext>()
                .UseSqlite(connection)
                .Options);
            await db.Database.EnsureCreatedAsync();

            var state = new CloudConnectionState
            {
                ScopeKey = CloudConnectionState.InstanceScopeKey,
                Mode = enabled ? CloudIntelligenceModes.Enabled : CloudIntelligenceModes.Disabled
            };
            db.CloudConnectionStates.Add(state);
            if (enabled)
            {
                db.CloudIntelligenceConsents.Add(new CloudIntelligenceConsent
                {
                    InstanceId = state.InstanceId,
                    AcceptedByUserId = Guid.NewGuid(),
                    PolicyVersion = CloudIntelligencePolicy.CurrentVersion,
                    ClientVersion = "test-client",
                    Locale = "de-DE"
                });
            }
            await db.SaveChangesAsync();

            var cloud = new FakeCloudClient();
            var credentialStore = new CloudInstanceCredentialStore(db, FieldCipher.Null);
            if (withCredential)
            {
                await credentialStore.SaveAsync(new FullWorthCloudRegistrationResult(
                    state.InstanceId,
                    "existing-secret",
                    DateTimeOffset.UtcNow.AddDays(30),
                    "active"), CancellationToken.None);
            }
            var uploader = new CloudOutboxUploader(db, credentialStore, cloud);
            return new Fixture(connection, db, state, cloud, credentialStore, uploader);
        }

        public CloudSubmissionOutbox AddOutbox(string payload)
        {
            var row = new CloudSubmissionOutbox
            {
                InstanceId = State.InstanceId,
                IdempotencyKey = $"test:{Guid.NewGuid():N}",
                SchemaVersion = CloudIntelligencePolicy.SubmissionSchemaVersion,
                EventType = "product_category_corrected",
                PayloadJson = payload,
                Status = CloudSubmissionStatuses.Queued
            };
            Db.CloudSubmissionOutbox.Add(row);
            return row;
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class FakeCloudClient : IFullWorthCloudClient
    {
        public Uri BaseUri { get; } = new("https://cloud.test/");
        public int RegisterCount { get; private set; }
        public int RotateCount { get; private set; }
        public int SubmitCount { get; private set; }
        public Func<Guid, string, IReadOnlyList<FullWorthCloudSubmissionEvent>, Task<FullWorthCloudBatchResult>>? SubmitHandler { get; set; }

        public Task<FullWorthCloudRegistrationResult> RegisterAsync(
            Guid instanceId,
            string policyVersion,
            string clientVersion,
            CancellationToken ct)
        {
            RegisterCount++;
            return Task.FromResult(new FullWorthCloudRegistrationResult(
                instanceId,
                "registered-secret",
                DateTimeOffset.UtcNow.AddDays(30),
                "active"));
        }

        public Task<FullWorthCloudRegistrationResult> RotateCredentialAsync(
            Guid instanceId,
            string currentCredential,
            CancellationToken ct)
        {
            RotateCount++;
            return Task.FromResult(new FullWorthCloudRegistrationResult(
                instanceId,
                "rotated-secret",
                DateTimeOffset.UtcNow.AddDays(30),
                "active"));
        }

        public Task<FullWorthCloudBatchResult> SubmitBatchAsync(
            Guid instanceId,
            string instanceCredential,
            IReadOnlyList<FullWorthCloudSubmissionEvent> events,
            CancellationToken ct)
        {
            SubmitCount++;
            return SubmitHandler?.Invoke(instanceId, instanceCredential, events)
                   ?? Task.FromResult(new FullWorthCloudBatchResult(
                       "batch-default",
                       events.Count,
                       0,
                       0,
                       events.Select(x => new FullWorthCloudBatchEventResult(x.IdempotencyKey, "accepted", null)).ToList()));
        }
    }
}
