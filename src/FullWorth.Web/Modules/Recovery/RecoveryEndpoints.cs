using System.Security.Claims;
using FullWorth.Web.Modules.Auth;
using FullWorth.Web.Security.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace FullWorth.Web.Modules.Recovery;

public sealed record RecoveryRedeemRequest(string? Email, string? RecoveryCode);

public static class RecoveryEndpoints
{
    public static RouteGroupBuilder MapRecoveryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Anonymous, rate-limited, single-use recovery-code sign-in for users who lost their
        // password/passkey. Enumeration-safe uniform failure; issues a session on success.
        endpoints.MapPost("/auth/recovery-code/redeem", RedeemAsync)
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicies.PasswordReset);

        var group = endpoints.MapGroup("/auth/recovery-codes").RequireAuthorization();
        group.MapGet("/status", StatusAsync);
        group.MapPost("/generate", GenerateAsync);
        group.MapPost("/regenerate", RegenerateAsync);
        return group;
    }

    private static async Task<IResult> RedeemAsync(
        HttpContext context,
        RecoveryRedeemRequest request,
        AuthSessionCoordinator sessions,
        CancellationToken ct)
    {
        context.Response.Headers.CacheControl = "no-store";
        var redeemed = await sessions.RedeemRecoveryCodeAsync(request.Email, request.RecoveryCode, context, ct);
        return redeemed
            ? Results.Ok(new { returnUrl = "/" })
            : Results.Json(new { error = "Invalid email or recovery code." }, statusCode: StatusCodes.Status401Unauthorized);
    }

    private static async Task<IResult> StatusAsync(
        HttpContext context,
        RecoveryService recovery,
        CancellationToken ct)
    {
        if (!TryGetAuthUserId(context.User, out var authUserId))
            return Results.Unauthorized();

        return Results.Ok(await recovery.GetStatusAsync(authUserId, ct));
    }

    private static async Task<IResult> GenerateAsync(
        HttpContext context,
        RecoveryService recovery,
        CancellationToken ct)
    {
        if (!TryGetAuthUserId(context.User, out var authUserId))
            return Results.Unauthorized();

        context.Response.Headers.CacheControl = "no-store";
        return Results.Ok(await recovery.GenerateAsync(authUserId, ct));
    }

    private static async Task<IResult> RegenerateAsync(
        HttpContext context,
        RecoveryService recovery,
        CancellationToken ct)
    {
        if (!TryGetAuthUserId(context.User, out var authUserId))
            return Results.Unauthorized();

        context.Response.Headers.CacheControl = "no-store";
        return Results.Ok(await recovery.RegenerateAsync(authUserId, ct));
    }

    private static bool TryGetAuthUserId(ClaimsPrincipal principal, out Guid authUserId) =>
        Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out authUserId);
}
