namespace FullWorth.Web.Modules.Sessions;

/// <summary>
/// Feature-specific persistence boundary implemented against AuthDbContext by Integrator C.
/// It intentionally avoids any dependency on FullWorthDbContext and is not a generic repository.
/// </summary>
public interface ISessionPersistence
{
    Task<UserSession?> GetAsync(Guid sessionId, CancellationToken ct);
    Task<UserSession?> GetForUserAsync(Guid authUserId, Guid sessionId, CancellationToken ct);
    Task AddAsync(UserSession session, CancellationToken ct);
    Task<IReadOnlyList<UserSession>> ListForUserAsync(Guid authUserId, CancellationToken ct);
    Task<bool> TouchAsync(Guid sessionId, DateTimeOffset lastSeenAt, DateTimeOffset expiresAt, CancellationToken ct);
    Task<bool> RevokeAsync(Guid authUserId, Guid sessionId, DateTimeOffset revokedAt, CancellationToken ct);
    Task<int> RevokeAllOtherAsync(Guid authUserId, Guid currentSessionId, DateTimeOffset revokedAt, CancellationToken ct);
    Task<int> RevokeAllAsync(Guid authUserId, DateTimeOffset revokedAt, CancellationToken ct);
    Task<int> PurgeExpiredAsync(DateTimeOffset expiredBefore, DateTimeOffset revokedBefore, CancellationToken ct);
}
