using Microsoft.Extensions.Options;

namespace FullWorth.Web.Security.Headers;

public sealed class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;
    private readonly SecurityHeadersOptions _options;

    public SecurityHeadersMiddleware(RequestDelegate next, IOptions<SecurityHeadersOptions> options)
    {
        _next = next;
        _options = options.Value;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            Apply(context);
            return Task.CompletedTask;
        });

        await _next(context);
    }

    private void Apply(HttpContext context)
    {
        var headers = context.Response.Headers;
        var cspHeader = _options.ReportOnly
            ? "Content-Security-Policy-Report-Only"
            : "Content-Security-Policy";
        var otherCspHeader = _options.ReportOnly
            ? "Content-Security-Policy"
            : "Content-Security-Policy-Report-Only";

        headers.Remove(otherCspHeader);
        headers[cspHeader] = SecurityHeadersPolicy.ContentSecurityPolicy;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["Referrer-Policy"] = SecurityHeadersPolicy.ReferrerPolicy;
        headers["Permissions-Policy"] = SecurityHeadersPolicy.PermissionsPolicy;

        if (_options.AddLegacyFrameProtection)
            headers["X-Frame-Options"] = "DENY";

        ApplySearchIndexingHeaders(context);
    }

    private static void ApplySearchIndexingHeaders(HttpContext context)
    {
        var path = (context.Request.Path.Value ?? string.Empty).TrimEnd('/');
        var isLogin = path.Equals("/auth/login", StringComparison.OrdinalIgnoreCase);
        var isRegister = path.Equals("/auth/register", StringComparison.OrdinalIgnoreCase);

        if (isLogin || isRegister)
        {
            context.Response.Headers.Remove("X-Robots-Tag");
            var canonicalPath = isRegister ? "/auth/register" : "/auth/login";
            context.Response.Headers.Append(
                "Link",
                $"<{context.Request.Scheme}://{context.Request.Host}{canonicalPath}>; rel=\"canonical\"");
            return;
        }

        // web.fullworth.de is an authenticated finance application, not a public content site.
        // Keep the two deliberate public entry pages crawlable and exclude every private, technical
        // or duplicate URL from search indexes. The header also covers redirects and non-HTML
        // responses, so crawlers cannot accidentally retain a private route just because they never
        // received the authenticated app shell.
        context.Response.Headers["X-Robots-Tag"] = "noindex, follow";
    }
}
