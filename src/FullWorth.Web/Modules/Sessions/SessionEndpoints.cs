using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;

namespace FullWorth.Web.Modules.Sessions;

public static class SessionEndpoints
{
    public static RouteGroupBuilder MapSessionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/auth/sessions").RequireAuthorization();
        group.MapGet("/", ListAsync);
        group.MapDelete("/{sessionId:guid}", RevokeAsync);
        group.MapPost("/revoke-others", RevokeOthersAsync);
        return group;
    }

    private static async Task<IResult> ListAsync(HttpContext context, SessionService sessions, CancellationToken ct)
    {
        if (!TryGetCurrent(context.User, out var authUserId, out var sessionId))
            return Results.Unauthorized();

        return Results.Ok(await sessions.ListSessionsAsync(authUserId, sessionId, ct));
    }

    private static async Task<IResult> RevokeAsync(Guid sessionId, HttpContext context, SessionService sessions, CancellationToken ct)
    {
        if (!TryGetCurrent(context.User, out var authUserId, out var currentSessionId))
            return Results.Unauthorized();

        if (!await sessions.RevokeSessionAsync(authUserId, sessionId, ct))
            return Results.NotFound();

        if (sessionId == currentSessionId)
            await context.SignOutAsync(IdentityConstants.ApplicationScheme);

        return Results.NoContent();
    }

    private static async Task<IResult> RevokeOthersAsync(HttpContext context, SessionService sessions, CancellationToken ct)
    {
        if (!TryGetCurrent(context.User, out var authUserId, out var currentSessionId))
            return Results.Unauthorized();

        await sessions.RevokeAllOtherSessionsAsync(authUserId, currentSessionId, ct);
        return Results.NoContent();
    }

    private static bool TryGetCurrent(ClaimsPrincipal principal, out Guid authUserId, out Guid sessionId)
    {
        authUserId = Guid.Empty;
        sessionId = Guid.Empty;

        var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(userId, out authUserId))
            return false;

        return SessionClaims.TryGetSessionId(principal, out sessionId);
    }
}
