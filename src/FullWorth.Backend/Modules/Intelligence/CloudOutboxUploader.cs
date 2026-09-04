using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Intelligence;

public sealed record CloudSyncResult(
    string Mode,
    bool Registered,
    int Claimed,
    int Sent,
    int Retried,
    int DeadLettered,
    string? ErrorCode = null);

/// <summary>
/// Sends only pre-sanitized CloudSubmissionOutbox envelopes. The first operation of every sync is a
/// local consent check; disabled/revoked instances perform zero FullWorth Cloud HTTP calls.
/// </summary>
public sealed class CloudOutboxUploader(
    IntelligenceDbContext db,
    CloudInstanceCredentialStore credentials,
    IFullWorthCloudClient cloud)
{
    public const int MaxAttempts = 8;
    public const int DefaultBatchSize = 100;
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(3);
    private readonly string leaseOwner = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";

    public async Task<CloudSyncResult> SyncOnceAsync(CancellationToken ct)
    {
        var gate = await GetActiveGateAsync(ct);
        if (gate is null)
            return new(CloudIntelligenceModes.Disabled, false, 0, 0, 0, 0);

        var state = gate.Value.State;
        var consent = gate.Value.Consent;
        var registered = false;
        string secret;
        try
        {
            (secret, registered) = await EnsureCredentialAsync(state, consent, ct);
        }
        catch (FullWorthCloudException ex)
        {
            await RecordStateErrorAsync(state.Id, ex.ErrorCode, ct);
            return new(state.Mode, false, 0, 0, 0, 0, ex.ErrorCode);
        }

        await DeadLetterExhaustedAsync(state.InstanceId, ct);
        var claimed = await ClaimAsync(state.InstanceId, DefaultBatchSize, ct);
        if (claimed.Count == 0)
            return new(state.Mode, registered, 0, 0, 0, 0);

        var valid = new List<(CloudSubmissionOutbox Row, FullWorthCloudSubmissionEvent Event)>();
        var locallyDead = 0;
        foreach (var row in claimed)
        {
            try
            {
                using var payload = JsonDocument.Parse(row.PayloadJson);
                valid.Add((row, new FullWorthCloudSubmissionEvent(
                    row.IdempotencyKey,
                    row.SchemaVersion,
                    row.EventType,
                    payload.RootElement.Clone())));
            }
            catch (JsonException)
            {
                await MarkDeadLetterAsync([row.Id], "cloud_outbox_invalid_json", ct);
                locallyDead++;
            }
        }

        if (valid.Count == 0)
            return new(state.Mode, registered, claimed.Count, 0, 0, locallyDead);

        try
        {
            var result = await SubmitWithAuthRecoveryAsync(state, secret, valid.Select(x => x.Event).ToList(), ct);
            var outcome = await ApplyBatchResultAsync(valid.Select(x => x.Row).ToList(), result, ct);
            await MarkCredentialUsedAndStateHealthyAsync(state.InstanceId, outcome.Sent > 0, ct);
            return new(state.Mode, registered, claimed.Count, outcome.Sent, outcome.Retried, outcome.DeadLettered + locallyDead);
        }
        catch (FullWorthCloudException ex)
        {
            var ids = valid.Select(x => x.Row.Id).ToArray();
            var permanent = IsPermanentBatchFailure(ex);
            var dead = 0;
            var retried = 0;
            if (permanent)
            {
                await MarkDeadLetterAsync(ids, ex.ErrorCode, ct);
                dead = ids.Length;
            }
            else
            {
                retried = await ScheduleRetryAsync(ids, ex.ErrorCode, ex.RetryAfter, ct);
                dead = ids.Length - retried;
            }

            await RecordStateErrorAsync(state.Id, ex.ErrorCode, ct);
            return new(state.Mode, registered, claimed.Count, 0, retried, dead + locallyDead, ex.ErrorCode);
        }
    }

    private async Task<(string Secret, bool Registered)> EnsureCredentialAsync(
        CloudConnectionState state,
        CloudIntelligenceConsent consent,
        CancellationToken ct)
    {
        var row = await credentials.GetAsync(state.InstanceId, ct);
        var currentSecret = row is null ? null : await credentials.GetSecretAsync(state.InstanceId, ct);
        FullWorthCloudRegistrationResult? issued = null;
        var registered = false;

        if (row is null || string.IsNullOrWhiteSpace(currentSecret))
        {
            issued = await cloud.RegisterAsync(
                state.InstanceId,
                CloudIntelligencePolicy.CurrentVersion,
                string.IsNullOrWhiteSpace(consent.ClientVersion) ? "unknown" : consent.ClientVersion,
                ct);
            registered = true;
        }
        else if (row.ExpiresAt.HasValue && row.ExpiresAt.Value <= DateTimeOffset.UtcNow.AddHours(24))
        {
            issued = await cloud.RotateCredentialAsync(state.InstanceId, currentSecret, ct);
        }

        if (issued is not null)
        {
            await credentials.SaveAsync(issued, ct);
            var trackedState = await db.CloudConnectionStates.SingleAsync(x => x.Id == state.Id, ct);
            trackedState.LastRegistrationAt = DateTimeOffset.UtcNow;
            trackedState.EntitlementStatus = issued.EntitlementStatus;
            trackedState.LastErrorCode = null;
            trackedState.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            currentSecret = issued.Credential;
        }

        return (currentSecret!, registered);
    }

    private async Task<FullWorthCloudBatchResult> SubmitWithAuthRecoveryAsync(
        CloudConnectionState state,
        string secret,
        IReadOnlyList<FullWorthCloudSubmissionEvent> events,
        CancellationToken ct)
    {
        try
        {
            return await cloud.SubmitBatchAsync(state.InstanceId, secret, events, ct);
        }
        catch (FullWorthCloudException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized || ex.ErrorCode == "cloud_unauthorized")
        {
            var rotated = await cloud.RotateCredentialAsync(state.InstanceId, secret, ct);
            await credentials.SaveAsync(rotated, ct);
            var trackedState = await db.CloudConnectionStates.SingleAsync(x => x.Id == state.Id, ct);
            trackedState.LastRegistrationAt = DateTimeOffset.UtcNow;
            trackedState.EntitlementStatus = rotated.EntitlementStatus;
            trackedState.LastErrorCode = null;
            trackedState.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return await cloud.SubmitBatchAsync(state.InstanceId, rotated.Credential, events, ct);
        }
    }

    private async Task<(CloudConnectionState State, CloudIntelligenceConsent Consent)?> GetActiveGateAsync(CancellationToken ct)
    {
        var state = await db.CloudConnectionStates.AsNoTracking()
            .SingleOrDefaultAsync(x => x.ScopeKey == CloudConnectionState.InstanceScopeKey, ct);
        if (state is null || state.Mode != CloudIntelligenceModes.Enabled) return null;

        var consent = await db.CloudIntelligenceConsents.AsNoTracking()
            .Where(x => x.InstanceId == state.InstanceId &&
                        x.PolicyVersion == CloudIntelligencePolicy.CurrentVersion &&
                        x.RevokedAt == null)
            .OrderByDescending(x => x.AcceptedAt)
            .FirstOrDefaultAsync(ct);
        return consent is null ? null : (state, consent);
    }

    private async Task<List<CloudSubmissionOutbox>> ClaimAsync(Guid instanceId, int limit, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var candidateIds = await db.CloudSubmissionOutbox.AsNoTracking()
            .Where(x => x.InstanceId == instanceId && x.AttemptCount < MaxAttempts)
            .Where(x =>
                x.Status == CloudSubmissionStatuses.Queued ||
                x.Status == CloudSubmissionStatuses.Failed ||
                (x.Status == CloudSubmissionStatuses.Sending && x.LeaseExpiresAt != null && x.LeaseExpiresAt <= now))
            .Where(x => x.NextAttemptAt == null || x.NextAttemptAt <= now)
            .Where(x => x.LeaseExpiresAt == null || x.LeaseExpiresAt <= now)
            .OrderBy(x => x.CreatedAt)
            .Select(x => x.Id)
            .Take(Math.Clamp(limit * 3, limit, FullWorthCloudClient.MaximumBatchEvents * 3))
            .ToListAsync(ct);

        var claimedIds = new List<Guid>(limit);
        foreach (var id in candidateIds)
        {
            if (claimedIds.Count >= limit) break;
            var affected = await db.CloudSubmissionOutbox
                .Where(x => x.Id == id && x.InstanceId == instanceId && x.AttemptCount < MaxAttempts)
                .Where(x =>
                    x.Status == CloudSubmissionStatuses.Queued ||
                    x.Status == CloudSubmissionStatuses.Failed ||
                    (x.Status == CloudSubmissionStatuses.Sending && x.LeaseExpiresAt != null && x.LeaseExpiresAt <= now))
                .Where(x => x.NextAttemptAt == null || x.NextAttemptAt <= now)
                .Where(x => x.LeaseExpiresAt == null || x.LeaseExpiresAt <= now)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.Status, CloudSubmissionStatuses.Sending)
                    .SetProperty(x => x.LeaseOwner, leaseOwner)
                    .SetProperty(x => x.LeaseExpiresAt, now.Add(LeaseDuration))
                    .SetProperty(x => x.LastAttemptAt, now)
                    .SetProperty(x => x.AttemptCount, x => x.AttemptCount + 1)
                    .SetProperty(x => x.ErrorCode, (string?)null), ct);
            if (affected == 1) claimedIds.Add(id);
        }

        return claimedIds.Count == 0
            ? []
            : await db.CloudSubmissionOutbox.AsNoTracking()
                .Where(x => claimedIds.Contains(x.Id) && x.LeaseOwner == leaseOwner)
                .OrderBy(x => x.CreatedAt)
                .ToListAsync(ct);
    }

    private async Task<(int Sent, int Retried, int DeadLettered)> ApplyBatchResultAsync(
        IReadOnlyList<CloudSubmissionOutbox> rows,
        FullWorthCloudBatchResult result,
        CancellationToken ct)
    {
        if (result.Events.Count == 0)
        {
            if (result.Rejected == 0 && result.Accepted + result.Duplicate == rows.Count)
            {
                await MarkSentAsync(rows.Select(x => x.Id).ToArray(), ct);
                return (rows.Count, 0, 0);
            }

            var retriedRows = await ScheduleRetryAsync(rows.Select(x => x.Id).ToArray(), "cloud_partial_batch_result", null, ct);
            return (0, retriedRows, rows.Count - retriedRows);
        }

        var byKey = result.Events
            .GroupBy(x => x.IdempotencyKey, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.Last(), StringComparer.Ordinal);
        var sent = new List<Guid>();
        var retry = new List<Guid>();
        var dead = new List<(Guid Id, string Error)>();

        foreach (var row in rows)
        {
            if (!byKey.TryGetValue(row.IdempotencyKey, out var item))
            {
                retry.Add(row.Id);
                continue;
            }

            var status = item.Status.Trim().ToLowerInvariant();
            if (status is "accepted" or "sent" or "duplicate")
                sent.Add(row.Id);
            else if (status is "rejected" or "invalid" or "unsupported")
                dead.Add((row.Id, NormalizeError(item.ErrorCode, "cloud_event_rejected")));
            else
                retry.Add(row.Id);
        }

        if (sent.Count > 0) await MarkSentAsync(sent, ct);
        foreach (var group in dead.GroupBy(x => x.Error, StringComparer.Ordinal))
            await MarkDeadLetterAsync(group.Select(x => x.Id).ToArray(), group.Key, ct);
        var retried = retry.Count == 0 ? 0 : await ScheduleRetryAsync(retry, "cloud_event_retry", null, ct);
        return (sent.Count, retried, dead.Count + retry.Count - retried);
    }

    private async Task MarkSentAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return;
        var now = DateTimeOffset.UtcNow;
        await db.CloudSubmissionOutbox.Where(x => ids.Contains(x.Id) && x.LeaseOwner == leaseOwner)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, CloudSubmissionStatuses.Sent)
                .SetProperty(x => x.SentAt, now)
                .SetProperty(x => x.NextAttemptAt, (DateTimeOffset?)null)
                .SetProperty(x => x.ErrorCode, (string?)null)
                .SetProperty(x => x.LeaseOwner, (string?)null)
                .SetProperty(x => x.LeaseExpiresAt, (DateTimeOffset?)null), ct);
    }

    private async Task<int> ScheduleRetryAsync(
        IReadOnlyCollection<Guid> ids,
        string errorCode,
        TimeSpan? retryAfter,
        CancellationToken ct)
    {
        if (ids.Count == 0) return 0;
        var rows = await db.CloudSubmissionOutbox
            .Where(x => ids.Contains(x.Id) && x.LeaseOwner == leaseOwner)
            .ToListAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var retried = 0;
        foreach (var row in rows)
        {
            row.ErrorCode = NormalizeError(errorCode, "cloud_retry");
            row.LeaseOwner = null;
            row.LeaseExpiresAt = null;
            if (row.AttemptCount >= MaxAttempts)
            {
                row.Status = CloudSubmissionStatuses.DeadLetter;
                row.NextAttemptAt = null;
                continue;
            }

            row.Status = CloudSubmissionStatuses.Failed;
            row.NextAttemptAt = now.Add(ComputeRetryDelay(row.AttemptCount, retryAfter, errorCode, row.Id));
            retried++;
        }
        await db.SaveChangesAsync(ct);
        return retried;
    }

    private async Task MarkDeadLetterAsync(IReadOnlyCollection<Guid> ids, string errorCode, CancellationToken ct)
    {
        if (ids.Count == 0) return;
        await db.CloudSubmissionOutbox.Where(x => ids.Contains(x.Id) && x.LeaseOwner == leaseOwner)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, CloudSubmissionStatuses.DeadLetter)
                .SetProperty(x => x.NextAttemptAt, (DateTimeOffset?)null)
                .SetProperty(x => x.ErrorCode, NormalizeError(errorCode, "cloud_dead_letter"))
                .SetProperty(x => x.LeaseOwner, (string?)null)
                .SetProperty(x => x.LeaseExpiresAt, (DateTimeOffset?)null), ct);
    }

    private async Task DeadLetterExhaustedAsync(Guid instanceId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        await db.CloudSubmissionOutbox
            .Where(x => x.InstanceId == instanceId &&
                        x.AttemptCount >= MaxAttempts &&
                        x.Status != CloudSubmissionStatuses.Sent &&
                        x.Status != CloudSubmissionStatuses.DeadLetter &&
                        (x.LeaseExpiresAt == null || x.LeaseExpiresAt <= now))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, CloudSubmissionStatuses.DeadLetter)
                .SetProperty(x => x.NextAttemptAt, (DateTimeOffset?)null)
                .SetProperty(x => x.ErrorCode, x => x.ErrorCode ?? "cloud_retry_exhausted")
                .SetProperty(x => x.LeaseOwner, (string?)null)
                .SetProperty(x => x.LeaseExpiresAt, (DateTimeOffset?)null), ct);
    }

    private async Task MarkCredentialUsedAndStateHealthyAsync(Guid instanceId, bool submitted, CancellationToken ct)
    {
        await credentials.MarkUsedAsync(instanceId, ct);
        var state = await db.CloudConnectionStates.SingleOrDefaultAsync(x => x.InstanceId == instanceId, ct);
        if (state is null) return;
        if (submitted) state.LastSubmissionAt = DateTimeOffset.UtcNow;
        state.LastErrorCode = null;
        state.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private async Task RecordStateErrorAsync(Guid stateId, string errorCode, CancellationToken ct)
    {
        var state = await db.CloudConnectionStates.SingleOrDefaultAsync(x => x.Id == stateId, ct);
        if (state is null) return;
        state.LastErrorCode = NormalizeError(errorCode, "cloud_error");
        state.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    internal static TimeSpan ComputeRetryDelay(int attemptCount, TimeSpan? retryAfter, string errorCode, Guid rowId)
    {
        if (retryAfter.HasValue)
            return TimeSpan.FromSeconds(Math.Clamp(retryAfter.Value.TotalSeconds, 30, 24 * 60 * 60));

        var exponent = Math.Clamp(attemptCount - 1, 0, 10);
        var seconds = Math.Min(6 * 60 * 60, 30 * Math.Pow(2, exponent));
        if (errorCode is "cloud_entitlement_denied" or "cloud_enrollment_missing")
            seconds = Math.Max(seconds, 60 * 60);
        var jitter = rowId.ToByteArray()[0] % 31;
        return TimeSpan.FromSeconds(seconds + jitter);
    }

    private static bool IsPermanentBatchFailure(FullWorthCloudException ex) =>
        ex.ErrorCode is "cloud_batch_too_large" ||
        (ex.StatusCode.HasValue &&
         (int)ex.StatusCode.Value >= 400 &&
         (int)ex.StatusCode.Value < 500 &&
         ex.StatusCode is not HttpStatusCode.Unauthorized and not HttpStatusCode.Forbidden and not HttpStatusCode.TooManyRequests);

    private static string NormalizeError(string? value, string fallback)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim().ToLowerInvariant();
        return normalized.Length <= 120 ? normalized : normalized[..120];
    }
}

public sealed class CloudOutboxUploaderWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<CloudOutboxUploaderWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (!stoppingToken.IsCancellationRequested)
        {
            await RunOnceAsync(stoppingToken);
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken)) break;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var uploader = scope.ServiceProvider.GetRequiredService<CloudOutboxUploader>();
            var result = await uploader.SyncOnceAsync(ct);
            if (result.ErrorCode is not null)
                logger.LogWarning("FullWorth Cloud sync ended with {ErrorCode}; claimed={Claimed} retried={Retried} dead={Dead}",
                    result.ErrorCode, result.Claimed, result.Retried, result.DeadLettered);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "FullWorth Cloud outbox worker failed unexpectedly.");
        }
    }
}
