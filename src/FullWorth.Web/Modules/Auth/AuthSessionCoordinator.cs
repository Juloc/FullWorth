using System.Security.Claims;
using FullWorth.Web.Modules.Recovery;
using FullWorth.Web.Modules.Sessions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;

namespace FullWorth.Web.Modules.Auth;

public sealed class AuthSessionCoordinator(
    AuthService auth,
    UserManager<AuthUser> users,
    SignInManager<AuthUser> signInManager,
    SessionService sessions,
    RecoveryService recovery)
{
    public async Task<LoginResultDto> LoginAsync(LoginRequest request, HttpContext context, CancellationToken ct)
    {
        var result = await auth.ValidatePasswordAsync(request.Email, request.Password);
        if (!result.Succeeded || result.User is null)
            return LoginResultDto.InvalidCredentials();

        var user = await users.FindByIdAsync(result.User.Id.ToString());
        if (user is null || user.IsDisabled || await users.IsLockedOutAsync(user))
            return LoginResultDto.InvalidCredentials();

        if (await users.GetTwoFactorEnabledAsync(user))
        {
            if (string.IsNullOrWhiteSpace(request.Code))
                return LoginResultDto.TwoFactorRequired();

            var validCode = await users.VerifyTwoFactorTokenAsync(
                user,
                TokenOptions.DefaultAuthenticatorProvider,
                request.Code.Replace(" ", string.Empty).Replace("-", string.Empty).Trim());
            if (!validCode)
            {
                await users.AccessFailedAsync(user);
                return LoginResultDto.InvalidTwoFactor();
            }

            await users.ResetAccessFailedCountAsync(user);
        }

        if (!await SignInUserAsync(user, context, ct))
            return LoginResultDto.InvalidCredentials();

        return result;
    }

    public async Task<bool> SignInUserAsync(AuthUser user, HttpContext context, CancellationToken ct)
    {
        if (user.IsDisabled || await users.IsLockedOutAsync(user))
            return false;

        var securityStamp = await users.GetSecurityStampAsync(user);
        var session = await sessions.CreateSessionAsync(
            user.Id,
            securityStamp,
            context.Request.Headers.UserAgent.ToString(),
            context.Connection.RemoteIpAddress?.ToString(),
            ct);

        try
        {
            await signInManager.SignInWithClaimsAsync(
                user,
                isPersistent: true,
                [SessionClaims.CreateSessionIdClaim(session.Id)]);
        }
        catch
        {
            await sessions.RevokeSessionAsync(user.Id, session.Id, ct);
            throw;
        }

        return true;
    }

    /// <summary>
    /// Anonymous account recovery using a single-use recovery code. Enumeration-safe (uniform
    /// failure for unknown email or wrong code) and independent of password lockout, so a locked-out
    /// user can still recover. Only an explicit admin disable blocks it. On success a session is
    /// issued so the user can sign in and then change their password / manage passkeys.
    /// </summary>
    public async Task<bool> RedeemRecoveryCodeAsync(string? email, string? recoveryCode, HttpContext context, CancellationToken ct)
    {
        var user = await users.FindByEmailAsync((email ?? string.Empty).Trim());
        if (user is null || user.IsDisabled)
            return false;

        if (!await recovery.ValidateAndConsumeAsync(user.Id, recoveryCode, ct))
            return false;

        var securityStamp = await users.GetSecurityStampAsync(user);
        var session = await sessions.CreateSessionAsync(
            user.Id,
            securityStamp,
            context.Request.Headers.UserAgent.ToString(),
            context.Connection.RemoteIpAddress?.ToString(),
            ct);

        try
        {
            await signInManager.SignInWithClaimsAsync(
                user,
                // Persistent so the login survives a browser restart and app redeploy (the cookie's
                // absolute ceiling is still the DB session's AbsoluteLifetime; see SessionOptions).
                isPersistent: true,
                [SessionClaims.CreateSessionIdClaim(session.Id)]);
        }
        catch
        {
            await sessions.RevokeSessionAsync(user.Id, session.Id, ct);
            throw;
        }

        return true;
    }

    public async Task LogoutAsync(ClaimsPrincipal principal, CancellationToken ct)
    {
        if (TryGetCurrent(principal, out var authUserId, out var sessionId))
            await sessions.LogoutAsync(authUserId, sessionId, ct);

        await signInManager.SignOutAsync();
    }

    public async Task<AuthActionResultDto> ChangePasswordAsync(
        ClaimsPrincipal principal,
        string currentPassword,
        string newPassword,
        CancellationToken ct)
    {
        var subject = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(subject, out var authUserId))
            return AuthActionResultDto.Failure("Unable to change password.");

        var result = await auth.ChangePasswordAsync(authUserId, currentPassword, newPassword);
        if (!result.Succeeded)
            return result;

        await sessions.RevokeAllSessionsAsync(authUserId, ct);
        await signInManager.SignOutAsync();
        return result;
    }

    public async Task<AuthActionResultDto> ResetPasswordAsync(
        string email,
        string token,
        string newPassword,
        CancellationToken ct)
    {
        var result = await auth.ResetPasswordAsync(email, token, newPassword);
        if (!result.Succeeded)
            return result;

        var user = await users.FindByEmailAsync(email.Trim());
        if (user is not null)
            await sessions.RevokeAllSessionsAsync(user.Id, ct);

        return result;
    }

    public Task<int> RevokeAllForUserAsync(Guid authUserId, CancellationToken ct) =>
        sessions.RevokeForSecurityEventAsync(authUserId, ct);

    private static bool TryGetCurrent(ClaimsPrincipal principal, out Guid authUserId, out Guid sessionId)
    {
        authUserId = Guid.Empty;
        sessionId = Guid.Empty;
        return Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out authUserId)
            && SessionClaims.TryGetSessionId(principal, out sessionId);
    }
}
