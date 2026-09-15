using System.Net;
using FullWorth.Web.Security.Headers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpsPolicy;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace FullWorth.Web.Tests.Security.Headers;

public sealed class SecurityHeadersMiddlewareTests
{
    [Theory]
    [InlineData("/ok", HttpStatusCode.OK)]
    [InlineData("/unauthorized", HttpStatusCode.Unauthorized)]
    [InlineData("/forbidden", HttpStatusCode.Forbidden)]
    [InlineData("/missing", HttpStatusCode.NotFound)]
    [InlineData("/too-many", HttpStatusCode.TooManyRequests)]
    public async Task SecurityHeaders_ApplyToSuccessAndErrorResponses(string path, HttpStatusCode expectedStatus)
    {
        await using var app = await CreateAppAsync();
        using var response = await app.GetTestClient().GetAsync(path);

        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal(SecurityHeadersPolicy.ContentSecurityPolicy, Header(response, "Content-Security-Policy"));
        Assert.False(response.Headers.Contains("Content-Security-Policy-Report-Only"));
        Assert.Equal("nosniff", Header(response, "X-Content-Type-Options"));
        Assert.Equal(SecurityHeadersPolicy.ReferrerPolicy, Header(response, "Referrer-Policy"));
        Assert.Equal(SecurityHeadersPolicy.PermissionsPolicy, Header(response, "Permissions-Policy"));
        Assert.Equal("DENY", Header(response, "X-Frame-Options"));
    }

    [Theory]
    [InlineData("/auth/login", "/auth/login")]
    [InlineData("/auth/login?returnUrl=%2Fdashboard", "/auth/login")]
    [InlineData("/auth/register", "/auth/register")]
    [InlineData("/auth/register/", "/auth/register")]
    public async Task PublicAuthEntryPages_AreIndexableAndCanonical(string requestPath, string canonicalPath)
    {
        await using var app = await CreateAppAsync();
        using var response = await app.GetTestClient().GetAsync(requestPath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("X-Robots-Tag"));
        Assert.Equal($"<http://localhost{canonicalPath}>; rel=\"canonical\"", Header(response, "Link"));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/auth")]
    [InlineData("/auth/index.html")]
    [InlineData("/auth/forgot-password")]
    [InlineData("/auth/reset-password")]
    [InlineData("/auth/two-factor")]
    [InlineData("/settings")]
    [InlineData("/ok")]
    public async Task PrivateTechnicalAndDuplicateUrls_AreNoIndex(string path)
    {
        await using var app = await CreateAppAsync();
        using var response = await app.GetTestClient().GetAsync(path);

        Assert.Equal("noindex, follow", Header(response, "X-Robots-Tag"));
    }

    [Fact]
    public async Task Policy_DoesNotDeriveSourcesFromUntrustedOrigin()
    {
        await using var app = await CreateAppAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/ok");
        request.Headers.TryAddWithoutValidation("Origin", "https://attacker.example");
        using var response = await app.GetTestClient().SendAsync(request);

        Assert.Equal(SecurityHeadersPolicy.ContentSecurityPolicy, Header(response, "Content-Security-Policy"));
        Assert.DoesNotContain("attacker.example", Header(response, "Content-Security-Policy"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HstsConfiguration_IsProductionOnlyAndConservative()
    {
        Assert.False(SecurityHeadersPolicy.ShouldUseHsts(Environments.Development));
        Assert.True(SecurityHeadersPolicy.ShouldUseHsts(Environments.Production));
        Assert.False(SecurityHeadersPolicy.ShouldUseHsts(Environments.Staging));

        var services = new ServiceCollection();
        services.AddFinanceSecurityHeaders();
        using var provider = services.BuildServiceProvider();
        var hsts = provider.GetRequiredService<IOptions<HstsOptions>>().Value;

        Assert.Equal(TimeSpan.FromDays(180), hsts.MaxAge);
        Assert.False(hsts.IncludeSubDomains);
        Assert.False(hsts.Preload);
    }

    private static async Task<WebApplication> CreateAppAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development
        });
        builder.WebHost.UseTestServer();
        builder.Services.AddFinanceSecurityHeaders();

        var app = builder.Build();
        app.UseFinanceSecurityHeaders();
        app.MapGet("/ok", () => Results.Ok(new { ok = true }));
        app.MapGet("/auth/login", () => Results.Text("<html>login</html>", "text/html"));
        app.MapGet("/auth/register", () => Results.Text("<html>register</html>", "text/html"));
        app.MapGet("/auth/register/", () => Results.Text("<html>register</html>", "text/html"));
        app.MapGet("/auth/forgot-password", () => Results.Text("<html>forgot</html>", "text/html"));
        app.MapGet("/auth/reset-password", () => Results.Text("<html>reset</html>", "text/html"));
        app.MapGet("/auth/two-factor", () => Results.Text("<html>two-factor</html>", "text/html"));
        app.MapGet("/unauthorized", () => Results.StatusCode(StatusCodes.Status401Unauthorized));
        app.MapGet("/forbidden", () => Results.StatusCode(StatusCodes.Status403Forbidden));
        app.MapGet("/too-many", () => Results.StatusCode(StatusCodes.Status429TooManyRequests));
        await app.StartAsync();
        return app;
    }

    private static string Header(HttpResponseMessage response, string name)
    {
        Assert.True(response.Headers.TryGetValues(name, out var values), $"Missing header: {name}");
        return Assert.Single(values);
    }
}
