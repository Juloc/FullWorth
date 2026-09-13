using System.Security.Cryptography;
using System.Text;
using FullWorth.Web.Data;
using FullWorth.Web.Modules.Auth;
using FullWorth.Web.Modules.Sessions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Web.Modules.Admin;

/// <summary>
/// A short-lived, second-factor-backed permission to look at stored secrets.
///
/// A row rather than a flag on <see cref="UserSession"/>, because that row is touched on every single
/// request and this one must not be. It is bound to <c>(AuthUserId, SessionId)</c>, and the check is a
/// join onto the session — so everything that already ends a session (signing out, a password change,
/// a security-stamp bump, "end sessions" in the admin menu, disabling the account) ends the elevation
/// too, without one line of new code.
///
/// Deliberately narrow: five minutes absolute, ten reveals, and the four secrets that own the whole
/// installation need the factor again for every single one.
/// </summary>
public sealed class AdminElevation
{
    /// <summary>Absolute, not sliding. An elevation that renews itself is a permanent one.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How many secrets one elevation may reveal. A number, because "as many as you like within five
    /// minutes" is a script, and a script that exports the installation is exactly the thing a stolen
    /// session should not be able to do quietly.
    /// </summary>
    public const int RevealBudget = 10;

    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AuthUserId { get; set; }
    public Guid SessionId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public int RevealsUsed { get; set; }

    /// <summary>"totp" or "password" — recorded so the audit trail says how it was proven.</summary>
    public string Factor { get; set; } = string.Empty;

    /// <summary>
    /// SHA-256 of the TOTP code this elevation was granted for. A code is valid for a window, not an
    /// instant, so without this one code shoulder-surfed from a screen buys two elevations.
    /// </summary>
    public string? CodeHash { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }
}

/// <summary>
/// Failed step-up attempts, counted apart from the sign-in lockout.
///
/// Using <c>SignInManager</c> with <c>lockoutOnFailure</c> here would hand an attacker who already
/// holds a session a way to lock the real administrator out of signing in, while their own stolen
/// session keeps working. So the vault counts its own, and a lockout here blocks only the vault.
/// </summary>
public sealed class AdminElevationLockout
{
    public const int MaxFailures = 5;
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    public Guid AuthUserId { get; set; }
    public int Failures { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public enum ElevationOutcome
{
    Granted,
    WrongFactor,
    LockedOut,
    CodeAlreadyUsed,
    NoSession
}

public sealed record ElevationResult(ElevationOutcome Outcome, AdminElevation? Elevation = null)
{
    public bool Granted => Outcome == ElevationOutcome.Granted;
}

public sealed class AdminElevationService(
    AuthDbContext db,
    UserManager<AuthUser> users,
    TimeProvider clock)
{
    /// <summary>
    /// Proves the factor and opens a window. <paramref name="secret"/> is a TOTP code when the account
    /// has two-factor on, and the account password otherwise.
    /// </summary>
    public async Task<ElevationResult> ElevateAsync(
        AuthUser user, Guid sessionId, string? secret, CancellationToken ct)
    {
        var now = clock.GetUtcNow();

        var lockout = await db.Set<AdminElevationLockout>()
            .SingleOrDefaultAsync(x => x.AuthUserId == user.Id, ct);
        if (lockout?.LockedUntil is { } until && until > now)
            return new(ElevationOutcome.LockedOut);

        var sessionIsLive = await db.UserSessions.AnyAsync(
            s => s.Id == sessionId && s.AuthUserId == user.Id && s.RevokedAt == null && s.ExpiresAt > now,
            ct);
        if (!sessionIsLive) return new(ElevationOutcome.NoSession);

        // Which factor, decided here and not by the caller. With TOTP on there is no password branch at
        // all - if the weaker factor stayed available beside the stronger one, the second factor would
        // be decoration. The inventory response tells the browser which one to ask for.
        var twoFactor = await users.GetTwoFactorEnabledAsync(user);
        var code = (secret ?? string.Empty).Trim();
        string factor;

        if (twoFactor)
        {
            factor = "totp";
            var hash = HashOf(code);

            // Replay inside the code's own validity window. Checked before verifying, so a reused code
            // never reaches the verifier and never resets the failure counter either.
            var reused = await db.Set<AdminElevation>().AnyAsync(
                x => x.AuthUserId == user.Id && x.CodeHash == hash && x.CreatedAt > now.AddMinutes(-5), ct);
            if (reused) return new(ElevationOutcome.CodeAlreadyUsed);

            var valid = await users.VerifyTwoFactorTokenAsync(
                user, TokenOptions.DefaultAuthenticatorProvider, code.Replace(" ", string.Empty));
            if (!valid) return await FailAsync(user, lockout, now, ct);

            return new(ElevationOutcome.Granted, await GrantAsync(user, sessionId, now, factor, hash, ct));
        }

        if (string.IsNullOrEmpty(code)) return await FailAsync(user, lockout, now, ct);

        factor = "password";
        // CheckPasswordAsync, not SignInManager: see AdminElevationLockout.
        if (!await users.CheckPasswordAsync(user, code)) return await FailAsync(user, lockout, now, ct);

        return new(ElevationOutcome.Granted, await GrantAsync(user, sessionId, now, factor, null, ct));
    }

    /// <summary>
    /// The live elevation for this session, or null. Reading it is also what enforces the budget: the
    /// caller passes <paramref name="consume"/> when a secret is actually about to be handed out.
    /// </summary>
    public async Task<AdminElevation?> CurrentAsync(
        Guid authUserId, Guid sessionId, bool consume, CancellationToken ct)
    {
        var now = clock.GetUtcNow();

        var elevation = await db.Set<AdminElevation>()
            .Where(x => x.AuthUserId == authUserId
                        && x.SessionId == sessionId
                        && x.RevokedAt == null
                        && x.ExpiresAt > now
                        && x.RevealsUsed < AdminElevation.RevealBudget)
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (elevation is null) return null;

        // The join the whole design rests on: an elevation cannot outlive its session.
        var sessionIsLive = await db.UserSessions.AnyAsync(
            s => s.Id == sessionId && s.AuthUserId == authUserId && s.RevokedAt == null && s.ExpiresAt > now,
            ct);
        if (!sessionIsLive) return null;

        // Nor the administrator role. A demotion has to take the vault with it, and IsAdmin is the one
        // notion of administrator this check is allowed to use - the Intelligence grant in the backend
        // is bootstrapped onto the oldest finance user and never follows a demotion here.
        var stillAdmin = await db.Users.AnyAsync(
            u => u.Id == authUserId && u.IsAdmin && !u.IsDisabled, ct);
        if (!stillAdmin) return null;

        if (consume)
        {
            elevation.RevealsUsed++;
            await db.SaveChangesAsync(ct);
        }

        return elevation;
    }

    /// <summary>
    /// Ends every elevation this account holds. Called when the vault itself has a reason to distrust
    /// the session - a factor that failed too often, for instance.
    /// </summary>
    public async Task RevokeAllAsync(Guid authUserId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        await db.Set<AdminElevation>()
            .Where(x => x.AuthUserId == authUserId && x.RevokedAt == null)
            .ExecuteUpdateAsync(set => set.SetProperty(x => x.RevokedAt, now), ct);
    }

    private async Task<AdminElevation> GrantAsync(
        AuthUser user, Guid sessionId, DateTimeOffset now, string factor, string? codeHash,
        CancellationToken ct)
    {
        // One elevation at a time. Without this, asking twice would leave two windows open and the
        // budget would quietly double.
        await db.Set<AdminElevation>()
            .Where(x => x.AuthUserId == user.Id && x.RevokedAt == null)
            .ExecuteUpdateAsync(set => set.SetProperty(x => x.RevokedAt, now), ct);

        var elevation = new AdminElevation
        {
            AuthUserId = user.Id,
            SessionId = sessionId,
            CreatedAt = now,
            ExpiresAt = now.Add(AdminElevation.Lifetime),
            Factor = factor,
            CodeHash = codeHash
        };
        db.Add(elevation);

        var lockout = await db.Set<AdminElevationLockout>()
            .SingleOrDefaultAsync(x => x.AuthUserId == user.Id, ct);
        if (lockout is not null)
        {
            lockout.Failures = 0;
            lockout.LockedUntil = null;
            lockout.UpdatedAt = now;
        }

        await db.SaveChangesAsync(ct);
        return elevation;
    }

    private async Task<ElevationResult> FailAsync(
        AuthUser user, AdminElevationLockout? lockout, DateTimeOffset now, CancellationToken ct)
    {
        if (lockout is null)
        {
            lockout = new AdminElevationLockout { AuthUserId = user.Id };
            db.Add(lockout);
        }

        lockout.Failures++;
        lockout.UpdatedAt = now;
        if (lockout.Failures >= AdminElevationLockout.MaxFailures)
        {
            lockout.LockedUntil = now.Add(AdminElevationLockout.LockoutDuration);
            lockout.Failures = 0;
        }

        await db.SaveChangesAsync(ct);
        return new(lockout.LockedUntil is not null ? ElevationOutcome.LockedOut : ElevationOutcome.WrongFactor);
    }

    internal static string HashOf(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
