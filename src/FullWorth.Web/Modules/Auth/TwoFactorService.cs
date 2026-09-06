using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Identity;

namespace FullWorth.Web.Modules.Auth;

public sealed record TwoFactorStatusDto(bool Enabled);
public sealed record TwoFactorSetupDto(string SharedKey, string AuthenticatorUri);
public sealed record TwoFactorCodeRequest(string Code);

public sealed class TwoFactorService(UserManager<AuthUser> users)
{
    public async Task<TwoFactorStatusDto?> GetStatusAsync(ClaimsPrincipal principal)
    {
        var user = await GetUserAsync(principal);
        return user is null ? null : new(await users.GetTwoFactorEnabledAsync(user));
    }

    public async Task<TwoFactorSetupDto?> BeginSetupAsync(ClaimsPrincipal principal)
    {
        var user = await GetUserAsync(principal);
        if (user is null) return null;
        if (await users.GetTwoFactorEnabledAsync(user))
            throw new InvalidOperationException("two_factor_enabled");

        var key = await users.GetAuthenticatorKeyAsync(user);
        if (string.IsNullOrWhiteSpace(key))
        {
            await users.ResetAuthenticatorKeyAsync(user);
            key = await users.GetAuthenticatorKeyAsync(user);
        }

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("authenticator_key_unavailable");

        var email = user.Email ?? user.UserName ?? user.Id.ToString();
        return new(key, BuildUri("FullWorth", email, key));
    }

    public async Task<bool> EnableAsync(ClaimsPrincipal principal, string? code)
    {
        var user = await GetUserAsync(principal);
        if (user is null) return false;

        var valid = await users.VerifyTwoFactorTokenAsync(
            user,
            TokenOptions.DefaultAuthenticatorProvider,
            NormalizeCode(code));
        if (!valid) return false;

        return (await users.SetTwoFactorEnabledAsync(user, true)).Succeeded;
    }

    public async Task<bool> DisableAsync(ClaimsPrincipal principal, string? code)
    {
        var user = await GetUserAsync(principal);
        if (user is null) return false;
        if (!await users.GetTwoFactorEnabledAsync(user)) return true;

        var valid = await users.VerifyTwoFactorTokenAsync(
            user,
            TokenOptions.DefaultAuthenticatorProvider,
            NormalizeCode(code));
        if (!valid) return false;

        var disabled = await users.SetTwoFactorEnabledAsync(user, false);
        if (!disabled.Succeeded) return false;

        await users.ResetAuthenticatorKeyAsync(user);
        return true;
    }

    private async Task<AuthUser?> GetUserAsync(ClaimsPrincipal principal)
    {
        var id = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(id, out var authUserId)
            ? await users.FindByIdAsync(authUserId.ToString())
            : null;
    }

    private static string NormalizeCode(string? code) =>
        (code ?? string.Empty).Replace(" ", string.Empty).Replace("-", string.Empty).Trim();

    private static string BuildUri(string issuer, string account, string key)
    {
        static string E(string value) => Uri.EscapeDataString(value);
        return $"otpauth://totp/{E(issuer)}:{E(account)}?secret={E(key)}&issuer={E(issuer)}&digits=6";
    }
}
