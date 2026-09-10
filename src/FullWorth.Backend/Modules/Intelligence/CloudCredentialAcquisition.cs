using System.Diagnostics;

namespace FullWorth.Backend.Modules.Intelligence;

/// <summary>
/// Remembers that registering with the Cloud just failed, so the next page load does not wait again.
/// A singleton on purpose: the point is to survive across requests, which is where the cost was.
/// </summary>
public sealed class CloudRegistrationCooldown
{
    private readonly object gate = new();
    private DateTimeOffset until = DateTimeOffset.MinValue;
    private string? errorCode;

    /// <summary>The reason to report immediately, or null when an attempt is allowed.</summary>
    public string? ActiveReason(DateTimeOffset now)
    {
        lock (gate) return now < until ? errorCode ?? "cloud_unavailable" : null;
    }

    public void Fail(DateTimeOffset until, string errorCode)
    {
        lock (gate) { this.until = until; this.errorCode = errorCode; }
    }

    public void Clear()
    {
        lock (gate) { until = DateTimeOffset.MinValue; errorCode = null; }
    }
}

/// <summary>
/// Gets the Cloud credential for a user-facing request without letting the page wait on the Cloud.
///
/// Six read endpoints each carried the same twenty-five lines: no secret stored → register → save →
/// record the transport status. Registration goes through the Cloud client's 45-second HTTP timeout, so
/// an unreachable Cloud stalled a page load for up to 45 seconds, once per request, and every one of
/// those requests started its own attempt.
///
/// Two changes, both because the caller is a page load: the attempt gets a budget of a few seconds
/// rather than the full HTTP timeout, and a failure starts a cooldown so the next load fails fast
/// instead of waiting again. Nothing is lost by giving up early — the background workers register too,
/// so a Cloud that comes back is picked up without anyone reloading anything.
/// </summary>
public sealed class CloudCredentialAcquisition(
    CloudInstanceCredentialStore credentials,
    IFullWorthCloudClient cloud,
    CloudIntelligenceStateService cloudState,
    CloudRegistrationCooldown cooldown,
    ILogger<CloudCredentialAcquisition> logger)
{
    /// <summary>
    /// What a page load may spend on registering. Long enough for a healthy Cloud, short enough that a
    /// dead one is not felt as a hang.
    /// </summary>
    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

    /// <summary>After a failure the next request reports the same reason at once instead of retrying.</summary>
    public static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(1);

    /// <summary>The credential, or the reason there is none. Never throws for a Cloud failure.</summary>
    public async Task<(string? Secret, string? ErrorCode)> TryGetAsync(Guid instanceId, CancellationToken ct)
    {
        var stored = await credentials.GetSecretAsync(instanceId, ct);
        if (!string.IsNullOrWhiteSpace(stored)) return (stored, null);

        if (cooldown.ActiveReason(DateTimeOffset.UtcNow) is { } reason) return (null, reason);

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(Budget);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var registration = await cloud.RegisterAsync(
                instanceId,
                CloudIntelligencePolicy.CurrentVersion,
                FullWorthVersion.Full,
                null,
                budget.Token);
            await credentials.SaveAsync(registration, ct);
            await cloudState.SetTransportStatusAsync(
                instanceId, null, registration.EntitlementStatus, DateTimeOffset.UtcNow, null, ct);
            cooldown.Clear();
            return (registration.Credential, null);
        }
        catch (FullWorthCloudException exception)
        {
            return await FailAsync(instanceId, exception.ErrorCode, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The budget ran out, not the request. The caller gets an answer; the workers keep trying.
            logger.LogWarning(
                "Cloud registration exceeded its {Budget} budget for a user request after {Elapsed}.",
                Budget,
                stopwatch.Elapsed);
            return await FailAsync(instanceId, "cloud_timeout", ct);
        }
    }

    private async Task<(string?, string?)> FailAsync(Guid instanceId, string? errorCode, CancellationToken ct)
    {
        var code = string.IsNullOrWhiteSpace(errorCode) ? "cloud_unavailable" : errorCode;
        cooldown.Fail(DateTimeOffset.UtcNow.Add(Cooldown), code);

        // Recording the status must not fail the caller: it is diagnostics, not the answer.
        try { await cloudState.SetTransportStatusAsync(instanceId, code, null, null, null, ct); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogDebug(exception, "Could not record the Cloud transport status.");
        }

        return (null, code);
    }
}
