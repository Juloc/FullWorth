using System.Security.Claims;
using FullWorth.Web.Security.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;

namespace FullWorth.Web.Modules.Passkeys;

public static class PasskeyEndpoints
{
    public static IEndpointRouteBuilder MapPasskeyEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/auth/passkeys");

        group.MapPost("/register/begin", BeginRegistrationAsync)
            .RequireAuthorization()
            .RequireRateLimiting(RateLimitPolicies.Passkey);
        group.MapPost("/register/complete", CompleteRegistrationAsync)
            .RequireAuthorization()
            .RequireRateLimiting(RateLimitPolicies.Passkey);
        group.MapGet("", ListAsync)
            .RequireAuthorization();
        group.MapDelete("/{credentialId:guid}", DeleteAsync)
            .RequireAuthorization();

        group.MapPost("/login/begin", BeginLoginAsync)
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicies.Passkey);
        group.MapPost("/login/complete", CompleteLoginAsync)
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicies.Passkey);

        // Unlock the inactivity lock: same WebAuthn assertion as login, but the session already
        // exists — we only verify the passkey belongs to the current user, never minting a session.
        group.MapPost("/unlock/begin", BeginLoginAsync)
            .RequireAuthorization()
            .RequireRateLimiting(RateLimitPolicies.Passkey);
        group.MapPost("/unlock/complete", CompleteUnlockAsync)
            .RequireAuthorization()
            .RequireRateLimiting(RateLimitPolicies.Passkey);

        return endpoints;
    }

    private static async Task<IResult> BeginRegistrationAsync(
        ClaimsPrincipal principal,
        PasskeyService service,
        CancellationToken cancellationToken)
    {
        if (!TryGetAuthUserId(principal, out var authUserId))
            return Results.Unauthorized();

        try
        {
            return Results.Ok(await service.BeginRegistrationAsync(authUserId, cancellationToken));
        }
        catch (PasskeyRegistrationException)
        {
            return Results.BadRequest(new { error = "Passkey registration failed." });
        }
    }

    private static async Task<IResult> CompleteRegistrationAsync(
        ClaimsPrincipal principal,
        PasskeyCompleteRegistrationRequest request,
        PasskeyService service,
        CancellationToken cancellationToken)
    {
        if (!TryGetAuthUserId(principal, out var authUserId))
            return Results.Unauthorized();
        if (request.Credential is null)
            return Results.BadRequest(new { error = "Passkey registration failed." });

        try
        {
            return Results.Ok(await service.CompleteRegistrationAsync(authUserId, request, cancellationToken));
        }
        catch (PasskeyChallengeException)
        {
            return Results.BadRequest(new { error = "Passkey registration failed." });
        }
        catch (PasskeyRegistrationException)
        {
            return Results.BadRequest(new { error = "Passkey registration failed." });
        }
    }

    private static Task<PasskeyBeginLoginResponse> BeginLoginAsync(
        PasskeyService service,
        CancellationToken cancellationToken) =>
        service.BeginLoginAsync(null, cancellationToken);

    private static async Task<IResult> CompleteLoginAsync(
        PasskeyCompleteLoginRequest request,
        PasskeyService service,
        PasskeySessionSignInService sessionSignIn,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await service.CompleteLoginAsync(request, cancellationToken);
            if (!await sessionSignIn.SignInAsync(result.AuthUserId, context, cancellationToken))
                return Results.Unauthorized();
            return Results.Ok(new PasskeyLoginResponse(true));
        }
        catch (PasskeyAuthenticationException)
        {
            return Results.Unauthorized();
        }
    }

    private static async Task<IResult> CompleteUnlockAsync(
        ClaimsPrincipal principal,
        PasskeyCompleteLoginRequest request,
        PasskeyService service,
        CancellationToken cancellationToken)
    {
        if (!TryGetAuthUserId(principal, out var authUserId))
            return Results.Unauthorized();
        try
        {
            var result = await service.CompleteLoginAsync(request, cancellationToken);
            return result.AuthUserId == authUserId
                ? Results.Ok(new PasskeyLoginResponse(true))
                : Results.Unauthorized();
        }
        catch (PasskeyAuthenticationException)
        {
            return Results.Unauthorized();
        }
    }

    private static async Task<IResult> ListAsync(
        ClaimsPrincipal principal,
        PasskeyService service,
        CancellationToken cancellationToken)
    {
        if (!TryGetAuthUserId(principal, out var authUserId))
            return Results.Unauthorized();
        return Results.Ok(await service.ListAsync(authUserId, cancellationToken));
    }

    private static async Task<IResult> DeleteAsync(
        Guid credentialId,
        ClaimsPrincipal principal,
        PasskeyService service,
        CancellationToken cancellationToken)
    {
        if (!TryGetAuthUserId(principal, out var authUserId))
            return Results.Unauthorized();
        return await service.DeleteAsync(authUserId, credentialId, cancellationToken)
            ? Results.NoContent()
            : Results.NotFound();
    }

    private static bool TryGetAuthUserId(ClaimsPrincipal principal, out Guid authUserId) =>
        Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out authUserId);
}
