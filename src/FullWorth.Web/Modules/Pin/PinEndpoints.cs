using System.Security.Claims;
using FullWorth.Web.Security.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;

namespace FullWorth.Web.Modules.Pin;

public sealed record PinRequest(string? Pin);

public static class PinEndpoints
{
    public static IEndpointRouteBuilder MapPinEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/auth/pin").RequireAuthorization();

        group.MapGet("", StatusAsync);
        group.MapPut("", SetAsync);
        group.MapDelete("", RemoveAsync);
        group.MapPost("/verify", VerifyAsync).RequireRateLimiting(RateLimitPolicies.Login);

        return endpoints;
    }

    private static async Task<IResult> StatusAsync(ClaimsPrincipal principal, PinService pins)
    {
        if (!TryGetAuthUserId(principal, out var authUserId))
            return Results.Unauthorized();
        return Results.Ok(new { isSet = await pins.HasPinAsync(authUserId) });
    }

    private static async Task<IResult> SetAsync(ClaimsPrincipal principal, PinRequest request, PinService pins)
    {
        if (!TryGetAuthUserId(principal, out var authUserId))
            return Results.Unauthorized();
        return await pins.SetPinAsync(authUserId, request.Pin)
            ? Results.NoContent()
            : Results.BadRequest(new { error = "invalid_pin" });
    }

    private static async Task<IResult> RemoveAsync(ClaimsPrincipal principal, PinService pins)
    {
        if (!TryGetAuthUserId(principal, out var authUserId))
            return Results.Unauthorized();
        await pins.RemovePinAsync(authUserId);
        return Results.NoContent();
    }

    private static async Task<IResult> VerifyAsync(ClaimsPrincipal principal, PinRequest request, PinService pins) =>
        !TryGetAuthUserId(principal, out var authUserId)
            ? Results.Unauthorized()
            : await pins.VerifyPinAsync(authUserId, request.Pin) switch
            {
                PinVerifyStatus.Success => Results.Ok(new { unlocked = true }),
                PinVerifyStatus.Locked => Results.StatusCode(StatusCodes.Status423Locked),
                PinVerifyStatus.NotSet => Results.BadRequest(new { error = "pin_not_set" }),
                _ => Results.Unauthorized()
            };

    private static bool TryGetAuthUserId(ClaimsPrincipal principal, out Guid authUserId) =>
        Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out authUserId);
}
