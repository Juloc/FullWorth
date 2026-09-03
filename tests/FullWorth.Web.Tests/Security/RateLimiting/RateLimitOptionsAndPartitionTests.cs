using System.Net;
using System.Security.Claims;
using FullWorth.Web.Modules.Auth;
using FullWorth.Web.Security.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace FullWorth.Web.Tests.Security.RateLimiting;

public sealed class RateLimitOptionsAndPartitionTests
{
    [Fact]
    public void Policy_names_are_centralized_and_unique()
    {
        var names = new[]
        {
            RateLimitPolicies.Login,
            RateLimitPolicies.PasswordReset,
            RateLimitPolicies.Passkey,
            RateLimitPolicies.BrowserApi,
            RateLimitPolicies.ReceiptUpload
        };

        Assert.Equal(5, names.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Defaults_are_conservative_and_usable()
    {
        var options = new RateLimitOptions();

        Assert.Equal(10, options.Login.PermitLimit);
        Assert.Equal(300, options.Login.WindowSeconds);
        Assert.Equal(5, options.PasswordReset.PermitLimit);
        Assert.Equal(900, options.PasswordReset.WindowSeconds);
        Assert.Equal(20, options.Passkey.PermitLimit);
        Assert.Equal(300, options.Passkey.WindowSeconds);
        Assert.Equal(600, options.BrowserApi.PermitLimit);
        Assert.Equal(60, options.BrowserApi.WindowSeconds);
        Assert.Equal(10, options.ReceiptUpload.PermitLimit);
        Assert.Equal(600, options.ReceiptUpload.WindowSeconds);
        Assert.All(new[] { options.Login, options.PasswordReset, options.Passkey, options.BrowserApi, options.ReceiptUpload }, item => Assert.Equal(0, item.QueueLimit));
    }

    [Fact]
    public void Browser_api_has_higher_allowance_than_login()
    {
        var options = new RateLimitOptions();
        Assert.True(options.BrowserApi.PermitLimit > options.Login.PermitLimit);
    }

    [Fact]
    public void Receipt_upload_is_more_restrictive_than_browser_api()
    {
        var options = new RateLimitOptions();
        var browserPerMinute = options.BrowserApi.PermitLimit * 60d / options.BrowserApi.WindowSeconds;
        var receiptPerMinute = options.ReceiptUpload.PermitLimit * 60d / options.ReceiptUpload.WindowSeconds;
        Assert.True(receiptPerMinute < browserPerMinute);
    }

    [Fact]
    public void Configuration_overrides_defaults()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RateLimits:Login:PermitLimit"] = "3",
                ["RateLimits:Login:WindowSeconds"] = "42",
                ["RateLimits:BrowserApi:QueueLimit"] = "2"
            })
            .Build();

        var options = RateLimitOptions.FromConfiguration(configuration);

        Assert.Equal(3, options.Login.PermitLimit);
        Assert.Equal(42, options.Login.WindowSeconds);
        Assert.Equal(2, options.BrowserApi.QueueLimit);
        Assert.Equal(5, options.PasswordReset.PermitLimit);
    }

    [Theory]
    [InlineData(0, 60, 0)]
    [InlineData(1, 0, 0)]
    [InlineData(1, 60, -1)]
    public void Invalid_policy_options_are_rejected(int permitLimit, int windowSeconds, int queueLimit)
    {
        var options = new RateLimitOptions
        {
            Login = new RateLimitPolicyOptions
            {
                PermitLimit = permitLimit,
                WindowSeconds = windowSeconds,
                QueueLimit = queueLimit
            }
        };

        var result = new RateLimitOptionsValidator().Validate(null, options);
        Assert.True(result.Failed);
    }

    [Fact]
    public void Identity_lockout_defaults_are_unchanged()
    {
        var options = new AuthOptions();
        Assert.Equal(5, options.MaxFailedAccessAttempts);
        Assert.Equal(TimeSpan.FromMinutes(15), options.LockoutDuration);
    }

    [Fact]
    public void Anonymous_partition_uses_normalized_ip()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("::ffff:192.0.2.10");

        Assert.Equal("ip:192.0.2.10", RateLimitPartitionKeys.GetIpPartitionKey(context));
    }

    [Fact]
    public void Authenticated_partition_uses_auth_user_id()
    {
        var userId = Guid.NewGuid();
        var context = CreateAuthenticatedContext(userId, "192.0.2.20");

        Assert.Equal($"user:{userId:D}", RateLimitPartitionKeys.GetUserOrIpPartitionKey(context));
    }

    [Fact]
    public void Two_authenticated_users_get_different_partitions()
    {
        var first = RateLimitPartitionKeys.GetUserOrIpPartitionKey(CreateAuthenticatedContext(Guid.NewGuid(), "192.0.2.20"));
        var second = RateLimitPartitionKeys.GetUserOrIpPartitionKey(CreateAuthenticatedContext(Guid.NewGuid(), "192.0.2.20"));

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Two_anonymous_requests_from_same_ip_share_partition()
    {
        var first = new DefaultHttpContext();
        first.Connection.RemoteIpAddress = IPAddress.Parse("2001:db8::10");
        var second = new DefaultHttpContext();
        second.Connection.RemoteIpAddress = IPAddress.Parse("2001:db8::10");

        Assert.Equal(
            RateLimitPartitionKeys.GetIpPartitionKey(first),
            RateLimitPartitionKeys.GetIpPartitionKey(second));
    }

    [Fact]
    public void Null_ip_uses_deterministic_bounded_partition()
    {
        var first = new DefaultHttpContext();
        var second = new DefaultHttpContext();

        Assert.Equal(RateLimitPartitionKeys.UnknownIpPartition, RateLimitPartitionKeys.GetIpPartitionKey(first));
        Assert.Equal(RateLimitPartitionKeys.GetIpPartitionKey(first), RateLimitPartitionKeys.GetIpPartitionKey(second));
    }

    [Fact]
    public void Secret_request_values_do_not_enter_partition_key()
    {
        const string secret = "reset-token-or-password-secret";
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.30");
        context.Request.QueryString = new QueryString($"?token={secret}&password={secret}");
        context.Request.Headers["X-AuthUserId"] = secret;

        var key = RateLimitPartitionKeys.GetUserOrIpPartitionKey(context);

        Assert.DoesNotContain(secret, key, StringComparison.Ordinal);
        Assert.Equal("ip:192.0.2.30", key);
    }

    [Theory]
    [InlineData("POST", "/auth/login", RateLimitPolicies.Login)]
    [InlineData("POST", "/auth/password-reset/request", RateLimitPolicies.PasswordReset)]
    [InlineData("POST", "/auth/password-reset/complete", RateLimitPolicies.PasswordReset)]
    [InlineData("POST", "/auth/passkeys/login/begin", RateLimitPolicies.Passkey)]
    [InlineData("POST", "/auth/passkeys/login/complete", RateLimitPolicies.Passkey)]
    [InlineData("POST", "/auth/passkeys/register/begin", RateLimitPolicies.Passkey)]
    [InlineData("POST", "/auth/passkeys/register/complete", RateLimitPolicies.Passkey)]
    [InlineData("GET", "/bff/backend/api/accounts", RateLimitPolicies.BrowserApi)]
    [InlineData("POST", "/bff/banking/api/banking/sync", RateLimitPolicies.BrowserApi)]
    public void Policy_selection_maps_sensitive_and_bff_routes(string method, string path, string expected)
    {
        Assert.Equal(expected, RateLimitPolicySelection.ForRequest(method, new PathString(path)));
    }

    [Theory]
    [InlineData("GET", "/auth/login")]
    [InlineData("GET", "/auth/reset-password")]
    [InlineData("GET", "/health")]
    [InlineData("GET", "/app.js")]
    public void Policy_selection_leaves_pages_health_and_static_assets_unlimited(string method, string path)
    {
        Assert.Null(RateLimitPolicySelection.ForRequest(method, new PathString(path)));
    }

    private static DefaultHttpContext CreateAuthenticatedContext(Guid userId, string ip)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(ip);
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) },
            authenticationType: "test"));
        return context;
    }
}
