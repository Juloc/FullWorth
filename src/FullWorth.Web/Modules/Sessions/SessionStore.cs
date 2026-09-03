namespace FullWorth.Web.Modules.Sessions;

public sealed class SessionStore(ISessionPersistence persistence)
{
    public Task<UserSession?> GetAsync(Guid sessionId, CancellationToken ct) =>
        persistence.GetAsync(sessionId, ct);

    public Task<UserSession?> GetForUserAsync(Guid authUserId, Guid sessionId, CancellationToken ct) =>
        persistence.GetForUserAsync(authUserId, sessionId, ct);

    public async Task<UserSession> CreateAsync(
        Guid authUserId,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt,
        DateTimeOffset absoluteExpiresAt,
        string deviceName,
        string? userAgent,
        string? ipAddress,
        string? securityStampAtIssue,
        CancellationToken ct)
    {
        var session = new UserSession
        {
            Id = Guid.NewGuid(),
            AuthUserId = authUserId,
            CreatedAt = createdAt,
            LastSeenAt = createdAt,
            ExpiresAt = expiresAt,
            AbsoluteExpiresAt = absoluteExpiresAt,
            DeviceName = deviceName,
            UserAgent = userAgent,
            IpAddress = ipAddress,
            SecurityStampAtIssue = securityStampAtIssue
        };

        await persistence.AddAsync(session, ct);
        return session;
    }

    public Task<IReadOnlyList<UserSession>> ListForUserAsync(Guid authUserId, CancellationToken ct) =>
        persistence.ListForUserAsync(authUserId, ct);

    public Task<bool> TouchAsync(Guid sessionId, DateTimeOffset now, DateTimeOffset expiresAt, CancellationToken ct) =>
        persistence.TouchAsync(sessionId, now, expiresAt, ct);

    public Task<bool> RevokeAsync(Guid authUserId, Guid sessionId, DateTimeOffset now, CancellationToken ct) =>
        persistence.RevokeAsync(authUserId, sessionId, now, ct);

    public Task<int> RevokeAllOtherAsync(Guid authUserId, Guid currentSessionId, DateTimeOffset now, CancellationToken ct) =>
        persistence.RevokeAllOtherAsync(authUserId, currentSessionId, now, ct);

    public Task<int> RevokeAllAsync(Guid authUserId, DateTimeOffset now, CancellationToken ct) =>
        persistence.RevokeAllAsync(authUserId, now, ct);

    public Task<int> PurgeExpiredAsync(DateTimeOffset expiredBefore, DateTimeOffset revokedBefore, CancellationToken ct) =>
        persistence.PurgeExpiredAsync(expiredBefore, revokedBefore, ct);
}
