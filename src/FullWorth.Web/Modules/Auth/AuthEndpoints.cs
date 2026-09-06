using System.Security.Claims;
using FullWorth.Web.Security.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace FullWorth.Web.Modules.Auth;

public static class AuthEndpoints
{
    private const string ExternalModeItem = "fullworth:external-mode";
    private const string ExternalReturnUrlItem = "fullworth:external-return-url";

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/auth");

        group.MapGet("/providers", ProvidersAsync).AllowAnonymous();
        group.MapGet("/external/{provider}", StartExternalAsync).AllowAnonymous().RequireRateLimiting(RateLimitPolicies.Login);
        group.MapGet("/external/callback", ExternalCallbackAsync).AllowAnonymous().RequireRateLimiting(RateLimitPolicies.Login);
        group.MapPost("/login", LoginAsync).AllowAnonymous().RequireRateLimiting(RateLimitPolicies.Login);
        group.MapPost("/register", RegisterAsync).AllowAnonymous().RequireRateLimiting(RateLimitPolicies.Login);
        group.MapPost("/logout", LogoutAsync).RequireAuthorization();
        group.MapPost("/change-password", ChangePasswordAsync).RequireAuthorization();
        group.MapGet("/account-deletion/status", AccountDeletionStatusAsync).RequireAuthorization();
        group.MapPost("/account-deletion/request", RequestAccountDeletionAsync).RequireAuthorization().RequireRateLimiting(RateLimitPolicies.BrowserApi);
        group.MapPost("/account-deletion/cancel", CancelAccountDeletionAsync).RequireAuthorization().RequireRateLimiting(RateLimitPolicies.BrowserApi);
        group.MapPost("/password-reset/request", RequestPasswordResetAsync).AllowAnonymous().RequireRateLimiting(RateLimitPolicies.PasswordReset);
        group.MapPost("/password-reset/complete", ResetPasswordAsync).AllowAnonymous().RequireRateLimiting(RateLimitPolicies.PasswordReset);
        group.MapPost("/claim", ClaimInviteAsync).AllowAnonymous().RequireRateLimiting(RateLimitPolicies.PasswordReset);

        return endpoints;
    }

    private static async Task<IResult> ProvidersAsync(
        IAuthenticationSchemeProvider schemes,
        IOptions<RegistrationOptions> registration)
    {
        return Results.Ok(new
        {
            registrationEnabled = registration.Value.Enabled,
            google = await schemes.GetSchemeAsync("Google") is not null,
            apple = await schemes.GetSchemeAsync("Apple") is not null
        });
    }

    private static async Task<IResult> StartExternalAsync(
        HttpContext context,
        string provider,
        IAuthenticationSchemeProvider schemes,
        SignInManager<AuthUser> signInManager,
        IOptions<RegistrationOptions> registration)
    {
        if (context.User.Identity?.IsAuthenticated == true)
            return Results.Redirect("/");

        var scheme = NormalizeProvider(provider);
        if (scheme is null || await schemes.GetSchemeAsync(scheme) is null)
            return Results.NotFound();

        var mode = string.Equals(context.Request.Query["mode"], "register", StringComparison.OrdinalIgnoreCase)
            ? "register"
            : "login";
        if (mode == "register" && !registration.Value.Enabled)
            return Results.Redirect("/auth/register?status=registration-disabled");

        var properties = signInManager.ConfigureExternalAuthenticationProperties(
            scheme,
            "/auth/external/callback");
        properties.Items[ExternalModeItem] = mode;
        properties.Items[ExternalReturnUrlItem] = GetSafeReturnUrl(context);

        return Results.Challenge(properties, [scheme]);
    }

    private static async Task<IResult> ExternalCallbackAsync(
        HttpContext context,
        SignInManager<AuthUser> signInManager,
        UserManager<AuthUser> userManager,
        AuthSessionCoordinator sessions,
        RegistrationService registration,
        AccountDeletionService deletion,
        CancellationToken ct)
    {
        var external = await context.AuthenticateAsync(IdentityConstants.ExternalScheme);
        var info = await signInManager.GetExternalLoginInfoAsync();
        if (!external.Succeeded || external.Properties is null || info is null)
            return Results.Redirect("/auth/login?status=external-failed");

        var mode = external.Properties.Items.TryGetValue(ExternalModeItem, out var storedMode)
            ? storedMode
            : "login";
        var returnUrl = external.Properties.Items.TryGetValue(ExternalReturnUrlItem, out var storedReturnUrl)
            ? GetSafeReturnUrl(context, storedReturnUrl)
            : "/";

        var user = await userManager.FindByLoginAsync(info.LoginProvider, info.ProviderKey);
        if (user is null)
        {
            var email = GetExternalEmail(info.Principal);
            if (email.Length > 0)
            {
                user = await userManager.FindByEmailAsync(email);
                if (user is not null)
                {
                    var linked = await userManager.AddLoginAsync(user, info);
                    if (!linked.Succeeded)
                    {
                        var linkedUser = await userManager.FindByLoginAsync(info.LoginProvider, info.ProviderKey);
                        if (linkedUser?.Id != user.Id)
                        {
                            await context.SignOutAsync(IdentityConstants.ExternalScheme);
                            return Results.Redirect("/auth/login?status=external-failed");
                        }
                    }
                }
            }
        }

        if (user is null)
        {
            if (!string.Equals(mode, "register", StringComparison.Ordinal))
            {
                await context.SignOutAsync(IdentityConstants.ExternalScheme);
                return Results.Redirect("/auth/register?status=external-account-not-found");
            }

            var registered = await registration.RegisterExternalAsync(info, context, ct);
            await context.SignOutAsync(IdentityConstants.ExternalScheme);
            return registered.Succeeded
                ? Results.Redirect(returnUrl)
                : Results.Redirect(registered.Error == "registration_disabled"
                    ? "/auth/register?status=registration-disabled"
                    : "/auth/register?status=external-registration-failed");
        }

        if (!await sessions.SignInUserAsync(user, context, ct))
        {
            await context.SignOutAsync(IdentityConstants.ExternalScheme);
            return Results.Redirect("/auth/login?status=external-failed");
        }

        await context.SignOutAsync(IdentityConstants.ExternalScheme);
        if (await deletion.IsPendingAsync(user.Id, ct))
            return Results.Redirect("/account/deletion");

        return Results.Redirect(returnUrl);
    }

    private static async Task<IResult> LoginAsync(
        HttpContext context,
        LoginRequest request,
        AuthSessionCoordinator sessions,
        [FromServices] AccountDeletionService deletion,
        CancellationToken ct)
    {
        var result = await sessions.LoginAsync(request, context, ct);
        if (!result.Succeeded)
            return Results.Json(result, statusCode: StatusCodes.Status401Unauthorized);

        var returnUrl = result.User is not null && await deletion.IsPendingAsync(result.User.Id, ct)
            ? "/account/deletion"
            : GetSafeReturnUrl(context);
        return Results.Ok(new
        {
            result.Succeeded,
            result.Error,
            result.User,
            ReturnUrl = returnUrl
        });
    }

    private static async Task<IResult> RegisterAsync(
        HttpContext context,
        RegisterRequest request,
        [FromServices] RegistrationService registration,
        CancellationToken ct)
    {
        var result = await registration.RegisterAsync(request, context, ct);
        if (result.Succeeded)
            return Results.Ok(new { result.Succeeded, result.User, ReturnUrl = "/" });

        var status = result.Error switch
        {
            "registration_disabled" => StatusCodes.Status403Forbidden,
            "registration_unavailable" => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status400BadRequest
        };
        return Results.Json(result, statusCode: status);
    }

    private static async Task<IResult> LogoutAsync(
        HttpContext context,
        AuthSessionCoordinator sessions,
        CancellationToken ct)
    {
        await sessions.LogoutAsync(context.User, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> ChangePasswordAsync(
        HttpContext context,
        ChangePasswordRequest request,
        AuthSessionCoordinator sessions,
        CancellationToken ct)
    {
        var result = await sessions.ChangePasswordAsync(
            context.User,
            request.CurrentPassword,
            request.NewPassword,
            ct);
        return result.Succeeded ? Results.NoContent() : Results.BadRequest(result);
    }

    private static async Task<IResult> AccountDeletionStatusAsync(
        HttpContext context,
        [FromServices] AccountDeletionService deletion,
        CancellationToken ct)
    {
        var status = await deletion.GetStatusAsync(context.User, ct);
        return status is null ? Results.Unauthorized() : Results.Ok(status);
    }

    private static async Task<IResult> RequestAccountDeletionAsync(
        HttpContext context,
        AccountDeletionRequest request,
        [FromServices] AccountDeletionService deletion,
        CancellationToken ct)
    {
        var (status, error) = await deletion.RequestAsync(context.User, request.CurrentPassword, ct);
        if (status is not null) return Results.Ok(status);
        return error switch
        {
            "invalid_password" => Results.BadRequest(new { error = "invalid_password" }),
            "last_admin" => Results.Conflict(new { error = "last_admin" }),
            _ => Results.StatusCode(StatusCodes.Status503ServiceUnavailable)
        };
    }

    private static async Task<IResult> CancelAccountDeletionAsync(
        HttpContext context,
        [FromServices] AccountDeletionService deletion,
        CancellationToken ct)
    {
        var (status, error) = await deletion.CancelAsync(context.User, ct);
        if (status is not null) return Results.Ok(status);
        return error is "deletion_deadline_passed" or "purge_in_progress"
            ? Results.Conflict(new { error })
            : Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    private static async Task<IResult> RequestPasswordResetAsync(
        RequestPasswordResetRequest request,
        AuthService auth)
    {
        _ = await auth.GeneratePasswordResetTokenAsync(request.Email);
        return Results.Accepted(value: new PasswordResetRequestResultDto(
            "If the account exists, the password-reset request was accepted."));
    }

    private static async Task<IResult> ResetPasswordAsync(
        ResetPasswordRequest request,
        AuthSessionCoordinator sessions,
        CancellationToken ct)
    {
        var result = await sessions.ResetPasswordAsync(request.Email, request.Token, request.NewPassword, ct);
        return result.Succeeded
            ? Results.NoContent()
            : Results.BadRequest(AuthActionResultDto.Failure("Invalid reset token or request."));
    }

    private static async Task<IResult> ClaimInviteAsync(
        HttpContext context,
        ClaimInviteRequest request,
        [FromServices] InviteClaimService claims,
        CancellationToken ct)
    {
        var result = await claims.ClaimAsync(request, context, ct);
        return result.Succeeded ? Results.Ok(result) : Results.BadRequest(result);
    }

    private static string GetExternalEmail(ClaimsPrincipal principal) =>
        (principal.FindFirstValue(ClaimTypes.Email)
            ?? principal.FindFirstValue("email")
            ?? string.Empty).Trim();

    private static string? NormalizeProvider(string? provider) =>
        provider?.Trim().ToLowerInvariant() switch
        {
            "google" => "Google",
            "apple" => "Apple",
            _ => null
        };

    private static string GetSafeReturnUrl(HttpContext context) =>
        GetSafeReturnUrl(context, context.Request.Query["returnUrl"].ToString());

    private static string GetSafeReturnUrl(HttpContext context, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return "/";

        var actionContext = new ActionContext(context, context.GetRouteData(), new ActionDescriptor());
        var url = new UrlHelper(actionContext);
        return url.IsLocalUrl(candidate) ? candidate : "/";
    }
}
