using FullWorth.Web.Modules.Sessions;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Web.Data;

public sealed class AuthSessionPersistence(AuthDbContext db) : ISessionPersistence
{
    public Task<UserSession?> GetAsync(Guid sessionId, CancellationToken ct) =>
        db.UserSessions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == sessionId, ct);

    public Task<UserSession?> GetForUserAsync(Guid authUserId, Guid sessionId, CancellationToken ct) =>
        db.UserSessions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == sessionId && x.AuthUserId == authUserId, ct);

    public async Task AddAsync(UserSession session, CancellationToken ct)
    {
        db.UserSessions.Add(session);
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<UserSession>> ListForUserAsync(Guid authUserId, CancellationToken ct) =>
        await db.UserSessions.AsNoTracking()
            .Where(x => x.AuthUserId == authUserId)
            .OrderByDescending(x => x.LastSeenAt)
            .ToListAsync(ct);

    public async Task<bool> TouchAsync(Guid sessionId, DateTimeOffset lastSeenAt, DateTimeOffset expiresAt, CancellationToken ct) =>
        await db.UserSessions
            .Where(x => x.Id == sessionId && x.RevokedAt == null && x.ExpiresAt > lastSeenAt && x.AbsoluteExpiresAt > lastSeenAt)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.LastSeenAt, lastSeenAt)
                .SetProperty(x => x.ExpiresAt, expiresAt), ct) == 1;

    public async Task<bool> RevokeAsync(Guid authUserId, Guid sessionId, DateTimeOffset revokedAt, CancellationToken ct) =>
        await db.UserSessions
            .Where(x => x.Id == sessionId && x.AuthUserId == authUserId && x.RevokedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.RevokedAt, revokedAt), ct) == 1;

    public Task<int> RevokeAllOtherAsync(Guid authUserId, Guid currentSessionId, DateTimeOffset revokedAt, CancellationToken ct) =>
        db.UserSessions
            .Where(x => x.AuthUserId == authUserId && x.Id != currentSessionId && x.RevokedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.RevokedAt, revokedAt), ct);

    public Task<int> RevokeAllAsync(Guid authUserId, DateTimeOffset revokedAt, CancellationToken ct) =>
        db.UserSessions
            .Where(x => x.AuthUserId == authUserId && x.RevokedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.RevokedAt, revokedAt), ct);

    public Task<int> PurgeExpiredAsync(DateTimeOffset expiredBefore, DateTimeOffset revokedBefore, CancellationToken ct) =>
        db.UserSessions
            .Where(x => x.AbsoluteExpiresAt < expiredBefore || x.ExpiresAt < expiredBefore || (x.RevokedAt != null && x.RevokedAt < revokedBefore))
            .ExecuteDeleteAsync(ct);
}
