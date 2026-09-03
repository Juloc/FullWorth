using System.Net;
using System.Net.Http.Json;
using FullWorth.Web.Modules.Auth;
using FullWorth.Web.Security.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Web.Tests.Security;

public sealed class WaveDSecurityIntegrationTests
{
    private const string Password = "correct horse battery staple";

    [Fact]
    public async Task RealProgram_MissingAntiforgeryRejectsRepresentativeUnsafeRoutes_AndSafeGetsRemainUsable()
    {
        await using var factory = new FullWorthWebFactory();
        var user = await CreateUserAsync(factory);
        using var protectedClient = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        var authCookie = await LoginAndGetCookieAsync(protectedClient, user.Email);
        using var raw = factory.CreateRawClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });

        foreach (var request in new[]
        {
            Json(HttpMethod.Post, "/auth/logout", authCookie, new { }),
            Json(HttpMethod.Post, "/auth/password-reset/request", null, new { email = user.Email }),
            Json(HttpMethod.Post, "/auth/password-reset/complete", null, new { email = user.Email, token = "invalid", newPassword = Password + "2" }),
            Json(HttpMethod.Post, "/auth/recovery-codes/regenerate", authCookie, new { }),
            Json(HttpMethod.Post, "/auth/sessions/revoke-others", authCookie, new { }),
            Json(HttpMethod.Post, "/auth/passkeys/register/begin", authCookie, new { }),
            Json(HttpMethod.Delete, $"/auth/passkeys/{Guid.NewGuid()}", authCookie, null),
            Json(HttpMethod.Post, "/bff/backend/api/test", authCookie, new { value = 1 })
        })
        {
            using (request)
            using (var response = await raw.SendAsync(request))
            {
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
                AssertSecurityHeaders(response);
            }
        }

        using var health = await raw.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        using var loginPage = await raw.GetAsync("/auth/login");
        Assert.Equal(HttpStatusCode.OK, loginPage.StatusCode);
        using var authJs = await raw.GetAsync("/auth/auth.js");
        Assert.Equal(HttpStatusCode.OK, authJs.StatusCode);
        using var locale = await raw.GetAsync("/locales/en.json");
        Assert.Equal(HttpStatusCode.OK, locale.StatusCode);

        using var passkeys = Request(HttpMethod.Get, "/auth/passkeys", authCookie);
        using var passkeyList = await raw.SendAsync(passkeys);
        Assert.Equal(HttpStatusCode.OK, passkeyList.StatusCode);
        using var bff = Request(HttpMethod.Get, "/bff/backend/api/test", authCookie);
        using var bffGet = await raw.SendAsync(bff);
        Assert.Equal(HttpStatusCode.OK, bffGet.StatusCode);
    }

    [Fact]
    public async Task RealProgram_ValidAntiforgeryAllowsRepresentativeUnsafeRoutes()
    {
        await using var factory = new FullWorthWebFactory();
        var user = await CreateUserAsync(factory);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        var authCookie = await LoginAndGetCookieAsync(client, user.Email);

        using var recovery = await client.SendAsync(Json(HttpMethod.Post, "/auth/recovery-codes/regenerate", authCookie, new { }));
        Assert.Equal(HttpStatusCode.OK, recovery.StatusCode);

        using var session = await client.SendAsync(Json(HttpMethod.Delete, $"/auth/sessions/{Guid.NewGuid()}", authCookie, null));
        Assert.Equal(HttpStatusCode.NotFound, session.StatusCode);

        using var passkeyBegin = await client.SendAsync(Json(HttpMethod.Post, "/auth/passkeys/register/begin", authCookie, new { }));
        Assert.Equal(HttpStatusCode.OK, passkeyBegin.StatusCode);

        using var passkeyDelete = await client.SendAsync(Json(HttpMethod.Delete, $"/auth/passkeys/{Guid.NewGuid()}", authCookie, null));
        Assert.Equal(HttpStatusCode.NotFound, passkeyDelete.StatusCode);

        using var bff = await client.SendAsync(Json(HttpMethod.Post, "/bff/backend/api/test", authCookie, new { value = 1 }));
        Assert.Equal(HttpStatusCode.OK, bff.StatusCode);

        using var resetRequest = await client.PostAsJsonAsync("/auth/password-reset/request", new { email = $"unknown-{Guid.NewGuid():N}@example.com" });
        Assert.Equal(HttpStatusCode.Accepted, resetRequest.StatusCode);

        using var resetComplete = await client.PostAsJsonAsync("/auth/password-reset/complete", new { email = user.Email, token = "invalid", newPassword = Password + "2" });
        Assert.Equal(HttpStatusCode.BadRequest, resetComplete.StatusCode);

        using var logout = await client.SendAsync(Json(HttpMethod.Post, "/auth/logout", authCookie, new { }));
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
    }

    [Fact]
    public async Task AntiforgeryToken_IsNoStoreAndNotPlacedInUrl()
    {
        await using var factory = new FullWorthWebFactory();
        using var raw = factory.CreateRawClient();
        using var response = await raw.GetAsync("/auth/antiforgery");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token=", response.RequestMessage?.RequestUri?.Query ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RealProgram_SecurityHeadersCoverNormalNotFoundUnauthorizedAndAntiforgeryFailure()
    {
        await using var factory = new FullWorthWebFactory();
        using var raw = factory.CreateRawClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });

        using var ok = await raw.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        AssertSecurityHeaders(ok);

        using var missing = await raw.GetAsync("/appsettings.json");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        AssertSecurityHeaders(missing);

        using var unauthorized = await raw.PostAsJsonAsync("/auth/passkeys/register/begin", new { });
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        AssertSecurityHeaders(unauthorized);

        Assert.DoesNotContain("unsafe-eval", Header(ok, "Content-Security-Policy"), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("script-src 'self' 'unsafe-inline'", Header(ok, "Content-Security-Policy"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("frame-ancestors 'none'", Header(ok, "Content-Security-Policy"), StringComparison.Ordinal);
        Assert.Contains("connect-src 'self'", Header(ok, "Content-Security-Policy"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RealProgram_LoginPasswordResetAndPasskeyRateLimits_ReturnGeneric429WithHeaders()
    {
        await using var factory = new SmallRateFullWorthWebFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        for (var i = 0; i < 2; i++)
        {
            using var response = await client.PostAsJsonAsync("/auth/login", new { email = $"unknown-{i}@example.com", password = Password });
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        using (var limited = await client.PostAsJsonAsync("/auth/login", new { email = "unknown-3@example.com", password = Password }))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
            AssertSecurityHeaders(limited);
            Assert.DoesNotContain("user", await limited.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        }

        for (var i = 0; i < 2; i++)
        {
            using var response = await client.PostAsJsonAsync("/auth/password-reset/request", new { email = $"reset-{i}@example.com" });
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        }
        using (var limited = await client.PostAsJsonAsync("/auth/password-reset/request", new { email = "reset-3@example.com" }))
            Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);

        for (var i = 0; i < 2; i++)
        {
            using var response = await client.PostAsJsonAsync("/auth/passkeys/login/begin", new { });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        using (var limited = await client.PostAsJsonAsync("/auth/passkeys/login/begin", new { }))
            Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);

        using var health = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    [Fact]
    public async Task RealProgram_BrowserApiRateLimitPartitionsAuthenticatedUsersIndependently()
    {
        await using var factory = new BrowserPartitionFullWorthWebFactory();
        var first = await CreateUserAsync(factory);
        var second = await CreateUserAsync(factory);
        using var firstClient = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        using var secondClient = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        var firstCookie = await LoginAndGetCookieAsync(firstClient, first.Email);
        var secondCookie = await LoginAndGetCookieAsync(secondClient, second.Email);

        using (var request = Request(HttpMethod.Get, "/bff/backend/api/test", firstCookie))
        using (var response = await firstClient.SendAsync(request))
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using (var request = Request(HttpMethod.Get, "/bff/backend/api/test", firstCookie))
        using (var response = await firstClient.SendAsync(request))
            Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        using (var request = Request(HttpMethod.Get, "/bff/backend/api/test", secondCookie))
        using (var response = await secondClient.SendAsync(request))
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<(Guid Id, string Email)> CreateUserAsync(FullWorthWebFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var auth = scope.ServiceProvider.GetRequiredService<AuthService>();
        var email = $"wave-d-{Guid.NewGuid():N}@example.com";
        var result = await auth.CreateUserAsync(new CreateAuthUserRequest(Guid.NewGuid(), email, Password));
        Assert.True(result.Succeeded, string.Join("; ", result.Errors));
        return (result.User!.Id, email);
    }

    private static async Task<string> LoginAndGetCookieAsync(HttpClient client, string email)
    {
        using var response = await client.PostAsJsonAsync("/auth/login", new { email, password = Password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return response.Headers.GetValues("Set-Cookie")
            .Single(x => x.StartsWith("Finance.Auth=", StringComparison.Ordinal))
            .Split(';', 2)[0];
    }

    private static HttpRequestMessage Json(HttpMethod method, string path, string? cookie, object? body)
    {
        var request = Request(method, path, cookie);
        if (body is not null) request.Content = JsonContent.Create(body);
        return request;
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, string? cookie)
    {
        var request = new HttpRequestMessage(method, path);
        if (!string.IsNullOrWhiteSpace(cookie)) request.Headers.TryAddWithoutValidation("Cookie", cookie);
        return request;
    }

    private static void AssertSecurityHeaders(HttpResponseMessage response)
    {
        Assert.Equal(SecurityHeadersPolicy.ContentSecurityPolicy, Header(response, "Content-Security-Policy"));
        Assert.Equal("nosniff", Header(response, "X-Content-Type-Options"));
        Assert.Equal(SecurityHeadersPolicy.ReferrerPolicy, Header(response, "Referrer-Policy"));
        Assert.Equal(SecurityHeadersPolicy.PermissionsPolicy, Header(response, "Permissions-Policy"));
        Assert.Equal("DENY", Header(response, "X-Frame-Options"));
    }

    private static string Header(HttpResponseMessage response, string name)
    {
        Assert.True(response.Headers.TryGetValues(name, out var values), $"Missing header: {name}");
        return Assert.Single(values);
    }

    private sealed class SmallRateFullWorthWebFactory : FullWorthWebFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RateLimits:Login:PermitLimit"] = "2",
                ["RateLimits:PasswordReset:PermitLimit"] = "2",
                ["RateLimits:Passkey:PermitLimit"] = "2",
                ["RateLimits:BrowserApi:PermitLimit"] = "10",
                ["RateLimits:Login:WindowSeconds"] = "300",
                ["RateLimits:PasswordReset:WindowSeconds"] = "300",
                ["RateLimits:Passkey:WindowSeconds"] = "300"
            }));
        }
    }

    private sealed class BrowserPartitionFullWorthWebFactory : FullWorthWebFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RateLimits:Login:PermitLimit"] = "100",
                ["RateLimits:BrowserApi:PermitLimit"] = "1",
                ["RateLimits:BrowserApi:WindowSeconds"] = "300"
            }));
        }
    }
}
