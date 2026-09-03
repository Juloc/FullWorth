using Microsoft.AspNetCore.Identity;

namespace FullWorth.Web.Modules.Auth;

public sealed class AuthUser : IdentityUser<Guid>
{
    public Guid FinanceUserId { get; set; }

    public bool IsDisabled { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
