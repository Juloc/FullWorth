namespace FullWorth.Web.Modules.Auth;

public sealed record LoginRequest(string Email, string Password);

public sealed record RegisterRequest(string Email, string Password, string DisplayName, bool AcceptTerms, bool ConfirmAdult);

public sealed record RegisterResultDto(
    bool Succeeded,
    string? Error,
    AuthUserDto? User,
    IReadOnlyList<string> Errors)
{
    public static RegisterResultDto Success(AuthUserDto user) => new(true, null, user, Array.Empty<string>());
    public static RegisterResultDto Disabled() => new(false, "registration_disabled", null, Array.Empty<string>());
    public static RegisterResultDto Invalid() => new(false, "invalid_registration", null, Array.Empty<string>());
    public static RegisterResultDto Unavailable() => new(false, "registration_unavailable", null, Array.Empty<string>());
    public static RegisterResultDto Failed() => new(false, "registration_failed", null, Array.Empty<string>());
}

public sealed record LoginResultDto(bool Succeeded, string? Error, AuthUserDto? User)
{
    public static LoginResultDto Success(AuthUserDto user) => new(true, null, user);

    public static LoginResultDto InvalidCredentials() => new(false, "Invalid credentials.", null);
}

public sealed record CreateAuthUserRequest(Guid FinanceUserId, string Email, string Password);

public sealed record CreateAuthUserResultDto(bool Succeeded, AuthUserDto? User, IReadOnlyList<string> Errors);

public sealed record AuthUserDto(
    Guid Id,
    Guid FinanceUserId,
    string Email,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public sealed record RequestPasswordResetRequest(string Email);

public sealed record ResetPasswordRequest(string Email, string Token, string NewPassword);

public sealed record PasswordResetRequestResultDto(string Message);

public sealed record AuthActionResultDto(bool Succeeded, IReadOnlyList<string> Errors)
{
    public static AuthActionResultDto Success() => new(true, Array.Empty<string>());

    public static AuthActionResultDto Failure(params string[] errors) => new(false, errors);
}

public sealed record PasswordResetTokenResult(Guid AuthUserId, string Token);

// Multi-user sharing: an invitee claims an owner-issued invite by setting their own password.
public sealed record ClaimInviteRequest(string Token, string NewPassword);

// ExistingLogin = the invitee already had a login (e.g. shared into a second space); their access was
// granted, but we can't sign them in without their existing password, so the UI tells them to log in.
public sealed record ClaimInviteResultDto(bool Succeeded, bool ExistingLogin, IReadOnlyList<string> Errors)
{
    public static ClaimInviteResultDto Ok() => new(true, false, Array.Empty<string>());
    public static ClaimInviteResultDto AlreadyHasLogin() => new(true, true, Array.Empty<string>());
    public static ClaimInviteResultDto Failure(params string[] errors) => new(false, false, errors);
}
