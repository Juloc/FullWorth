using FullWorth.Web.Modules.Auth;
using FullWorth.Web.Modules.Sessions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;

namespace FullWorth.Web.Modules.Passkeys;

public sealed record PasskeyUserAccount(Guid Id, string Name, string DisplayName);

public interface IPasskeyUserLookup
{
    Task<PasskeyUserAccount?> GetEligibleAsync(Guid authUserId, CancellationToken cancellationToken = default);
}

public sealed class PasskeyUserLookup(UserManager<AuthUser> users) : IPasskeyUserLookup
{
    public async Task<PasskeyUserAccount?> GetEligibleAsync(Guid authUserId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var user = await users.FindByIdAsync(authUserId.ToString());
        // Deliberately not gated by IsLockedOutAsync: password lockout must not disable passkey
        // sign-in, or an attacker who only knows the email could lock a passkey user out of a factor
        // they never use. Password lockout stays on the password login path only.
        if (user is null || user.IsDisabled)
            return null;

        var name = user.Email ?? user.UserName ?? user.Id.ToString("D");
        return new PasskeyUserAccount(user.Id, name, name);
    }
}

public sealed class PasskeySessionSignInService(
    UserManager<AuthUser> users,
    SignInManager<AuthUser> signInManager,
    SessionService sessions)
{
    public async Task<bool> SignInAsync(Guid authUserId, HttpContext context, CancellationToken cancellationToken = default)
    {
        var user = await users.FindByIdAsync(authUserId.ToString());
        // Deliberately not gated by IsLockedOutAsync: password lockout must not disable passkey
        // sign-in, or an attacker who only knows the email could lock a passkey user out of a factor
        // they never use. Password lockout stays on the password login path only.
        if (user is null || user.IsDisabled)
            return false;

        var securityStamp = await users.GetSecurityStampAsync(user);
        var session = await sessions.CreateSessionAsync(
            user.Id,
            securityStamp,
            context.Request.Headers.UserAgent.ToString(),
            context.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);

        try
        {
            await signInManager.SignInWithClaimsAsync(
                user,
                // Persistent so the login survives a browser restart and app redeploy (bounded by the
                // DB session's AbsoluteLifetime; see SessionOptions).
                isPersistent: true,
                [SessionClaims.CreateSessionIdClaim(session.Id)]);
        }
        catch
        {
            await sessions.RevokeSessionAsync(user.Id, session.Id, cancellationToken);
            throw;
        }

        return true;
    }
}
