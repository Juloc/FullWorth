using System.Security.Claims;
using FullWorth.Web.Modules.Auth;
using Microsoft.AspNetCore.Identity;

namespace FullWorth.Web.Security.BackendContext;

/// <summary>
/// Attaches trusted FullWorth caller identity and browser-presence (PSU) context to the internal
/// Banking service. All inbound Psu-* values are discarded and rebuilt from the actual ASP.NET
/// request so arbitrary browser headers cannot impersonate a different online context.
/// </summary>
public sealed class BankingUserContextHandler(IHttpContextAccessor httpContextAccessor) : DelegatingHandler
{
    private static readonly string[] PsuHeaders =
    [
        "Psu-Ip-Address",
        "Psu-User-Agent",
        "Psu-Referer",
        "Psu-Accept",
        "Psu-Accept-Charset",
        "Psu-Accept-Encoding",
        "Psu-Accept-language",
        "Psu-Geo-Location"
    ];

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        request.Headers.Remove(BackendContextHeaders.UserId);
        request.Headers.Remove(BackendContextHeaders.SpaceId);
        foreach (var name in PsuHeaders) request.Headers.Remove(name);

        var context = httpContextAccessor.HttpContext;
        if (context is not null)
        {
            AddPsuContext(request, context);

            if (context.User.Identity?.IsAuthenticated == true &&
                Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var authUserId))
            {
                var users = context.RequestServices.GetRequiredService<UserManager<AuthUser>>();
                var authUser = await users.FindByIdAsync(authUserId.ToString());
                if (authUser is { IsDisabled: false } && authUser.FinanceUserId != Guid.Empty)
                {
                    request.Headers.TryAddWithoutValidation(
                        BackendContextHeaders.UserId,
                        authUser.FinanceUserId.ToString("D"));
                    if (Guid.TryParse(context.Request.Query["fullWorthSpaceId"], out var spaceId) &&
                        spaceId != Guid.Empty)
                        request.Headers.TryAddWithoutValidation(
                            BackendContextHeaders.SpaceId,
                            spaceId.ToString("D"));
                }
            }
        }

        return await base.SendAsync(request, cancellationToken);
    }

    private static void AddPsuContext(HttpRequestMessage outbound, HttpContext inbound)
    {
        Add(outbound, "Psu-Ip-Address", inbound.Connection.RemoteIpAddress?.ToString(), 64);
        Add(outbound, "Psu-User-Agent", inbound.Request.Headers.UserAgent.ToString(), 1024);
        Add(outbound, "Psu-Referer", inbound.Request.Headers.Referer.ToString(), 2048);
        Add(outbound, "Psu-Accept", inbound.Request.Headers.Accept.ToString(), 1024);
        Add(outbound, "Psu-Accept-Charset", inbound.Request.Headers.AcceptCharset.ToString(), 512);
        Add(outbound, "Psu-Accept-Encoding", inbound.Request.Headers.AcceptEncoding.ToString(), 512);
        Add(outbound, "Psu-Accept-language", inbound.Request.Headers.AcceptLanguage.ToString(), 512);

        // Psu-Geo-Location is intentionally absent. It may only be added by a future explicit
        // geolocation-consent feature; merely visiting FullWorth is not permission to expose location.
    }

    private static void Add(HttpRequestMessage request, string name, string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var clean = new string(value.Trim()
            .Where(character => !char.IsControl(character))
            .Take(maxLength)
            .ToArray());
        if (clean.Length > 0) request.Headers.TryAddWithoutValidation(name, clean);
    }
}
