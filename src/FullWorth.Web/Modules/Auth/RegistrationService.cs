using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
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

        var (created, backendError) = await CreateFinanceUserAsync(email, displayName, registration, ct);
        if (backendError is not null)
            return backendError == "unavailable" ? RegisterResultDto.Unavailable() : RegisterResultDto.Failed();

        var authResult = await auth.CreateUserAsync(
            new CreateAuthUserRequest(created!.FinanceUserId, email, request.Password));
        if (!authResult.Succeeded)
            return new RegisterResultDto(false, "registration_failed", null, authResult.Errors);

        var authUser = await userManager.FindByEmailAsync(email);
        if (authUser is null)
            return RegisterResultDto.Failed();

        var agreement = await AddAgreementClaimsAsync(authUser);
        if (!agreement.Succeeded)
            return new RegisterResultDto(false, "registration_failed", null, agreement.Errors.Select(error => error.Description).ToArray());

        var login = await sessions.LoginAsync(new LoginRequest(email, request.Password), context, ct);
        return login.Succeeded && login.User is not null
            ? RegisterResultDto.Success(login.User)
            : RegisterResultDto.Failed();
    }

    public async Task<RegisterResultDto> RegisterExternalAsync(
        ExternalLoginInfo login,
        HttpContext context,
        CancellationToken ct)
    {
        var registration = options.Value;
        if (!registration.Enabled)
            return RegisterResultDto.Disabled();

        var email = (login.Principal.FindFirstValue(ClaimTypes.Email)
            ?? login.Principal.FindFirstValue("email")
            ?? string.Empty).Trim();
        if (email.Length == 0)
            return RegisterResultDto.Invalid();

        if (await userManager.FindByEmailAsync(email) is not null)
            return RegisterResultDto.Unavailable();

        var userErrors = await ValidateUserAsync(email);
        if (userErrors.Count > 0)
            return new RegisterResultDto(false, "invalid_registration", null, userErrors);

        var displayName = ResolveDisplayName(login.Principal, email);
        var (created, backendError) = await CreateFinanceUserAsync(email, displayName, registration, ct);
        if (backendError is not null)
            return backendError == "unavailable" ? RegisterResultDto.Unavailable() : RegisterResultDto.Failed();

        var authResult = await auth.CreateExternalUserAsync(created!.FinanceUserId, email, login);
        if (!authResult.Succeeded || authResult.User is null)
            return new RegisterResultDto(false, "registration_failed", null, authResult.Errors);

        var authUser = await userManager.FindByEmailAsync(email);
        if (authUser is null)
            return RegisterResultDto.Failed();

        var agreement = await AddAgreementClaimsAsync(authUser);
        if (!agreement.Succeeded)
            return new RegisterResultDto(false, "registration_failed", null, agreement.Errors.Select(error => error.Description).ToArray());

        return await sessions.SignInUserAsync(authUser, context, ct)
            ? RegisterResultDto.Success(authResult.User)
            : RegisterResultDto.Failed();
    }

    private async Task<(RegistrationBackendResponse? Created, string? Error)> CreateFinanceUserAsync(
        string email,
        string displayName,
        RegistrationOptions registration,
        CancellationToken ct)
    {
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
            return (null, "unavailable");
        if (!response.IsSuccessStatusCode)
            return (null, "failed");

        var created = await response.Content.ReadFromJsonAsync<RegistrationBackendResponse>(ct);
        return created is null || created.FinanceUserId == Guid.Empty
            ? (null, "failed")
            : (created, null);
    }

    private async Task<IdentityResult> AddAgreementClaimsAsync(AuthUser authUser)
    {
        var acceptedAt = DateTimeOffset.UtcNow.ToString("O");
        return await userManager.AddClaimsAsync(authUser,
        [
            new Claim(LegalDocumentVersions.TermsVersionClaim, LegalDocumentVersions.Terms),
            new Claim(LegalDocumentVersions.TermsAcceptedAtClaim, acceptedAt),
            new Claim(LegalDocumentVersions.PrivacyVersionClaim, LegalDocumentVersions.Privacy),
            new Claim(LegalDocumentVersions.PrivacyAcknowledgedAtClaim, acceptedAt),
            new Claim(LegalDocumentVersions.AdultConfirmedAtClaim, acceptedAt)
        ]);
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

    private static string ResolveDisplayName(ClaimsPrincipal principal, string email)
    {
        var displayName = (principal.FindFirstValue(ClaimTypes.Name)
            ?? principal.FindFirstValue("name")
            ?? string.Empty).Trim();

        if (displayName.Length == 0)
        {
            var givenName = (principal.FindFirstValue(ClaimTypes.GivenName) ?? principal.FindFirstValue("given_name") ?? string.Empty).Trim();
            var familyName = (principal.FindFirstValue(ClaimTypes.Surname) ?? principal.FindFirstValue("family_name") ?? string.Empty).Trim();
            displayName = string.Join(" ", new[] { givenName, familyName }.Where(value => value.Length > 0));
        }

        if (displayName.Length == 0)
        {
            var at = email.IndexOf('@');
            displayName = at > 0 ? email[..at] : email;
        }

        return displayName[..Math.Min(displayName.Length, 200)];
    }

    private sealed record RegistrationBackendResponse(Guid FinanceUserId, Guid FullWorthSpaceId);
}
