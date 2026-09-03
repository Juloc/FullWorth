using System.Net;
using System.Security.Claims;
using FullWorth.Web.Modules.Auth;
using FullWorth.Web.Security;
using FullWorth.Web.Modules.Sessions;
using Microsoft.AspNetCore.Identity;

namespace FullWorth.Web.Security.BackendContext;

public sealed class BackendUserContextHandler(
    IHttpContextAccessor httpContextAccessor,
    BackendContextOptions options) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        // SSRF second gate: the internal key and user id below must never travel to any origin
        // other than the configured backend, no matter what the caller composed as request URI.
        var target = request.RequestUri is { IsAbsoluteUri: false } relative
            ? new Uri(options.BackendBaseAddress, relative)
            : request.RequestUri;
        if (!ProxyTargetValidator.IsSameOrigin(target, options.BackendBaseAddress))
        {
            return new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                RequestMessage = request,
                ReasonPhrase = "Proxy target rejected"
            };
        }
        request.RequestUri = target;

        StripUntrustedHeaders(request);

        var context = httpContextAccessor.HttpContext;
        if (context?.User.Identity?.IsAuthenticated != true ||
            !Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var authUserId) ||
            !SessionClaims.TryGetSessionId(context.User, out _))
        {
            return Unauthorized(request);
        }

        var users = context.RequestServices.GetRequiredService<UserManager<AuthUser>>();
        var authUser = await users.FindByIdAsync(authUserId.ToString());
        // Not gated by IsLockedOutAsync: a transient password lockout must not block an already
        // authenticated user's proxied API calls (session DoS). IsDisabled still blocks forwarding.
        if (authUser is null ||
            authUser.IsDisabled ||
            authUser.FinanceUserId == Guid.Empty)
        {
            return Unauthorized(request);
        }

        request.Headers.TryAddWithoutValidation(BackendContextHeaders.InternalKey, options.InternalKey);
        request.Headers.TryAddWithoutValidation(BackendContextHeaders.UserId, authUser.FinanceUserId.ToString("D"));
        return await base.SendAsync(request, cancellationToken);
    }

    private static void StripUntrustedHeaders(HttpRequestMessage request)
    {
        foreach (var header in BackendContextHeaders.UntrustedForwardingHeaders)
            request.Headers.Remove(header);
    }

    private static HttpResponseMessage Unauthorized(HttpRequestMessage request) => new(HttpStatusCode.Unauthorized)
    {
        RequestMessage = request
    };
}
