using System.Security.Claims;
using System.Net;
using System.Net.Http.Json;
using FullWorth.Web.Modules.Bootstrap;
using FullWorth.Web.Security.BackendContext;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace FullWorth.Web.Modules.Auth;

public sealed class RegistrationService(
    IOptions<RegistrationOptions> options,
    IHttpClientFactory httpClientFactory,
    BackendContextOptions backendOptions,
    UserManager<AuthUser> userManager,
    AuthService auth,
    AuthSessionCoordinator sessions)
{
    public async Task<RegisterResultDto> RegisterAsync(
        RegisterRequest request,
        HttpContext context,
        CancellationToken ct)
    {
        var registration = options.Value;
        if (!registration.Enabled)
            return RegisterResultDto.Disabled();

        var email = (request.Email ?? string.Empty).Trim();
        var displayName = (request.DisplayName ?? string.Empty).Trim();
        if (email.Length == 0 || displayName.Length == 0 || displayName.Length > 200 || !request.AcceptTerms || !request.ConfirmAdult)
            return RegisterResultDto.Invalid();

        if (await userManager.FindByEmailAsync(email) is not null)
            return RegisterResultDto.Unavailable();

        var userErrors = await ValidateUserAsync(email);
        if (userErrors.Count > 0)
            return new RegisterResultDto(false, "invalid_registration", null, userErrors);

        var passwordErrors = await ValidatePasswordAsync(email, request.Password);
        if (passwordErrors.Count > 0)
            return new RegisterResultDto(false, "invalid_password", null, passwordErrors);

        var client = httpClientFactory.CreateClient(FirstRunBootstrapper.BackendClientName);
        using var backendRequest = new HttpRequestMessage(HttpMethod.Post, "api/bootstrap/register")
        {
            Content = JsonContent.Create(new
            {
                email,
                displayName,
                spaceName = registration.SpaceName,
                baseCurrency = registration.BaseCurrency
            })
        };
        backendRequest.Headers.TryAddWithoutValidation(BackendContextHeaders.InternalKey, backendOptions.InternalKey);

        using var response = await client.SendAsync(backendRequest, ct);
        if (response.StatusCode == HttpStatusCode.Conflict)
            return RegisterResultDto.Unavailable();
        if (!response.IsSuccessStatusCode)
            return RegisterResultDto.Failed();

        var created = await response.Content.ReadFromJsonAsync<RegistrationBackendResponse>(ct);
        if (created is null || created.FinanceUserId == Guid.Empty)
            return RegisterResultDto.Failed();

        var authResult = await auth.CreateUserAsync(
            new CreateAuthUserRequest(created.FinanceUserId, email, request.Password));
        if (!authResult.Succeeded)
            return new RegisterResultDto(false, "registration_failed", null, authResult.Errors);

        var authUser = await userManager.FindByEmailAsync(email);
        if (authUser is null)
            return RegisterResultDto.Failed();

        var acceptedAt = DateTimeOffset.UtcNow.ToString("O");
        var agreement = await userManager.AddClaimsAsync(authUser,
        [
            new Claim(LegalDocumentVersions.TermsVersionClaim, LegalDocumentVersions.Terms),
            new Claim(LegalDocumentVersions.TermsAcceptedAtClaim, acceptedAt),
            new Claim(LegalDocumentVersions.PrivacyVersionClaim, LegalDocumentVersions.Privacy),
            new Claim(LegalDocumentVersions.PrivacyAcknowledgedAtClaim, acceptedAt),
            new Claim(LegalDocumentVersions.AdultConfirmedAtClaim, acceptedAt)
        ]);
        if (!agreement.Succeeded)
            return new RegisterResultDto(false, "registration_failed", null, agreement.Errors.Select(error => error.Description).ToArray());

        var login = await sessions.LoginAsync(new LoginRequest(email, request.Password), context, ct);
        return login.Succeeded && login.User is not null
            ? RegisterResultDto.Success(login.User)
            : RegisterResultDto.Failed();
    }

    private async Task<IReadOnlyList<string>> ValidateUserAsync(string email)
    {
        var probe = new AuthUser { Id = Guid.NewGuid(), Email = email, UserName = email };
        var errors = new List<string>();
        foreach (var validator in userManager.UserValidators)
        {
            var result = await validator.ValidateAsync(userManager, probe);
            if (!result.Succeeded)
                errors.AddRange(result.Errors.Select(error => error.Description));
        }

        return errors;
    }

    private async Task<IReadOnlyList<string>> ValidatePasswordAsync(string email, string? password)
    {
        if (string.IsNullOrEmpty(password))
            return ["A password is required."];

        var probe = new AuthUser { Email = email, UserName = email };
        var errors = new List<string>();
        foreach (var validator in userManager.PasswordValidators)
        {
            var result = await validator.ValidateAsync(userManager, probe, password);
            if (!result.Succeeded)
                errors.AddRange(result.Errors.Select(error => error.Description));
        }

        return errors;
    }

    private sealed record RegistrationBackendResponse(Guid FinanceUserId, Guid FullWorthSpaceId);
}
