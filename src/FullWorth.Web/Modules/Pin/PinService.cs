using System.Globalization;
using FullWorth.Web.Modules.Auth;
using Microsoft.AspNetCore.Identity;

namespace FullWorth.Web.Modules.Pin;

public enum PinVerifyStatus
{
    Success,
    WrongPin,
    NotSet,
    Locked
}

/// <summary>
/// Manages the app-lock PIN (the fallback factor for the inactivity lock; passkeys are primary).
/// The PIN only unlocks an already-authenticated session — it is never a primary credential — so it
/// is stored as a per-user Identity authentication token (AspNetUserTokens), which needs no schema
/// change, and hashed with the same <see cref="IPasswordHasher{TUser}"/> (PBKDF2) as passwords.
/// Repeated wrong entries trigger a short lockout so a short PIN cannot be brute-forced.
/// </summary>
public sealed class PinService
{
    private const string Provider = "Finance.Lock";
    private const string HashToken = "pin";
    private const string FailToken = "pin-fail";
    private const string UntilToken = "pin-until";
    private const int MaxAttempts = 5;
    private const int MinLength = 4;
    private const int MaxLength = 12;
    private static readonly TimeSpan LockoutWindow = TimeSpan.FromMinutes(5);

    private readonly UserManager<AuthUser> users;
    private readonly IPasswordHasher<AuthUser> hasher;
    private readonly TimeProvider clock;

    public PinService(UserManager<AuthUser> users, IPasswordHasher<AuthUser> hasher, TimeProvider clock)
    {
        this.users = users;
        this.hasher = hasher;
        this.clock = clock;
    }

    public static bool IsValidPin(string? pin) =>
        pin is not null && pin.Length is >= MinLength and <= MaxLength && pin.All(char.IsAsciiDigit);

    public async Task<bool> HasPinAsync(Guid authUserId)
    {
        var user = await users.FindByIdAsync(authUserId.ToString());
        if (user is null)
            return false;
        return !string.IsNullOrEmpty(await users.GetAuthenticationTokenAsync(user, Provider, HashToken));
    }

    public async Task<bool> SetPinAsync(Guid authUserId, string? pin)
    {
        if (!IsValidPin(pin))
            return false;
        var user = await users.FindByIdAsync(authUserId.ToString());
        if (user is null || user.IsDisabled)
            return false;

        await users.SetAuthenticationTokenAsync(user, Provider, HashToken, hasher.HashPassword(user, pin!));
        await users.SetAuthenticationTokenAsync(user, Provider, FailToken, "0");
        await users.RemoveAuthenticationTokenAsync(user, Provider, UntilToken);
        return true;
    }

    public async Task<bool> RemovePinAsync(Guid authUserId)
    {
        var user = await users.FindByIdAsync(authUserId.ToString());
        if (user is null)
            return false;
        await users.RemoveAuthenticationTokenAsync(user, Provider, HashToken);
        await users.RemoveAuthenticationTokenAsync(user, Provider, FailToken);
        await users.RemoveAuthenticationTokenAsync(user, Provider, UntilToken);
        return true;
    }

    public async Task<PinVerifyStatus> VerifyPinAsync(Guid authUserId, string? pin)
    {
        var user = await users.FindByIdAsync(authUserId.ToString());
        if (user is null || user.IsDisabled)
            return PinVerifyStatus.NotSet;
        var hash = await users.GetAuthenticationTokenAsync(user, Provider, HashToken);
        if (string.IsNullOrEmpty(hash))
            return PinVerifyStatus.NotSet;

        var now = clock.GetUtcNow();
        var lockedUntil = ParseUnixSeconds(await users.GetAuthenticationTokenAsync(user, Provider, UntilToken));
        if (lockedUntil is not null && now < lockedUntil)
            return PinVerifyStatus.Locked;

        if (hasher.VerifyHashedPassword(user, hash, pin ?? string.Empty) == PasswordVerificationResult.Failed)
        {
            var fails = ParseInt(await users.GetAuthenticationTokenAsync(user, Provider, FailToken)) + 1;
            if (fails >= MaxAttempts)
            {
                await users.SetAuthenticationTokenAsync(user, Provider, FailToken, "0");
                await users.SetAuthenticationTokenAsync(user, Provider, UntilToken,
                    now.Add(LockoutWindow).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
                return PinVerifyStatus.Locked;
            }
            await users.SetAuthenticationTokenAsync(user, Provider, FailToken, fails.ToString(CultureInfo.InvariantCulture));
            return PinVerifyStatus.WrongPin;
        }

        await users.SetAuthenticationTokenAsync(user, Provider, FailToken, "0");
        await users.RemoveAuthenticationTokenAsync(user, Provider, UntilToken);
        return PinVerifyStatus.Success;
    }

    private static int ParseInt(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;

    private static DateTimeOffset? ParseUnixSeconds(string? value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;
}
