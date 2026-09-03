using System.Net;
using System.Security.Claims;
using FullWorth.Web.Security.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Web.Tests.Security.RateLimiting;

public sealed class RateLimitMiddlewareTests
{
    [Fact]
    public async Task Login_requests_within_limit_succeed_and_next_request_is_429()
    {
        using var server = CreateServer(("RateLimits:Login:PermitLimit", "2"));
        using var client = CreateClient(server, ip: "192.0.2.40");

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/auth/login", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/auth/login", null)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.PostAsync("/auth/login", null)).StatusCode);
    }

    [Fact]
    public async Task Password_reset_request_above_limit_returns_429()
    {
        using var server = CreateServer(("RateLimits:PasswordReset:PermitLimit", "1"));
        using var client = CreateClient(server, ip: "192.0.2.41");

        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync("/auth/password-reset/request", null)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.PostAsync("/auth/password-reset/request", null)).StatusCode);
    }

    [Fact]
    public async Task Passkey_operations_share_the_passkey_budget()
    {
        using var server = CreateServer(("RateLimits:Passkey:PermitLimit", "1"));
        using var client = CreateClient(server, ip: "192.0.2.42");

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/auth/passkeys/login/begin", null)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.PostAsync("/auth/passkeys/login/complete", null)).StatusCode);
    }

    [Fact]
    public async Task Browser_api_allows_realistic_burst()
    {
        using var server = CreateServer();
        using var client = CreateClient(server, ip: "192.0.2.43");

        for (var i = 0; i < 30; i++)
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/bff/backend/test")).StatusCode);
    }

    [Fact]
    public async Task Receipt_upload_policy_limits_expensive_starts()
    {
        using var server = CreateServer(("RateLimits:ReceiptUpload:PermitLimit", "1"));
        using var client = CreateClient(server, ip: "192.0.2.44", userId: Guid.NewGuid());

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/receipts/upload", null)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.PostAsync("/receipts/upload", null)).StatusCode);
    }

    [Fact]
    public async Task Two_authenticated_users_do_not_share_browser_api_bucket()
    {
        using var server = CreateServer(("RateLimits:BrowserApi:PermitLimit", "1"));
        using var first = CreateClient(server, ip: "192.0.2.45", userId: Guid.NewGuid());
        using var second = CreateClient(server, ip: "192.0.2.45", userId: Guid.NewGuid());

        Assert.Equal(HttpStatusCode.OK, (await first.GetAsync("/bff/backend/test")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await first.GetAsync("/bff/backend/test")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await second.GetAsync("/bff/backend/test")).StatusCode);
    }

    [Fact]
    public async Task Anonymous_clients_on_same_ip_share_bucket()
    {
        using var server = CreateServer(("RateLimits:BrowserApi:PermitLimit", "1"));
        using var first = CreateClient(server, ip: "192.0.2.46");
        using var second = CreateClient(server, ip: "192.0.2.46");

        Assert.Equal(HttpStatusCode.OK, (await first.GetAsync("/bff/backend/test")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await second.GetAsync("/bff/backend/test")).StatusCode);
    }

    [Fact]
    public async Task Null_ip_requests_remain_bounded()
    {
        using var server = CreateServer(("RateLimits:BrowserApi:PermitLimit", "1"));
        using var client = server.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/bff/backend/test")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.GetAsync("/bff/backend/test")).StatusCode);
    }

    [Fact]
    public async Task Rejection_body_is_generic_and_contains_no_account_identifier()
    {
        using var server = CreateServer(("RateLimits:Login:PermitLimit", "1"));
        using var client = CreateClient(server, ip: "192.0.2.47");
        const string account = "person@example.test";

        _ = await client.PostAsync("/auth/login", null);
        var response = await client.PostAsync($"/auth/login?email={Uri.EscapeDataString(account)}", null);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Contains(RateLimitServiceExtensions.RejectionMessage, body, StringComparison.Ordinal);
        Assert.DoesNotContain(account, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("user:", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ip:", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Rejection_does_not_reveal_account_existence()
    {
        var existing = await GetRejectedLoginBodyAsync("existing@example.test", "192.0.2.48");
        var missing = await GetRejectedLoginBodyAsync("missing@example.test", "192.0.2.49");

        Assert.Equal(existing, missing);
    }

    [Fact]
    public async Task Retry_after_is_present_when_fixed_window_lease_provides_it()
    {
        using var server = CreateServer(
            ("RateLimits:Login:PermitLimit", "1"),
            ("RateLimits:Login:WindowSeconds", "60"));
        using var client = CreateClient(server, ip: "192.0.2.50");

        _ = await client.PostAsync("/auth/login", null);
        var response = await client.PostAsync("/auth/login", null);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("Retry-After", out var values));
        Assert.True(int.TryParse(values.Single(), out var seconds));
        Assert.True(seconds > 0);
    }

    [Fact]
    public async Task Login_page_get_does_not_consume_login_attempt_budget()
    {
        using var server = CreateServer(("RateLimits:Login:PermitLimit", "1"));
        using var client = CreateClient(server, ip: "192.0.2.51");

        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/auth/login")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/auth/login", null)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.PostAsync("/auth/login", null)).StatusCode);
    }

    [Fact]
    public async Task Health_endpoint_is_unaffected_by_auth_rate_limit()
    {
        using var server = CreateServer(("RateLimits:Login:PermitLimit", "1"));
        using var client = CreateClient(server, ip: "192.0.2.52");

        _ = await client.PostAsync("/auth/login", null);
        _ = await client.PostAsync("/auth/login", null);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
    }

    [Fact]
    public async Task Static_asset_endpoint_is_unaffected_by_api_limiter()
    {
        using var server = CreateServer(("RateLimits:BrowserApi:PermitLimit", "1"));
        using var client = CreateClient(server, ip: "192.0.2.53");

        _ = await client.GetAsync("/bff/backend/test");
        _ = await client.GetAsync("/bff/backend/test");

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/app.js")).StatusCode);
    }

    [Fact]
    public async Task Passkey_registration_uses_authenticated_user_partition()
    {
        using var server = CreateServer(("RateLimits:Passkey:PermitLimit", "1"));
        using var first = CreateClient(server, ip: "192.0.2.54", userId: Guid.NewGuid());
        using var second = CreateClient(server, ip: "192.0.2.54", userId: Guid.NewGuid());

        Assert.Equal(HttpStatusCode.OK, (await first.PostAsync("/auth/passkeys/register/begin", null)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await first.PostAsync("/auth/passkeys/register/complete", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await second.PostAsync("/auth/passkeys/register/begin", null)).StatusCode);
    }

    [Fact]
    public void Invalid_configuration_fails_during_rate_limit_registration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RateLimits:Login:PermitLimit"] = "0"
            })
            .Build();
        var services = new ServiceCollection();

        Assert.Throws<InvalidOperationException>(() => services.AddFinanceRateLimiting(configuration));
    }

    private static async Task<string> GetRejectedLoginBodyAsync(string account, string ip)
    {
        using var server = CreateServer(("RateLimits:Login:PermitLimit", "1"));
        using var client = CreateClient(server, ip: ip);
        _ = await client.PostAsync("/auth/login", null);
        var response = await client.PostAsync($"/auth/login?email={Uri.EscapeDataString(account)}", null);
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    private static HttpClient CreateClient(TestServer server, string? ip = null, Guid? userId = null)
    {
        var client = server.CreateClient();
        if (ip is not null)
            client.DefaultRequestHeaders.Add("X-Test-IP", ip);
        if (userId is not null)
            client.DefaultRequestHeaders.Add("X-Test-User", userId.Value.ToString());
        return client;
    }

    private static TestServer CreateServer(params (string Key, string Value)[] overrides)
    {
        var settings = new Dictionary<string, string?>
        {
            ["RateLimits:Login:WindowSeconds"] = "60",
            ["RateLimits:PasswordReset:WindowSeconds"] = "60",
            ["RateLimits:Passkey:WindowSeconds"] = "60",
            ["RateLimits:BrowserApi:WindowSeconds"] = "60",
            ["RateLimits:ReceiptUpload:WindowSeconds"] = "60"
        };

        foreach (var (key, value) in overrides)
            settings[key] = value;

        var builder = new WebHostBuilder()
            .ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(settings))
            .ConfigureServices((context, services) =>
            {
                services.AddRouting();
                services.AddFinanceRateLimiting(context.Configuration);
            })
            .Configure(app =>
            {
                app.UseRouting();
                app.Use(async (context, next) =>
                {
                    context.Connection.RemoteIpAddress = null;
                    if (context.Request.Headers.TryGetValue("X-Test-IP", out var rawIp) &&
                        IPAddress.TryParse(rawIp.ToString(), out var ip))
                        context.Connection.RemoteIpAddress = ip;

                    if (context.Request.Headers.TryGetValue("X-Test-User", out var rawUser) &&
                        Guid.TryParse(rawUser.ToString(), out var userId))
                    {
                        context.User = new ClaimsPrincipal(new ClaimsIdentity(
                            new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) },
                            authenticationType: "test"));
                    }

                    await next();
                });
                app.UseRateLimiter();
                app.UseEndpoints(endpoints =>
                {
                    endpoints.MapGet("/auth/login", () => Results.Ok());
                    endpoints.MapPost("/auth/login", () => Results.Ok())
                        .RequireRateLimiting(RateLimitPolicies.Login);
                    endpoints.MapPost("/auth/password-reset/request", () => Results.Accepted())
                        .RequireRateLimiting(RateLimitPolicies.PasswordReset);
                    endpoints.MapPost("/auth/password-reset/complete", () => Results.NoContent())
                        .RequireRateLimiting(RateLimitPolicies.PasswordReset);
                    endpoints.MapPost("/auth/passkeys/login/begin", () => Results.Ok())
                        .RequireRateLimiting(RateLimitPolicies.Passkey);
                    endpoints.MapPost("/auth/passkeys/login/complete", () => Results.Ok())
                        .RequireRateLimiting(RateLimitPolicies.Passkey);
                    endpoints.MapPost("/auth/passkeys/register/begin", () => Results.Ok())
                        .RequireRateLimiting(RateLimitPolicies.Passkey);
                    endpoints.MapPost("/auth/passkeys/register/complete", () => Results.Ok())
                        .RequireRateLimiting(RateLimitPolicies.Passkey);
                    endpoints.MapGet("/bff/backend/test", () => Results.Ok())
                        .RequireRateLimiting(RateLimitPolicies.BrowserApi);
                    endpoints.MapPost("/bff/banking/test", () => Results.Ok())
                        .RequireRateLimiting(RateLimitPolicies.BrowserApi);
                    endpoints.MapPost("/receipts/upload", () => Results.Ok())
                        .RequireRateLimiting(RateLimitPolicies.ReceiptUpload);
                    endpoints.MapGet("/health", () => Results.Ok());
                    endpoints.MapGet("/app.js", () => Results.Text("app"));
                });
            });

        return new TestServer(builder);
    }
}
