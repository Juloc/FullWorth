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
            Apply(context.Response.Headers);
            return Task.CompletedTask;
        });

        await _next(context);
    }

    private void Apply(IHeaderDictionary headers)
    {
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
    }
}
