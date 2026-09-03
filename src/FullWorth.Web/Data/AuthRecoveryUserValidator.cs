using FullWorth.Web.Modules.Auth;
using FullWorth.Web.Modules.Recovery;
using Microsoft.AspNetCore.Identity;

namespace FullWorth.Web.Data;

public sealed class AuthRecoveryUserValidator(UserManager<AuthUser> users) : IRecoveryUserValidator
{
    public async Task<bool> IsValidRecoveryUserAsync(Guid authUserId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (authUserId == Guid.Empty)
            return false;

        var user = await users.FindByIdAsync(authUserId.ToString());
        return user is not null && !user.IsDisabled;
    }
}
