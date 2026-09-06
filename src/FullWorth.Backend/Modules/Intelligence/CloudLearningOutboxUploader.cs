using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Intelligence;

public sealed class CloudLearningOutboxUploader(
    IntelligenceDbContext db,
    CloudIntelligenceStateService stateService,
    CloudInstanceCredentialStore credentialStore,
    IFullWorthCloudClient cloud,
    ILogger<CloudLearningOutboxUploader> logger)
{
    private const int BatchSize = 100;

    public async Task<int> UploadOnceAsync(CancellationToken ct)
    {
        if (!await stateService.HasCurrentActiveConsentAsync(ct)) return 0;
        var state = await stateService.GetEnabledStateAsync(ct);
        if (state is null) return 0;

        var now = DateTimeOffset.UtcNow;
        var leaseOwner = $"cloud-uploader:{Environment.MachineName}:{Guid.NewGuid():N}";
        var candidateIds = await db.CloudSubmissionOutbox.AsNoTracking()
            .Where(x => (x.Status == CloudSubmissionStatuses.Queued || x.Status == CloudSubmissionStatuses.Failed) &&
                        (!x.NextAttemptAt.HasValue || x.NextAttemptAt <= now) &&
                        (!x.LeaseExpiresAt.HasValue || x.LeaseExpiresAt < now))
            .OrderBy(x => x.CreatedAt)
            .Select(x => x.Id)
            .Take(BatchSize)
            .ToListAsync(ct);
        if (candidateIds.Count == 0) return 0;

        var leaseUntil = now.AddMinutes(2);
        await db.CloudSubmissionOutbox
            .Where(x => candidateIds.Contains(x.Id) &&
                        (x.Status == CloudSubmissionStatuses.Queued || x.Status == CloudSubmissionStatuses.Failed) &&
                        (!x.LeaseExpiresAt.HasValue || x.LeaseExpiresAt < now))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, CloudSubmissionStatuses.Sending)
                .SetProperty(x => x.LeaseOwner, leaseOwner)
                .SetProperty(x => x.LeaseExpiresAt, leaseUntil)
                .SetProperty(x => x.LastAttemptAt, now)
                .SetProperty(x => x.AttemptCount, x => x.AttemptCount + 1), ct);

        var rows = await db.CloudSubmissionOutbox
            .Where(x => x.LeaseOwner == leaseOwner && x.Status == CloudSubmissionStatuses.Sending)
            .OrderBy(x => x.CreatedAt)
            .ToListAsync(ct);
        if (rows.Count == 0) return 0;

        try
        {
            var secret = await credentialStore.GetSecretAsync(state.InstanceId, ct);
            if (string.IsNullOrWhiteSpace(secret))
            {
                var registration = await cloud.RegisterAsync(
                    state.InstanceId,
                    CloudIntelligencePolicy.CurrentVersion,
                    ClientVersion(),
                    ct);
                await credentialStore.SaveAsync(registration, ct);
                secret = registration.Credential;
                await stateService.SetTransportStatusAsync(
                    state.InstanceId, null, registration.EntitlementStatus,
                    DateTimeOffset.UtcNow, null, ct);
            }

            var events = rows.Select(row =>
            {
                using var doc = JsonDocument.Parse(row.PayloadJson);
                return new FullWorthCloudSubmissionEvent(
                    row.IdempotencyKey,
                    row.SchemaVersion,
                    row.EventType,
                    doc.RootElement.Clone());
            }).ToList();

            var result = await cloud.SubmitBatchAsync(state.InstanceId, secret, events, ct);
            var perEvent = result.Events.ToDictionary(x => x.IdempotencyKey, StringComparer.Ordinal);
            var sent = 0;

            foreach (var row in rows)
            {
                if (!perEvent.TryGetValue(row.IdempotencyKey, out var eventResult))
                {
                    Retry(row, "cloud_missing_event_result", now);
                    continue;
                }

                switch (eventResult.Status.Trim().ToLowerInvariant())
                {
                    case "accepted":
                    case "duplicate":
                        row.Status = CloudSubmissionStatuses.Sent;
                        row.SentAt = now;
                        row.ErrorCode = null;
                        row.NextAttemptAt = null;
                        sent++;
                        break;
                    case "rejected":
                        row.Status = CloudSubmissionStatuses.DeadLetter;
                        row.ErrorCode = Trim(eventResult.ErrorCode, 120) ?? "cloud_rejected";
                        row.NextAttemptAt = null;
                        break;
                    default:
                        Retry(row, Trim(eventResult.ErrorCode, 120) ?? "cloud_unknown_event_status", now);
                        break;
                }
                row.LeaseOwner = null;
                row.LeaseExpiresAt = null;
            }

            await db.SaveChangesAsync(ct);
            await credentialStore.MarkUsedAsync(state.InstanceId, ct);
            await stateService.SetTransportStatusAsync(
                state.InstanceId, null, null, null, now, ct);
            return sent;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (FullWorthCloudException ex)
        {
            if (ex.StatusCode is System.Net.HttpStatusCode.Unauthorized)
                await credentialStore.DeleteAsync(state.InstanceId, CancellationToken.None);

            foreach (var row in rows)
            {
                Retry(row, ex.ErrorCode, now, ex.RetryAfter);
                row.LeaseOwner = null;
                row.LeaseExpiresAt = null;
            }
            await db.SaveChangesAsync(CancellationToken.None);
            await SafeSetErrorAsync(state.InstanceId, ex.ErrorCode);
            return 0;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "FullWorth Cloud learning upload failed; events remain queued.");
            foreach (var row in rows)
            {
                Retry(row, "cloud_upload_failed", now);
                row.LeaseOwner = null;
                row.LeaseExpiresAt = null;
            }
            await db.SaveChangesAsync(CancellationToken.None);
            await SafeSetErrorAsync(state.InstanceId, "cloud_upload_failed");
            return 0;
        }
    }

    private static void Retry(
        CloudSubmissionOutbox row,
        string errorCode,
        DateTimeOffset now,
        TimeSpan? retryAfter = null)
    {
        if (row.AttemptCount >= 12)
        {
            row.Status = CloudSubmissionStatuses.DeadLetter;
            row.NextAttemptAt = null;
        }
        else
        {
            row.Status = CloudSubmissionStatuses.Failed;
            var seconds = Math.Min(3600, 15 * Math.Pow(2, Math.Min(8, row.AttemptCount)));
            row.NextAttemptAt = now.Add(retryAfter ?? TimeSpan.FromSeconds(seconds));
        }
        row.ErrorCode = Trim(errorCode, 120);
    }

    private async Task SafeSetErrorAsync(Guid instanceId, string errorCode)
    {
        try
        {
            await stateService.SetTransportStatusAsync(
                instanceId, errorCode, null, null, null, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not persist FullWorth Cloud transport error state.");
        }
    }

    private static string ClientVersion() =>
        typeof(CloudLearningOutboxUploader).Assembly.GetName().Version?.ToString() ?? "unknown";

    private static string? Trim(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}

public sealed class CloudLearningOutboxWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<CloudLearningOutboxWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<CloudLearningOutboxUploader>()
                    .UploadOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "FullWorth Cloud outbox worker iteration failed.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
