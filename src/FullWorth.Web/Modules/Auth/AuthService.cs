using Microsoft.AspNetCore.Identity;

namespace FullWorth.Web.Modules.Auth;

public sealed class AuthService(
    UserManager<AuthUser> userManager,
    SignInManager<AuthUser> signInManager)
{
    public async Task<CreateAuthUserResultDto> CreateUserAsync(CreateAuthUserRequest request)
    {
        if (request.FinanceUserId == Guid.Empty)
            return new CreateAuthUserResultDto(false, null, ["FinanceUserId is required."]);

        var email = request.Email.Trim();
        if (email.Length == 0)
            return new CreateAuthUserResultDto(false, null, ["Email is required."]);

        var user = new AuthUser
        {
            Id = Guid.NewGuid(),
            FinanceUserId = request.FinanceUserId,
            Email = email,
            UserName = email
        };

        var result = await userManager.CreateAsync(user, request.Password);
        if (!result.Succeeded)
            return new CreateAuthUserResultDto(false, null, result.Errors.Select(x => x.Description).ToArray());

        return new CreateAuthUserResultDto(true, ToDto(user), Array.Empty<string>());
    }

    public async Task<LoginResultDto> ValidatePasswordAsync(string email, string password)
    {
        var (user, result) = await CheckPasswordAsync(email, password);
        return user is not null && result.Succeeded
            ? LoginResultDto.Success(ToDto(user))
            : LoginResultDto.InvalidCredentials();
    }

    public async Task<AuthActionResultDto> ChangePasswordAsync(Guid authUserId, string currentPassword, string newPassword)
    {
        var user = await userManager.FindByIdAsync(authUserId.ToString());
        if (user is null)
            return AuthActionResultDto.Failure("Unable to change password.");

        var result = await userManager.ChangePasswordAsync(user, currentPassword, newPassword);
        return ToActionResult(result);
    }

    public async Task<PasswordResetTokenResult?> GeneratePasswordResetTokenAsync(string email)
    {
        var user = await FindByEmailAsync(email);
        if (user is null)
            return null;

        var token = await userManager.GeneratePasswordResetTokenAsync(user);
        return new PasswordResetTokenResult(user.Id, token);
    }

    public async Task<AuthActionResultDto> ResetPasswordAsync(string email, string token, string newPassword)
    {
        var user = await FindByEmailAsync(email);
        if (user is null)
            return AuthActionResultDto.Failure("Invalid reset request.");

        var result = await userManager.ResetPasswordAsync(user, token, newPassword);
        return ToActionResult(result);
    }

    private async Task<(AuthUser? User, SignInResult Result)> CheckPasswordAsync(string email, string password)
    {
        var user = await FindByEmailAsync(email);
        if (user is null)
            return (null, SignInResult.Failed);

        var result = await signInManager.CheckPasswordSignInAsync(user, password, lockoutOnFailure: true);
        return (user, result);
    }

    private Task<AuthUser?> FindByEmailAsync(string email)
    {
        var normalizedInput = email.Trim();
        return normalizedInput.Length == 0
            ? Task.FromResult<AuthUser?>(null)
            : userManager.FindByEmailAsync(normalizedInput);
    }

    private static AuthActionResultDto ToActionResult(IdentityResult result) => result.Succeeded
        ? AuthActionResultDto.Success()
        : new AuthActionResultDto(false, result.Errors.Select(x => x.Description).ToArray());

    private static AuthUserDto ToDto(AuthUser user) => new(
        user.Id,
        user.FinanceUserId,
        user.Email!,
        user.CreatedAt,
        user.UpdatedAt);
}
