using Microsoft.AspNetCore.Identity;

namespace FullWorth.Web.Modules.Auth;

public sealed class AuthOptions
{
    public const string SectionName = "Auth";
    public const int DefaultMaxFailedAccessAttempts = 5;

    public int MinimumPasswordLength { get; set; } = 12;

    public int MaxFailedAccessAttempts { get; set; } = DefaultMaxFailedAccessAttempts;

    public TimeSpan LockoutDuration { get; set; } = TimeSpan.FromMinutes(15);

    public void Apply(IdentityOptions options)
    {
        options.Stores.SchemaVersion = IdentitySchemaVersions.Version2;
        options.Stores.MaxLengthForKeys = 128;

        options.User.RequireUniqueEmail = true;

        options.Password.RequiredLength = MinimumPasswordLength;
        options.Password.RequiredUniqueChars = 1;
        options.Password.RequireDigit = false;
        options.Password.RequireLowercase = false;
        options.Password.RequireUppercase = false;
        options.Password.RequireNonAlphanumeric = false;

        options.Lockout.AllowedForNewUsers = true;
        options.Lockout.MaxFailedAccessAttempts = MaxFailedAccessAttempts;
        options.Lockout.DefaultLockoutTimeSpan = LockoutDuration;

        options.SignIn.RequireConfirmedAccount = false;
        options.SignIn.RequireConfirmedEmail = false;
    }
}
