using System.Net.Http.Json;
using FullWorth.Web.Modules.Bootstrap;
using FullWorth.Web.Security.BackendContext;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;

namespace FullWorth.Web.Modules.Auth;

/// <summary>
/// Orchestrates claiming an owner-issued FullWorth Space invite (multi-user sharing). The invitee supplies a
/// one-time token and their OWN password. The backend (internal-key seam) resolves the token to a
/// FullWorthUser and applies the membership + account grants; the Web tier then creates the matching login
/// and signs the invitee in. The password is validated locally BEFORE the backend consumes the invite, so
/// a weak password can never burn a one-time token. The backend never sees the password.
/// </summary>
public sealed class InviteClaimService(
    IHttpClientFactory httpClientFactory,
    BackendContextOptions backendOptions,
    UserManager<AuthUser> userManager,
    AuthService auth,
    AuthSessionCoordinator sessions)
{
    public async Task<ClaimInviteResultDto> ClaimAsync(ClaimInviteRequest request, HttpContext context, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Token))
            return ClaimInviteResultDto.Failure("The invitation link is invalid.");

        // Validate the password up front so an invalid one does NOT consume the single-use invite.
        var passwordErrors = await ValidatePasswordAsync(request.NewPassword);
        if (passwordErrors.Count > 0)
            return ClaimInviteResultDto.Failure(passwordErrors.ToArray());

        // Exchange the token with the backend: this creates/reuses the FullWorthUser + membership + grants.
        var accepted = await AcceptOnBackendAsync(request.Token, ct);
        if (accepted is null || accepted.FinanceUserId == Guid.Empty)
            return ClaimInviteResultDto.Failure("The invitation is invalid, expired, or already used.");

        // If a login already exists for this identity (shared into a further space), access is already
        // granted server-side — we can't sign them in without their existing password.
        var existing = await userManager.FindByEmailAsync(accepted.Email);
        if (existing is not null)
            return ClaimInviteResultDto.AlreadyHasLogin();

        var created = await auth.CreateUserAsync(new CreateAuthUserRequest(accepted.FinanceUserId, accepted.Email, request.NewPassword));
        if (!created.Succeeded)
            return ClaimInviteResultDto.Failure(created.Errors.ToArray());

        var login = await sessions.LoginAsync(new LoginRequest(accepted.Email, request.NewPassword), context, ct);
        return login.Succeeded ? ClaimInviteResultDto.Ok() : ClaimInviteResultDto.AlreadyHasLogin();
    }

    private async Task<IReadOnlyList<string>> ValidatePasswordAsync(string? password)
    {
        if (string.IsNullOrEmpty(password))
            return ["A password is required."];
        var probe = new AuthUser { Email = "invitee@local", UserName = "invitee@local" };
        var errors = new List<string>();
        foreach (var validator in userManager.PasswordValidators)
        {
            var result = await validator.ValidateAsync(userManager, probe, password);
            if (!result.Succeeded)
                errors.AddRange(result.Errors.Select(e => e.Description));
        }
        return errors;
    }

    private async Task<AcceptInviteResponse?> AcceptOnBackendAsync(string token, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient(FirstRunBootstrapper.BackendClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/bootstrap/accept-invite")
        {
            Content = JsonContent.Create(new { token })
        };
        request.Headers.TryAddWithoutValidation(BackendContextHeaders.InternalKey, backendOptions.InternalKey);

        using var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadFromJsonAsync<AcceptInviteResponse>(ct);
    }

    private sealed record AcceptInviteResponse(Guid FinanceUserId, string Email);
}
