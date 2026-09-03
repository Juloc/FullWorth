using FullWorth.Web.Security.Headers;

namespace FullWorth.Web.Tests.Security.Headers;

public sealed class SecurityHeadersPolicyTests
{
    [Fact]
    public void ContentSecurityPolicy_IsRestrictiveAndAuditable()
    {
        Assert.Equal("default-src 'self'", Directive("default-src"));
        Assert.Equal("script-src 'self'", Directive("script-src"));
        Assert.Equal("style-src 'self'", Directive("style-src"));
        Assert.Equal("style-src-attr 'unsafe-inline'", Directive("style-src-attr"));
        Assert.Equal("object-src 'none'", Directive("object-src"));
        Assert.Equal("base-uri 'self'", Directive("base-uri"));
        Assert.Equal("frame-ancestors 'none'", Directive("frame-ancestors"));
        Assert.Equal("frame-src 'none'", Directive("frame-src"));
        Assert.Equal("form-action 'self'", Directive("form-action"));
        Assert.Equal("connect-src 'self'", Directive("connect-src"));
        Assert.Equal("worker-src 'self'", Directive("worker-src"));
        Assert.Equal("manifest-src 'self'", Directive("manifest-src"));

        var script = Directive("script-src");
        Assert.DoesNotContain("'unsafe-inline'", script, StringComparison.Ordinal);
        Assert.DoesNotContain("'unsafe-eval'", script, StringComparison.Ordinal);

        foreach (var name in new[]
                 {
                     "default-src", "script-src", "connect-src", "object-src", "base-uri",
                     "frame-ancestors", "frame-src", "form-action", "worker-src", "manifest-src"
                 })
        {
            Assert.DoesNotContain("*", Directive(name), StringComparison.Ordinal);
        }

        Assert.DoesNotContain("http://fullworth-backend", SecurityHeadersPolicy.ContentSecurityPolicy, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http://fullworth-banking", SecurityHeadersPolicy.ContentSecurityPolicy, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https:", Directive("script-src"), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("data:", Directive("script-src"), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("blob:", Directive("script-src"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ImageFontAndMediaSources_AreLimitedToCurrentSameOriginNeeds()
    {
        Assert.Equal("img-src 'self' data: https://enablebanking.com https://*.enablebanking.com", Directive("img-src"));
        Assert.Equal("font-src 'self'", Directive("font-src"));
        Assert.Equal("media-src 'self'", Directive("media-src"));
    }

    [Fact]
    public void ReferrerAndPermissionsPolicies_ArePrivacyPreserving()
    {
        Assert.Equal("strict-origin-when-cross-origin", SecurityHeadersPolicy.ReferrerPolicy);
        Assert.DoesNotContain("unsafe-url", SecurityHeadersPolicy.ReferrerPolicy, StringComparison.OrdinalIgnoreCase);

        foreach (var disabledFeature in new[] { "camera=()", "microphone=()", "geolocation=()", "payment=()", "usb=()", "serial=()" })
            Assert.Contains(disabledFeature, SecurityHeadersPolicy.PermissionsPolicy, StringComparison.Ordinal);

        Assert.DoesNotContain("clipboard", SecurityHeadersPolicy.PermissionsPolicy, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("publickey-credentials-get", SecurityHeadersPolicy.PermissionsPolicy, StringComparison.OrdinalIgnoreCase);
    }

    private static string Directive(string name) =>
        SecurityHeadersPolicy.ContentSecurityPolicy
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Single(value => value.StartsWith(name + " ", StringComparison.Ordinal));
}
