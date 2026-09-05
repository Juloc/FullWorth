using FullWorth.Web.Security.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;

namespace FullWorth.Web.Modules.Auth;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/auth");

        group.MapPost("/login", LoginAsync).AllowAnonymous().RequireRateLimiting(RateLimitPolicies.Login);
        group.MapPost("/register", RegisterAsync).AllowAnonymous().RequireRateLimiting(RateLimitPolicies.Login);
        group.MapPost("/logout", LogoutAsync).RequireAuthorization();
        group.MapPost("/change-password", ChangePasswordAsync).RequireAuthorization();
        group.MapPost("/password-reset/request", RequestPasswordResetAsync).AllowAnonymous().RequireRateLimiting(RateLimitPolicies.PasswordReset);
        group.MapPost("/password-reset/complete", ResetPasswordAsync).AllowAnonymous().RequireRateLimiting(RateLimitPolicies.PasswordReset);
        group.MapPost("/claim", ClaimInviteAsync).AllowAnonymous().RequireRateLimiting(RateLimitPolicies.PasswordReset);

        return endpoints;
    }

    private static async Task<IResult> LoginAsync(
        HttpContext context,
        LoginRequest request,
        AuthSessionCoordinator sessions,
        CancellationToken ct)
    {
        var result = await sessions.LoginAsync(request, context, ct);
        if (!result.Succeeded)
            return Results.Json(result, statusCode: StatusCodes.Status401Unauthorized);

        var returnUrl = GetSafeReturnUrl(context);
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
        RegistrationService registration,
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

    private static string GetSafeReturnUrl(HttpContext context)
    {
        var candidate = context.Request.Query["returnUrl"].ToString();
        if (string.IsNullOrWhiteSpace(candidate))
            return "/";

        var actionContext = new ActionContext(context, context.GetRouteData(), new ActionDescriptor());
        var url = new UrlHelper(actionContext);
        return url.IsLocalUrl(candidate) ? candidate : "/";
    }
}
