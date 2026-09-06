using System.Security.Claims;
using Microsoft.AspNetCore.Identity;

namespace FullWorth.Web.Modules.Auth;

public sealed class PendingDeletionAccessMiddleware(RequestDelegate next)
{
    private static readonly PathString[] AllowedPrefixes =
    [
        new("/account/deletion"),
        new("/account-deletion"),
        new("/auth/account-deletion"),
        new("/auth/logout"),
        new("/health"),
        new("/favicon.ico")
    ];

    public async Task InvokeAsync(HttpContext context, UserManager<AuthUser> users)
    {
        if (context.User.Identity?.IsAuthenticated != true ||
            !Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var authUserId))
        {
            await next(context);
            return;
        }

        var user = await users.FindByIdAsync(authUserId.ToString());
        if (user?.DeletionRequestedAt is null)
        {
            await next(context);
            return;
        }

        if (AllowedPrefixes.Any(prefix => context.Request.Path.StartsWithSegments(prefix)))
        {
            await next(context);
            return;
        }

        if (context.Request.Path.StartsWithSegments("/bff") ||
            context.Request.Path.StartsWithSegments("/api"))
        {
            context.Response.StatusCode = StatusCodes.Status423Locked;
            await context.Response.WriteAsJsonAsync(new
            {
                error = "account_pending_deletion",
                deletionScheduledFor = user.DeletionScheduledFor
            });
            return;
        }

        context.Response.Redirect("/account/deletion");
    }
}
