using FullWorth.Web.Modules.Sessions;
using Microsoft.Extensions.Options;

namespace FullWorth.Web.Tests.Sessions;

public sealed class SessionServiceTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 12, 18, 0, 0, TimeSpan.Zero);
    private static readonly SessionUserSecurityState ActiveUser = new(true, "stamp-1");

    [Fact]
    public async Task CreateSession_PersistsExpectedLifetimes()
    {
        var (service, persistence, _) = CreateService();
        var userId = Guid.NewGuid();

        var session = await service.CreateSessionAsync(userId, "stamp-1", "Mozilla/5.0 Windows Chrome/1", "192.0.2.10", default);

        Assert.NotEqual(Guid.Empty, session.Id);
        Assert.Equal(userId, session.AuthUserId);
        Assert.Equal(Start, session.CreatedAt);
        Assert.Equal(Start, session.LastSeenAt);
        Assert.Equal(Start.AddMinutes(30), session.ExpiresAt);
        Assert.Equal(Start.AddDays(30), session.AbsoluteExpiresAt);
        Assert.Equal("stamp-1", session.SecurityStampAtIssue);
        Assert.Equal(1, persistence.AddCalls);
    }

    [Fact]
    public async Task SeparateSessionCreations_GetDifferentServerGeneratedIds()
    {
        var (service, _, _) = CreateService();
        var userId = Guid.NewGuid();

        var first = await CreateAsync(service, userId);
        var second = await CreateAsync(service, userId);

        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public void CreateSessionContract_DoesNotAcceptCallerSuppliedSessionId()
    {
        var method = typeof(SessionService).GetMethod(nameof(SessionService.CreateSessionAsync));
        Assert.NotNull(method);
        Assert.DoesNotContain(method!.GetParameters(), x => string.Equals(x.Name, "sessionId", StringComparison.OrdinalIgnoreCase));

        var storeMethod = typeof(SessionStore).GetMethod(nameof(SessionStore.CreateAsync));
        Assert.NotNull(storeMethod);
        Assert.DoesNotContain(storeMethod!.GetParameters(), x => string.Equals(x.Name, "sessionId", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ListSessions_ContainsOnlyRequestingUsersSessions()
    {
        var (service, _, _) = CreateService();
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        var a1 = await CreateAsync(service, userA);
        var a2 = await CreateAsync(service, userA);
        await CreateAsync(service, userB);

        var result = await service.ListSessionsAsync(userA, a1.Id, default);

        Assert.Equal(2, result.Sessions.Count);
        Assert.Contains(result.Sessions, x => x.Id == a1.Id);
        Assert.Contains(result.Sessions, x => x.Id == a2.Id);
    }

    [Fact]
    public async Task ListSessions_IdentifiesCurrentSession()
    {
        var (service, _, _) = CreateService();
        var userId = Guid.NewGuid();
        var current = await CreateAsync(service, userId);
        var other = await CreateAsync(service, userId);

        var result = await service.ListSessionsAsync(userId, current.Id, default);

        Assert.True(result.Sessions.Single(x => x.Id == current.Id).Current);
        Assert.False(result.Sessions.Single(x => x.Id == other.Id).Current);
    }

    [Fact]
    public async Task CrossUserSession_CannotBeRetrievedOrRevokedAsOwn()
    {
        var (service, persistence, _) = CreateService();
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        var sessionB = await CreateAsync(service, userB);
        var store = new SessionStore(persistence);

        Assert.Null(await store.GetForUserAsync(userA, sessionB.Id, default));
        Assert.False(await service.RevokeSessionAsync(userA, sessionB.Id, default));
        Assert.Null(persistence.GetRaw(sessionB.Id).RevokedAt);
    }

    [Fact]
    public async Task RevokeOneSession_Works()
    {
        var (service, persistence, _) = CreateService();
        var userId = Guid.NewGuid();
        var session = await CreateAsync(service, userId);

        var revoked = await service.RevokeSessionAsync(userId, session.Id, default);

        Assert.True(revoked);
        Assert.NotNull(persistence.GetRaw(session.Id).RevokedAt);
    }

    [Fact]
    public async Task RevokedSession_FailsValidation()
    {
        var (service, _, _) = CreateService();
        var userId = Guid.NewGuid();
        var session = await CreateAsync(service, userId);
        await service.RevokeSessionAsync(userId, session.Id, default);

        var result = await service.ValidateSessionAsync(session.Id, userId, ActiveUser, default);

        Assert.Equal(SessionValidationStatus.Revoked, result.Status);
        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task RevokeAllOtherSessions_PreservesCurrent()
    {
        var (service, persistence, _) = CreateService();
        var userId = Guid.NewGuid();
        var current = await CreateAsync(service, userId);
        var other1 = await CreateAsync(service, userId);
        var other2 = await CreateAsync(service, userId);

        var count = await service.RevokeAllOtherSessionsAsync(userId, current.Id, default);

        Assert.Equal(2, count);
        Assert.Null(persistence.GetRaw(current.Id).RevokedAt);
        Assert.NotNull(persistence.GetRaw(other1.Id).RevokedAt);
        Assert.NotNull(persistence.GetRaw(other2.Id).RevokedAt);
    }

    [Fact]
    public async Task RevokeAllSessions_InvalidatesEverySessionForUserOnly()
    {
        var (service, persistence, _) = CreateService();
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        var a1 = await CreateAsync(service, userA);
        var a2 = await CreateAsync(service, userA);
        var b1 = await CreateAsync(service, userB);

        var count = await service.RevokeAllSessionsAsync(userA, default);

        Assert.Equal(2, count);
        Assert.NotNull(persistence.GetRaw(a1.Id).RevokedAt);
        Assert.NotNull(persistence.GetRaw(a2.Id).RevokedAt);
        Assert.Null(persistence.GetRaw(b1.Id).RevokedAt);
    }

    [Fact]
    public async Task IdleExpiredSession_FailsValidation()
    {
        var (service, _, clock) = CreateService();
        var userId = Guid.NewGuid();
        var session = await CreateAsync(service, userId);
        clock.Advance(TimeSpan.FromMinutes(31));

        var result = await service.ValidateSessionAsync(session.Id, userId, ActiveUser, default);

        Assert.Equal(SessionValidationStatus.IdleExpired, result.Status);
    }

    [Fact]
    public async Task ActiveSessionInsideIdleWindow_Succeeds()
    {
        var (service, _, clock) = CreateService();
        var userId = Guid.NewGuid();
        var session = await CreateAsync(service, userId);
        clock.Advance(TimeSpan.FromMinutes(10));

        var result = await service.ValidateSessionAsync(session.Id, userId, ActiveUser, default);

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task AbsoluteExpiration_FailsRegardlessOfRecentActivity()
    {
        var options = new SessionOptions
        {
            IdleTimeout = TimeSpan.FromMinutes(10),
            AbsoluteLifetime = TimeSpan.FromMinutes(15),
            TouchInterval = TimeSpan.FromMinutes(5)
        };
        var (service, _, clock) = CreateService(options);
        var userId = Guid.NewGuid();
        var session = await CreateAsync(service, userId);

        clock.Advance(TimeSpan.FromMinutes(9));
        Assert.True((await service.ValidateSessionAsync(session.Id, userId, ActiveUser, default)).IsValid);
        clock.Advance(TimeSpan.FromMinutes(6));

        var result = await service.ValidateSessionAsync(session.Id, userId, ActiveUser, default);
        Assert.Equal(SessionValidationStatus.AbsoluteExpired, result.Status);
    }

    [Fact]
    public async Task TouchingSession_CannotExtendAbsoluteLifetime()
    {
        var options = new SessionOptions
        {
            IdleTimeout = TimeSpan.FromMinutes(10),
            AbsoluteLifetime = TimeSpan.FromMinutes(15),
            TouchInterval = TimeSpan.FromMinutes(5)
        };
        var (service, persistence, clock) = CreateService(options);
        var userId = Guid.NewGuid();
        var session = await CreateAsync(service, userId);
        var absolute = session.AbsoluteExpiresAt;

        clock.Advance(TimeSpan.FromMinutes(9));
        var result = await service.ValidateSessionAsync(session.Id, userId, ActiveUser, default);
        var stored = persistence.GetRaw(session.Id);

        Assert.True(result.IsValid);
        Assert.Equal(absolute, stored.AbsoluteExpiresAt);
        Assert.Equal(absolute, stored.ExpiresAt);
    }

    [Fact]
    public async Task ValidationAfterTouchInterval_UpdatesLastSeenAt()
    {
        var (service, persistence, clock) = CreateService();
        var userId = Guid.NewGuid();
        var session = await CreateAsync(service, userId);
        clock.Advance(TimeSpan.FromMinutes(6));

        var result = await service.ValidateSessionAsync(session.Id, userId, ActiveUser, default);

        Assert.True(result.LastSeenUpdated);
        Assert.Equal(clock.GetUtcNow(), persistence.GetRaw(session.Id).LastSeenAt);
        Assert.Equal(1, persistence.TouchCalls);
    }

    [Fact]
    public async Task ValidationBeforeTouchInterval_DoesNotWriteHeartbeat()
    {
        var (service, persistence, clock) = CreateService();
        var userId = Guid.NewGuid();
        var session = await CreateAsync(service, userId);
        clock.Advance(TimeSpan.FromMinutes(1));

        var result = await service.ValidateSessionAsync(session.Id, userId, ActiveUser, default);

        Assert.True(result.IsValid);
        Assert.False(result.LastSeenUpdated);
        Assert.Equal(0, persistence.TouchCalls);
    }

    [Fact]
    public async Task RepeatedValidation_DoesNotCreateDuplicateSession()
    {
        var (service, persistence, clock) = CreateService();
        var userId = Guid.NewGuid();
        var session = await CreateAsync(service, userId);
        clock.Advance(TimeSpan.FromMinutes(6));

        await service.ValidateSessionAsync(session.Id, userId, ActiveUser, default);
        clock.Advance(TimeSpan.FromMinutes(6));
        await service.ValidateSessionAsync(session.Id, userId, ActiveUser, default);

        Assert.Equal(1, persistence.AddCalls);
        Assert.Single((await service.ListSessionsAsync(userId, session.Id, default)).Sessions);
    }

    [Fact]
    public async Task Logout_RevokesCurrentPersistedSession()
    {
        var (service, persistence, _) = CreateService();
        var userId = Guid.NewGuid();
        var session = await CreateAsync(service, userId);

        Assert.True(await service.LogoutAsync(userId, session.Id, default));
        Assert.NotNull(persistence.GetRaw(session.Id).RevokedAt);
    }

    [Fact]
    public async Task SecuritySensitiveEvent_CanRevokeAllSessions()
    {
        var (service, _, _) = CreateService();
        var userId = Guid.NewGuid();
        var first = await CreateAsync(service, userId);
        var second = await CreateAsync(service, userId);

        Assert.Equal(2, await service.RevokeForSecurityEventAsync(userId, default));
        Assert.Equal(SessionValidationStatus.Revoked, (await service.ValidateSessionAsync(first.Id, userId, ActiveUser, default)).Status);
        Assert.Equal(SessionValidationStatus.Revoked, (await service.ValidateSessionAsync(second.Id, userId, ActiveUser, default)).Status);
    }

    [Fact]
    public async Task SecurityStampMismatch_InvalidatesSession()
    {
        var (service, _, _) = CreateService();
        var userId = Guid.NewGuid();
        var session = await CreateAsync(service, userId);

        var result = await service.ValidateSessionAsync(session.Id, userId, new SessionUserSecurityState(true, "changed"), default);

        Assert.Equal(SessionValidationStatus.SecurityStampChanged, result.Status);
    }

    [Fact]
    public async Task DisabledUser_InvalidatesSession()
    {
        var (service, _, _) = CreateService();
        var userId = Guid.NewGuid();
        var session = await CreateAsync(service, userId);

        var result = await service.ValidateSessionAsync(session.Id, userId, new SessionUserSecurityState(false, "stamp-1"), default);

        Assert.Equal(SessionValidationStatus.UserInvalid, result.Status);
    }

    [Fact]
    public async Task PurgeExpired_RemovesOnlyRowsOutsideRetentionWindow()
    {
        var options = new SessionOptions { CleanupRetention = TimeSpan.FromDays(1) };
        var (service, _, clock) = CreateService(options);
        var userId = Guid.NewGuid();
        await CreateAsync(service, userId);
        clock.Advance(TimeSpan.FromDays(32));

        Assert.Equal(1, await service.PurgeExpiredAsync(default));
        Assert.Empty((await service.ListSessionsAsync(userId, null, default)).Sessions);
    }

    [Fact]
    public async Task RevokedSession_IsShownAsInactiveInOwnSessionList()
    {
        var (service, _, _) = CreateService();
        var userId = Guid.NewGuid();
        var session = await CreateAsync(service, userId);
        await service.RevokeSessionAsync(userId, session.Id, default);

        var dto = await service.ListSessionsAsync(userId, session.Id, default);

        Assert.False(Assert.Single(dto.Sessions).Active);
    }

    private static async Task<UserSession> CreateAsync(SessionService service, Guid userId) =>
        await service.CreateSessionAsync(userId, "stamp-1", "Mozilla/5.0 Windows Chrome/1", "192.0.2.10", default);

    private static (SessionService Service, FakeSessionPersistence Persistence, TestTimeProvider Clock) CreateService(SessionOptions? options = null)
    {
        var persistence = new FakeSessionPersistence();
        var clock = new TestTimeProvider(Start);
        var store = new SessionStore(persistence);
        var service = new SessionService(store, Options.Create(options ?? new SessionOptions()), clock);
        return (service, persistence, clock);
    }
}
