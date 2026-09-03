using System.Security.Claims;
using FullWorth.Web.Modules.Auth;
using Microsoft.AspNetCore.Identity;

namespace FullWorth.Web.Security.BackendContext;

/// <summary>
/// Attaches the trusted caller identity to FullWorth.Banking requests: the FinanceUserId is taken from
/// the authenticated session (never from the browser) and the requested fullWorthSpaceId is copied from
/// the inbound query. The Banking service forwards these to FullWorth.Backend, which performs the
/// authoritative owner/membership check — so a user cannot connect or sync in a space they don't own,
/// and cannot drive another tenant's connection, regardless of what the browser sends.
/// </summary>
public sealed class BankingUserContextHandler(IHttpContextAccessor httpContextAccessor) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Never trust an inbound value for these headers.
        request.Headers.Remove(BackendContextHeaders.UserId);
        request.Headers.Remove(BackendContextHeaders.SpaceId);

        var context = httpContextAccessor.HttpContext;
        if (context is not null &&
            context.User.Identity?.IsAuthenticated == true &&
            Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var authUserId))
        {
            var users = context.RequestServices.GetRequiredService<UserManager<AuthUser>>();
            var authUser = await users.FindByIdAsync(authUserId.ToString());
            if (authUser is { IsDisabled: false } && authUser.FinanceUserId != Guid.Empty)
            {
                request.Headers.TryAddWithoutValidation(BackendContextHeaders.UserId, authUser.FinanceUserId.ToString("D"));
                if (Guid.TryParse(context.Request.Query["fullWorthSpaceId"], out var spaceId) && spaceId != Guid.Empty)
                    request.Headers.TryAddWithoutValidation(BackendContextHeaders.SpaceId, spaceId.ToString("D"));
            }
        }

        return await base.SendAsync(request, cancellationToken);
    }
}
