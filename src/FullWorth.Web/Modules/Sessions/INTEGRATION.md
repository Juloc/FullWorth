# C2 session integration contract

C2 deliberately does not edit `Program.cs`, `Data/AuthDbContext.cs`, `Migrations/**` or C1 Auth types.

## AuthDbContext

Integrator C must add `DbSet<UserSession>` to the C1 `AuthDbContext` and map `UserSession.AuthUserId -> AuthUser.Id` with cascade delete. Auth-session data may be deleted with the AuthUser; finance history remains in Finance.Backend and is unrelated.

Map UTC `DateTimeOffset` columns for `CreatedAt`, `LastSeenAt`, `ExpiresAt`, `AbsoluteExpiresAt`, `RevokedAt`. Apply max lengths from `UserSession` for `DeviceName`, `UserAgent`, `IpAddress` and `SecurityStampAtIssue`.

Required indexes only:

- `AuthUserId`
- `RevokedAt`
- `AbsoluteExpiresAt`

Create the AuthDbContext migration after C1 and C2 are integrated.

## Persistence adapter

Implement `ISessionPersistence` against `AuthDbContext` inside the Sessions feature. `GetForUserAsync` and `RevokeAsync` must scope by both `AuthUserId` and session ID. `RevokeAllOtherAsync` and `RevokeAllAsync` should be one set-based database update each so partial revocation is avoided. `TouchAsync` must update only `LastSeenAt` and `ExpiresAt`; it must never change `AbsoluteExpiresAt` or revive a revoked session. `PurgeExpiredAsync` should delete rows whose absolute expiry or revocation timestamp is older than the supplied retention cutoffs.

## DI and cookie wiring

Register `SessionOptions`, `ISessionPersistence`, `SessionStore`, `SessionService` and `TimeProvider.System`. Apply `SessionCookiePolicy.Apply(...)` to the ASP.NET Core Identity application cookie. Production resolves to `HttpOnly=true`, `SecurePolicy=Always`, `SameSite=Lax`; development uses `SameAsRequest` rather than forcing insecure cookies globally.

Map `SessionEndpoints.MapSessionEndpoints()` after authentication/authorization are configured. The unsafe revoke endpoints intentionally do not add the later global CSRF/antiforgery wave themselves.

## Login/session fixation

After credentials are verified, call `SessionService.CreateSessionAsync`. The method creates a new server-generated GUID; no login request accepts a session ID. Add `SessionClaims.CreateSessionIdClaim(session.Id)` to the new authenticated principal/cookie. For Identity, prefer `CheckPasswordSignInAsync` followed by `SignInWithClaimsAsync(..., isPersistent: false, additionalClaims: [session claim])` so the authenticated cookie is issued only after the new persistent session exists.

Passkeys and future TOTP-assisted login must call the same `CreateSessionAsync` path.

## Per-request validation

In the Identity application-cookie `OnValidatePrincipal` event:

1. parse `ClaimTypes.NameIdentifier` and `session_id`;
2. load the current AuthUser through Identity and determine whether sign-in is still allowed;
3. read the current Identity security stamp;
4. call `ValidateSessionAsync(sessionId, authUserId, new SessionUserSecurityState(isActive, stamp), ct)`;
5. on any non-valid result, call `RejectPrincipal()` and sign out the cookie.

IP address changes alone must never invalidate a session.

## Logout and sensitive changes

Logout order: revoke the current persisted session with `SessionService.LogoutAsync`, then call Identity sign-out.

Password change: update the Identity security stamp, revoke all other sessions, revoke/rotate the current session, create a new session and reissue the current cookie with the new security stamp.

Password reset, account disable or security-credential reset: update the Identity security stamp and call `RevokeForSecurityEventAsync`/`RevokeAllSessionsAsync`. A later successful login creates a fresh session.

Recovery-code use may call `RevokeAllOtherSessionsAsync` or `RevokeAllSessionsAsync` according to the final C3 flow.

## Cleanup

No background worker is required in C2. Integrator C may call `PurgeExpiredAsync` from a low-frequency maintenance path. The default retention keeps expired/revoked records for 30 days before deletion, preventing unbounded growth without per-request cleanup writes.
