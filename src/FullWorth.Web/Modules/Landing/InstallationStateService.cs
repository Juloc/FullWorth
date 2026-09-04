using FullWorth.Web.Modules.Auth;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FullWorth.Web.Modules.Landing;

/// <summary>
/// Computes the sanitized <see cref="InstallationState"/> for the landing/setup surface. "Initialized"
/// means at least one login exists; the concrete count and identities are never exposed. Registration
/// mode is only meaningful for a multi-user install and is forced to <see cref="RegistrationMode.Disabled"/>
/// for a single-user install so a single-user landing can never advertise self-service registration.
/// </summary>
public sealed class InstallationStateService(UserManager<AuthUser> userManager, IOptions<InstallationOptions> options)
{
    public async Task<InstallationState> GetAsync(CancellationToken ct)
    {
        var initialized = await userManager.Users.AnyAsync(ct);
        var mode = options.Value.Mode;
        var registration = mode == InstallationMode.MultiUser
            ? options.Value.Registration
            : RegistrationMode.Disabled;
        return new InstallationState(mode, initialized, registration);
    }
}
