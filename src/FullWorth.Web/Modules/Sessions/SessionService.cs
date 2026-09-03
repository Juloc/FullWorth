using Microsoft.Extensions.Options;

namespace FullWorth.Web.Modules.Sessions;

public sealed class SessionService
{
    private readonly SessionStore _store;
    private readonly SessionOptions _options;
    private readonly TimeProvider _timeProvider;

    public SessionService(SessionStore store, IOptions<SessionOptions> options, TimeProvider timeProvider)
    {
        _store = store;
        _options = options.Value;
        _options.Validate();
        _timeProvider = timeProvider;
    }

    public async Task<UserSession> CreateSessionAsync(
        Guid authUserId,
        string? securityStamp,
        string? userAgent,
        string? ipAddress,
        CancellationToken ct)
    {
        if (authUserId == Guid.Empty)
            throw new ArgumentException("Authentication user ID is required.", nameof(authUserId));

        var now = _timeProvider.GetUtcNow();
        var absoluteExpiresAt = now.Add(_options.AbsoluteLifetime);
        var idleExpiresAt = Min(now.Add(_options.IdleTimeout), absoluteExpiresAt);
        var metadata = SessionDeviceMetadata.Create(userAgent, ipAddress);
        var safeSecurityStamp = NormalizeSecurityStamp(securityStamp);

        return await _store.CreateAsync(
            authUserId,
            now,
            idleExpiresAt,
            absoluteExpiresAt,
            metadata.DeviceName,
            metadata.UserAgent,
            metadata.IpAddress,
            safeSecurityStamp,
            ct);
    }

    public async Task<SessionValidationResult> ValidateSessionAsync(
        Guid sessionId,
        Guid expectedAuthUserId,
        SessionUserSecurityState userSecurity,
        CancellationToken ct)
    {
        if (sessionId == Guid.Empty || expectedAuthUserId == Guid.Empty)
            return new SessionValidationResult(SessionValidationStatus.NotFound);

        var session = await _store.GetForUserAsync(expectedAuthUserId, sessionId, ct);
        if (session is null)
            return new SessionValidationResult(SessionValidationStatus.NotFound);
        if (session.RevokedAt is not null)
            return new SessionValidationResult(SessionValidationStatus.Revoked);
        if (!userSecurity.IsActive)
            return new SessionValidationResult(SessionValidationStatus.UserInvalid);
        if (!SecurityStampMatches(session.SecurityStampAtIssue, userSecurity.SecurityStamp))
            return new SessionValidationResult(SessionValidationStatus.SecurityStampChanged);

        var now = _timeProvider.GetUtcNow();
        if (now >= session.AbsoluteExpiresAt)
            return new SessionValidationResult(SessionValidationStatus.AbsoluteExpired);
        if (now >= session.ExpiresAt)
            return new SessionValidationResult(SessionValidationStatus.IdleExpired);

        if (now - session.LastSeenAt < _options.TouchInterval)
            return new SessionValidationResult(SessionValidationStatus.Valid);

        var nextIdleExpiry = Min(now.Add(_options.IdleTimeout), session.AbsoluteExpiresAt);
        var touched = await _store.TouchAsync(session.Id, now, nextIdleExpiry, ct);
        return touched
            ? new SessionValidationResult(SessionValidationStatus.Valid, LastSeenUpdated: true)
            : new SessionValidationResult(SessionValidationStatus.NotFound);
    }

    public async Task<SessionListDto> ListSessionsAsync(Guid authUserId, Guid? currentSessionId, CancellationToken ct)
    {
        if (authUserId == Guid.Empty)
            return new SessionListDto([]);

        var now = _timeProvider.GetUtcNow();
        var sessions = await _store.ListForUserAsync(authUserId, ct);
        var dto = sessions
            .OrderByDescending(x => x.Id == currentSessionId)
            .ThenByDescending(x => x.LastSeenAt)
            .Select(x => new SessionDto(
                x.Id,
                x.DeviceName,
                x.CreatedAt,
                x.LastSeenAt,
                x.Id == currentSessionId,
                IsActive(x, now)))
            .ToArray();

        return new SessionListDto(dto);
    }

    public Task<bool> RevokeSessionAsync(Guid authUserId, Guid sessionId, CancellationToken ct) =>
        authUserId == Guid.Empty || sessionId == Guid.Empty
            ? Task.FromResult(false)
            : _store.RevokeAsync(authUserId, sessionId, _timeProvider.GetUtcNow(), ct);

    public Task<int> RevokeAllOtherSessionsAsync(Guid authUserId, Guid currentSessionId, CancellationToken ct) =>
        authUserId == Guid.Empty || currentSessionId == Guid.Empty
            ? Task.FromResult(0)
            : _store.RevokeAllOtherAsync(authUserId, currentSessionId, _timeProvider.GetUtcNow(), ct);

    public Task<int> RevokeAllSessionsAsync(Guid authUserId, CancellationToken ct) =>
        authUserId == Guid.Empty
            ? Task.FromResult(0)
            : _store.RevokeAllAsync(authUserId, _timeProvider.GetUtcNow(), ct);

    public Task<int> RevokeForSecurityEventAsync(Guid authUserId, CancellationToken ct) =>
        RevokeAllSessionsAsync(authUserId, ct);

    public Task<bool> LogoutAsync(Guid authUserId, Guid currentSessionId, CancellationToken ct) =>
        RevokeSessionAsync(authUserId, currentSessionId, ct);

    public Task<int> PurgeExpiredAsync(CancellationToken ct)
    {
        var cutoff = _timeProvider.GetUtcNow().Subtract(_options.CleanupRetention);
        return _store.PurgeExpiredAsync(cutoff, cutoff, ct);
    }

    private static bool IsActive(UserSession session, DateTimeOffset now) =>
        session.RevokedAt is null && now < session.ExpiresAt && now < session.AbsoluteExpiresAt;

    private static bool SecurityStampMatches(string? issued, string? current)
    {
        if (issued is null)
            return true;

        return current is not null && string.Equals(issued, current, StringComparison.Ordinal);
    }

    private static string? NormalizeSecurityStamp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();
        return trimmed[..Math.Min(trimmed.Length, UserSession.MaxSecurityStampLength)];
    }

    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) =>
        left <= right ? left : right;
}
