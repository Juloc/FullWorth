using FullWorth.Web.Modules.Sessions;

namespace FullWorth.Web.Tests.Sessions;

internal sealed class TestTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    private DateTimeOffset _utcNow = utcNow;

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void Advance(TimeSpan duration) => _utcNow = _utcNow.Add(duration);
}

internal sealed class FakeSessionPersistence : ISessionPersistence
{
    private readonly object _gate = new();
    private readonly List<UserSession> _sessions = [];

    public int AddCalls { get; private set; }
    public int TouchCalls { get; private set; }

    public Task<UserSession?> GetAsync(Guid sessionId, CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult(_sessions.SingleOrDefault(x => x.Id == sessionId));
    }

    public Task<UserSession?> GetForUserAsync(Guid authUserId, Guid sessionId, CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult(_sessions.SingleOrDefault(x => x.Id == sessionId && x.AuthUserId == authUserId));
    }

    public Task AddAsync(UserSession session, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_sessions.Any(x => x.Id == session.Id))
                throw new InvalidOperationException("Duplicate session ID.");

            _sessions.Add(session);
            AddCalls++;
            return Task.CompletedTask;
        }
    }

    public Task<IReadOnlyList<UserSession>> ListForUserAsync(Guid authUserId, CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<UserSession>>(_sessions.Where(x => x.AuthUserId == authUserId).ToArray());
    }

    public Task<bool> TouchAsync(Guid sessionId, DateTimeOffset lastSeenAt, DateTimeOffset expiresAt, CancellationToken ct)
    {
        lock (_gate)
        {
            TouchCalls++;
            var session = _sessions.SingleOrDefault(x => x.Id == sessionId && x.RevokedAt is null);
            if (session is null)
                return Task.FromResult(false);

            session.LastSeenAt = lastSeenAt;
            session.ExpiresAt = expiresAt;
            return Task.FromResult(true);
        }
    }

    public Task<bool> RevokeAsync(Guid authUserId, Guid sessionId, DateTimeOffset revokedAt, CancellationToken ct)
    {
        lock (_gate)
        {
            var session = _sessions.SingleOrDefault(x => x.Id == sessionId && x.AuthUserId == authUserId && x.RevokedAt is null);
            if (session is null)
                return Task.FromResult(false);

            session.RevokedAt = revokedAt;
            return Task.FromResult(true);
        }
    }

    public Task<int> RevokeAllOtherAsync(Guid authUserId, Guid currentSessionId, DateTimeOffset revokedAt, CancellationToken ct)
    {
        lock (_gate)
        {
            var sessions = _sessions.Where(x => x.AuthUserId == authUserId && x.Id != currentSessionId && x.RevokedAt is null).ToArray();
            foreach (var session in sessions)
                session.RevokedAt = revokedAt;
            return Task.FromResult(sessions.Length);
        }
    }

    public Task<int> RevokeAllAsync(Guid authUserId, DateTimeOffset revokedAt, CancellationToken ct)
    {
        lock (_gate)
        {
            var sessions = _sessions.Where(x => x.AuthUserId == authUserId && x.RevokedAt is null).ToArray();
            foreach (var session in sessions)
                session.RevokedAt = revokedAt;
            return Task.FromResult(sessions.Length);
        }
    }

    public Task<int> PurgeExpiredAsync(DateTimeOffset expiredBefore, DateTimeOffset revokedBefore, CancellationToken ct)
    {
        lock (_gate)
        {
            var removed = _sessions.RemoveAll(x =>
                x.AbsoluteExpiresAt <= expiredBefore ||
                (x.RevokedAt is not null && x.RevokedAt <= revokedBefore));
            return Task.FromResult(removed);
        }
    }

    public UserSession GetRaw(Guid sessionId)
    {
        lock (_gate)
            return _sessions.Single(x => x.Id == sessionId);
    }
}
