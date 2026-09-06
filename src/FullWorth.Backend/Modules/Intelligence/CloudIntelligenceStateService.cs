using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Intelligence;

public sealed record CloudIntelligenceStateView(
    Guid InstanceId,
    string Mode,
    bool RequiresSetupDecision,
    DateTimeOffset? SetupDecisionAt,
    Guid? SetupDecisionByUserId,
    string CurrentPolicyVersion,
    string? AcceptedPolicyVersion,
    DateTimeOffset? AcceptedAt,
    DateTimeOffset? RevokedAt,
    string? EntitlementStatus,
    DateTimeOffset? LastRegistrationAt,
    DateTimeOffset? LastSubmissionAt,
    string? LastErrorCode,
    DateTimeOffset UpdatedAt);

public sealed record EnableCloudIntelligenceRequest(string PolicyVersion, string? Locale, string? ClientVersion);

public sealed class CloudIntelligenceStateService(IntelligenceDbContext db)
{
    public async Task<CloudIntelligenceStateView> GetAsync(CancellationToken ct)
    {
        var state = await GetOrCreateStateAsync(ct);
        var consent = await db.CloudIntelligenceConsents.AsNoTracking()
            .Where(x => x.InstanceId == state.InstanceId)
            .OrderByDescending(x => x.AcceptedAt)
            .FirstOrDefaultAsync(ct);
        return ToView(state, consent);
    }

    public async Task<CloudIntelligenceStateView> EnableAsync(
        Guid acceptedByUserId,
        EnableCloudIntelligenceRequest request,
        CancellationToken ct)
    {
        if (!string.Equals(request.PolicyVersion?.Trim(), CloudIntelligencePolicy.CurrentVersion, StringComparison.Ordinal))
            throw new ArgumentException("Cloud Intelligence policy version is stale. Reload the consent information before enabling.");

        var state = await GetOrCreateStateAsync(ct);
        var now = DateTimeOffset.UtcNow;

        var active = await db.CloudIntelligenceConsents
            .Where(x => x.InstanceId == state.InstanceId && x.RevokedAt == null)
            .ToListAsync(ct);
        foreach (var consent in active.Where(x => x.PolicyVersion != CloudIntelligencePolicy.CurrentVersion))
            consent.RevokedAt = now;

        var current = active.FirstOrDefault(x => x.PolicyVersion == CloudIntelligencePolicy.CurrentVersion);
        if (current is null)
        {
            current = new CloudIntelligenceConsent
            {
                InstanceId = state.InstanceId,
                AcceptedByUserId = acceptedByUserId,
                PolicyVersion = CloudIntelligencePolicy.CurrentVersion,
                AcceptedAt = now,
                Locale = NormalizeLocale(request.Locale),
                ClientVersion = NormalizeClientVersion(request.ClientVersion)
            };
            db.CloudIntelligenceConsents.Add(current);
        }

        state.ScopeKey = CloudConnectionState.InstanceScopeKey;
        state.Mode = CloudIntelligenceModes.Enabled;
        state.SetupDecisionAt = now;
        state.SetupDecisionByUserId = acceptedByUserId;
        state.EnabledAt ??= now;
        state.DisabledAt = null;
        state.LastErrorCode = null;
        state.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        return ToView(state, current);
    }

    public Task<CloudIntelligenceStateView> DisableAsync(CancellationToken ct) => DisableAsync(null, ct);

    public async Task<CloudIntelligenceStateView> DisableAsync(Guid? decidedByUserId, CancellationToken ct)
    {
        var state = await GetOrCreateStateAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var active = await db.CloudIntelligenceConsents
            .Where(x => x.InstanceId == state.InstanceId && x.RevokedAt == null)
            .ToListAsync(ct);
        foreach (var consent in active) consent.RevokedAt = now;

        state.Mode = CloudIntelligenceModes.Disabled;
        state.SetupDecisionAt = now;
        if (decidedByUserId.HasValue) state.SetupDecisionByUserId = decidedByUserId.Value;
        state.DisabledAt = now;
        state.LastErrorCode = null;
        state.UpdatedAt = now;

        // Opt-out revokes consent, drops the rotatable credential and discards anything that has not
        // already been transmitted. Re-enabling starts with fresh consent and fresh learning events.
        db.CloudInstanceCredentials.RemoveRange(await db.CloudInstanceCredentials
            .Where(x => x.InstanceId == state.InstanceId)
            .ToListAsync(ct));
        db.CloudSubmissionOutbox.RemoveRange(await db.CloudSubmissionOutbox
            .Where(x => x.InstanceId == state.InstanceId &&
                        x.Status != CloudSubmissionStatuses.Sent &&
                        x.Status != CloudSubmissionStatuses.DeadLetter)
            .ToListAsync(ct));

        await db.SaveChangesAsync(ct);

        var latest = active.OrderByDescending(x => x.AcceptedAt).FirstOrDefault()
            ?? await db.CloudIntelligenceConsents.AsNoTracking()
                .Where(x => x.InstanceId == state.InstanceId)
                .OrderByDescending(x => x.AcceptedAt)
                .FirstOrDefaultAsync(ct);
        return ToView(state, latest);
    }

    public async Task<bool> HasCurrentActiveConsentAsync(CancellationToken ct)
    {
        var state = await db.CloudConnectionStates.AsNoTracking()
            .SingleOrDefaultAsync(x => x.ScopeKey == CloudConnectionState.InstanceScopeKey, ct);
        return state is not null &&
               state.Mode == CloudIntelligenceModes.Enabled &&
               await db.CloudIntelligenceConsents.AsNoTracking().AnyAsync(x =>
                   x.InstanceId == state.InstanceId &&
                   x.PolicyVersion == CloudIntelligencePolicy.CurrentVersion &&
                   x.RevokedAt == null, ct);
    }

    public Task<CloudConnectionState?> GetEnabledStateAsync(CancellationToken ct) =>
        db.CloudConnectionStates.SingleOrDefaultAsync(x =>
            x.ScopeKey == CloudConnectionState.InstanceScopeKey && x.Mode == CloudIntelligenceModes.Enabled, ct);

    public async Task SetTransportStatusAsync(
        Guid instanceId,
        string? errorCode,
        string? entitlementStatus,
        DateTimeOffset? registeredAt,
        DateTimeOffset? submittedAt,
        CancellationToken ct)
    {
        var state = await db.CloudConnectionStates.SingleAsync(x =>
            x.ScopeKey == CloudConnectionState.InstanceScopeKey && x.InstanceId == instanceId, ct);
        state.LastErrorCode = Trim(errorCode, 120);
        if (entitlementStatus is not null) state.EntitlementStatus = Trim(entitlementStatus, 80);
        if (registeredAt.HasValue) state.LastRegistrationAt = registeredAt;
        if (submittedAt.HasValue) state.LastSubmissionAt = submittedAt;
        state.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private async Task<CloudConnectionState> GetOrCreateStateAsync(CancellationToken ct)
    {
        var state = await db.CloudConnectionStates.SingleOrDefaultAsync(
            x => x.ScopeKey == CloudConnectionState.InstanceScopeKey, ct);
        if (state is not null) return state;

        state = new CloudConnectionState { ScopeKey = CloudConnectionState.InstanceScopeKey };
        db.CloudConnectionStates.Add(state);
        try
        {
            await db.SaveChangesAsync(ct);
            return state;
        }
        catch (DbUpdateException)
        {
            db.Entry(state).State = EntityState.Detached;
            var winner = await db.CloudConnectionStates.SingleOrDefaultAsync(
                x => x.ScopeKey == CloudConnectionState.InstanceScopeKey, ct);
            if (winner is not null) return winner;
            throw;
        }
    }

    private static CloudIntelligenceStateView ToView(CloudConnectionState state, CloudIntelligenceConsent? consent)
    {
        var hasCurrentActiveConsent = consent is not null &&
                                      consent.PolicyVersion == CloudIntelligencePolicy.CurrentVersion &&
                                      consent.RevokedAt is null;
        var requiresDecision = state.SetupDecisionAt is null ||
                               (state.Mode == CloudIntelligenceModes.Enabled && !hasCurrentActiveConsent);
        return new(
            state.InstanceId,
            state.Mode,
            requiresDecision,
            state.SetupDecisionAt,
            state.SetupDecisionByUserId,
            CloudIntelligencePolicy.CurrentVersion,
            consent?.PolicyVersion,
            consent?.AcceptedAt,
            consent?.RevokedAt,
            state.EntitlementStatus,
            state.LastRegistrationAt,
            state.LastSubmissionAt,
            state.LastErrorCode,
            state.UpdatedAt);
    }

    private static string NormalizeLocale(string? locale)
    {
        var value = string.IsNullOrWhiteSpace(locale) ? "en" : locale.Trim();
        return value.Length <= 20 ? value : value[..20];
    }

    private static string NormalizeClientVersion(string? clientVersion)
    {
        var value = string.IsNullOrWhiteSpace(clientVersion) ? "unknown" : clientVersion.Trim();
        return value.Length <= 80 ? value : value[..80];
    }

    private static string? Trim(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim();
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
    }
}
