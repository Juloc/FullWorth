using System.Net;

namespace FullWorth.Web.Security;

/// <summary>
/// Last line of defense on an outbound service client: refuses to send anything whose target is not
/// exactly the configured service origin, and attaches the service API key only AFTER that check.
/// Even if a caller ever bypasses the route-level validation, no request — and especially no key —
/// can reach a foreign host through a client wrapped with this handler.
/// </summary>
public sealed class ServiceProxyGuardHandler(Uri expectedBase, string? apiKeyHeader = null, string? apiKey = null) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Relative URIs resolve against the client BaseAddress only later in HttpClient — resolve
        // here first so the origin check always sees the final absolute target.
        var target = request.RequestUri is { IsAbsoluteUri: false } relative
            ? new Uri(expectedBase, relative)
            : request.RequestUri;

        if (!ProxyTargetValidator.IsSameOrigin(target, expectedBase))
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                RequestMessage = request,
                ReasonPhrase = "Proxy target rejected"
            });
        }

        request.RequestUri = target;
        if (!string.IsNullOrWhiteSpace(apiKeyHeader))
        {
            // Never forward an inbound value for this header; the key is set exclusively here.
            request.Headers.Remove(apiKeyHeader);
            if (!string.IsNullOrWhiteSpace(apiKey))
                request.Headers.TryAddWithoutValidation(apiKeyHeader, apiKey);
        }

        return base.SendAsync(request, cancellationToken);
    }
}
