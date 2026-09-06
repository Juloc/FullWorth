using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FullWorth.Web.Modules.Auth;
using FullWorth.Web.Modules.Recovery;
using FullWorth.Web.Modules.Sessions;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace FullWorth.Web.Tests.Auth;

public sealed class AuthIntegrationTests
{
    private const string Password = "correct horse battery staple";
    private const string NewPassword = "updated horse battery staple";

    [Fact]
    public async Task AnonymousAuthShellIsPublic_ButFinanceUiBffAndSecurityEndpointsAreProtected()
    {
        await using var factory = new FullWorthWebFactory();
        using var client = CreateClient(factory);

        foreach (var publicPath in new[] { "/auth/login", "/auth/register", "/auth/forgot-password", "/health" })
        {
            using var response = await client.GetAsync(publicPath);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        using (var finance = await client.GetAsync("/"))
        {
            Assert.Equal(HttpStatusCode.Redirect, finance.StatusCode);
            Assert.StartsWith("/auth/login?", finance.Headers.Location?.OriginalString);
        }

        foreach (var apiPath in new[] { "/bff/backend/api/accounts", "/auth/sessions/", "/auth/recovery-codes/status" })
        {
            using var response = await client.GetAsync(apiPath);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        using var changePassword = await client.PostAsJsonAsync("/auth/change-password", new { currentPassword = Password, newPassword = NewPassword });
        Assert.Equal(HttpStatusCode.Unauthorized, changePassword.StatusCode);
    }

    [Fact]
    public async Task ValidLoginCreatesPersistentSessionAndIntegratedCookie_WithSafeReturnUrl()
    {
        await using var factory = new FullWorthWebFactory();
        var user = await CreateUserAsync(factory);
        using var client = CreateClient(factory);

        var login = await LoginAsync(client, user.Email, Password, "/transactions?scope=current");

        Assert.Contains("Finance.Auth=", login.SetCookie, StringComparison.Ordinal);
        Assert.Contains("httponly", login.SetCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", login.SetCookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("; secure", login.SetCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("/transactions?scope=current", login.ReturnUrl);

        var sessions = await GetSessionsAsync(client, login.Cookie);
        var current = Assert.Single(sessions.Sessions, x => x.Current);
        Assert.True(current.Active);

        using var finance = await SendAsync(client, HttpMethod.Get, "/", login.Cookie);
        Assert.Equal(HttpStatusCode.OK, finance.StatusCode);

        using var bff = await SendAsync(client, HttpMethod.Get, "/bff/backend/api/accounts", login.Cookie);
        Assert.Equal(HttpStatusCode.OK, bff.StatusCode);
        var body = await bff.Content.ReadAsStringAsync();
        Assert.DoesNotContain(FullWorthWebFactory.BackendSecret, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(FullWorthWebFactory.BackendUrl, body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExternalReturnUrlIsRejectedByServer()
    {
        await using var factory = new FullWorthWebFactory();
        var user = await CreateUserAsync(factory);
        using var client = CreateClient(factory);

        var login = await LoginAsync(client, user.Email, Password, "https://evil.example/steal");

        Assert.Equal("/", login.ReturnUrl);
    }

    [Fact]
    public async Task WrongPasswordUnknownEmailAndLockoutUseSamePublicLoginFailure()
    {
        await using var factory = new FullWorthWebFactory();
        var user = await CreateUserAsync(factory);
        using var client = CreateClient(factory);

        using var wrong = await client.PostAsJsonAsync("/auth/login", new { email = user.Email, password = "wrong-password" });
        using var unknown = await client.PostAsJsonAsync("/auth/login", new { email = $"unknown-{Guid.NewGuid():N}@example.com", password = "wrong-password" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(await PublicErrorAsync(wrong), await PublicErrorAsync(unknown));

        for (var i = 1; i < AuthOptions.DefaultMaxFailedAccessAttempts; i++)
        {
            using var failed = await client.PostAsJsonAsync("/auth/login", new { email = user.Email, password = "wrong-password" });
            Assert.Equal(HttpStatusCode.Unauthorized, failed.StatusCode);
        }

        using var locked = await client.PostAsJsonAsync("/auth/login", new { email = user.Email, password = Password });
        Assert.Equal(HttpStatusCode.Unauthorized, locked.StatusCode);
        Assert.Equal("Invalid credentials.", await PublicErrorAsync(locked));
    }

    [Fact]
    public async Task PasswordLockoutDoesNotInvalidateAnActiveSession()
    {
        await using var factory = new FullWorthWebFactory();
        var user = await CreateUserAsync(factory);
        using var client = CreateClient(factory);
        var login = await LoginAsync(client, user.Email, Password);

        // An attacker who only knows the email locks the account via failed password attempts.
        for (var i = 0; i < AuthOptions.DefaultMaxFailedAccessAttempts; i++)
        {
            using var failed = await client.PostAsJsonAsync("/auth/login", new { email = user.Email, password = "wrong-password" });
            Assert.Equal(HttpStatusCode.Unauthorized, failed.StatusCode);
        }

        // The victim's existing session must stay valid: password lockout must not become a
        // session DoS. Before the fix, per-request validation rejected the cookie once locked.
        using var sessions = await SendAsync(client, HttpMethod.Get, "/auth/sessions", login.Cookie);
        Assert.Equal(HttpStatusCode.OK, sessions.StatusCode);
    }

    [Fact]
    public async Task RecoveryCodeRedemptionSignsInIsSingleUseAndEnumerationSafe()
    {
        await using var factory = new FullWorthWebFactory();
        var user = await CreateUserAsync(factory);
        using var client = CreateClient(factory);

        string code;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var recovery = scope.ServiceProvider.GetRequiredService<RecoveryService>();
            var set = await recovery.GenerateAsync(user.Id);
            code = set.Codes[0];
        }

        // Unknown email and wrong code both fail with the same uniform 401 (enumeration-safe).
        using var unknownEmail = await client.PostAsJsonAsync("/auth/recovery-code/redeem",
            new { email = $"nobody-{Guid.NewGuid():N}@example.com", recoveryCode = code });
        using var wrongCode = await client.PostAsJsonAsync("/auth/recovery-code/redeem",
            new { email = user.Email, recoveryCode = "AAAA-AAAA-AAAA-AAAA" });
        Assert.Equal(HttpStatusCode.Unauthorized, unknownEmail.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongCode.StatusCode);

        // A valid code signs the user in and returns a working session cookie.
        using var redeem = await client.PostAsJsonAsync("/auth/recovery-code/redeem",
            new { email = user.Email, recoveryCode = code });
        Assert.Equal(HttpStatusCode.OK, redeem.StatusCode);
        var setCookie = redeem.Headers.GetValues("Set-Cookie")
            .Single(x => x.StartsWith("Finance.Auth=", StringComparison.Ordinal));
        var cookie = setCookie.Split(';', 2)[0];
        using var afterRedeem = await SendAsync(client, HttpMethod.Get, "/auth/sessions", cookie);
        Assert.Equal(HttpStatusCode.OK, afterRedeem.StatusCode);

        // The code is single-use: redeeming it again fails.
        using var reuse = await client.PostAsJsonAsync("/auth/recovery-code/redeem",
            new { email = user.Email, recoveryCode = code });
        Assert.Equal(HttpStatusCode.Unauthorized, reuse.StatusCode);
    }

    [Fact]
    public async Task RevokeCurrentSessionMakesReplayedCookieInvalid()
    {
        await using var factory = new FullWorthWebFactory();
        var user = await CreateUserAsync(factory);
        using var client = CreateClient(factory);
        var login = await LoginAsync(client, user.Email, Password);
        var current = Assert.Single((await GetSessionsAsync(client, login.Cookie)).Sessions, x => x.Current);

        using var revoke = await SendAsync(client, HttpMethod.Delete, $"/auth/sessions/{current.Id}", login.Cookie);
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);

        await AssertCookieDeniedAsync(client, login.Cookie);
    }

    [Fact]
    public async Task RevokeOthersKeepsCurrentSessionAndRejectsOtherCookie()
    {
        await using var factory = new FullWorthWebFactory();
        var user = await CreateUserAsync(factory);
        using var client = CreateClient(factory);
        var sessionA = await LoginAsync(client, user.Email, Password);
        var sessionB = await LoginAsync(client, user.Email, Password);

        using var revoke = await SendAsync(client, HttpMethod.Post, "/auth/sessions/revoke-others", sessionA.Cookie);
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);

        using var a = await SendAsync(client, HttpMethod.Get, "/", sessionA.Cookie);
        Assert.Equal(HttpStatusCode.OK, a.StatusCode);
        await AssertCookieDeniedAsync(client, sessionB.Cookie);
    }

    [Fact]
    public async Task ForeignSessionIdReturnsNotFound()
    {
        await using var factory = new FullWorthWebFactory();
        var userA = await CreateUserAsync(factory);
        var userB = await CreateUserAsync(factory);
        using var client = CreateClient(factory);
        var loginA = await LoginAsync(client, userA.Email, Password);
        var loginB = await LoginAsync(client, userB.Email, Password);
        var sessionB = Assert.Single((await GetSessionsAsync(client, loginB.Cookie)).Sessions, x => x.Current);

        using var response = await SendAsync(client, HttpMethod.Delete, $"/auth/sessions/{sessionB.Id}", loginA.Cookie);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("/auth")]
    [InlineData("/auth/")]
    [InlineData("/auth/login")]
    [InlineData("/auth/register")]
    [InlineData("/auth/index.html")]
    public async Task AuthEntryRoutesRedirectAuthenticatedUsersToApp(string path)
    {
        await using var factory = new FullWorthWebFactory();
        var user = await CreateUserAsync(factory);
        using var client = CreateClient(factory);
        var login = await LoginAsync(client, user.Email, Password);

        using var response = await SendAsync(client, HttpMethod.Get, path, login.Cookie);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task ExternalAuthCapabilities_AreSafeWhenProvidersAreNotConfigured()
    {
        await using var factory = new FullWorthWebFactory();
        using var client = CreateClient(factory);

        using var response = await client.GetAsync("/auth/providers");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(json.RootElement.GetProperty("registrationEnabled").GetBoolean());
        Assert.False(json.RootElement.GetProperty("google").GetBoolean());
        Assert.False(json.RootElement.GetProperty("apple").GetBoolean());
    }

    [Fact]
    public async Task UnknownExternalProvider_ReturnsNotFound()
    {
        await using var factory = new FullWorthWebFactory();
        using var client = CreateClient(factory);

        using var response = await client.GetAsync("/auth/external/github");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task LogoutRevokesSessionAndReplayedCookieIsDenied()
    {
        await using var factory = new FullWorthWebFactory();
        var user = await CreateUserAsync(factory);
        using var client = CreateClient(factory);
        var login = await LoginAsync(client, user.Email, Password);

        using var logout = await SendAsync(client, HttpMethod.Post, "/auth/logout", login.Cookie);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        await AssertCookieDeniedAsync(client, login.Cookie);
    }

    [Fact]
    public async Task IdleTimeoutRejectsCookieWithoutWaitingRealTime()
    {
        await using var factory = new TimeFullWorthWebFactory();
        var user = await CreateUserAsync(factory);
        using var client = CreateClient(factory);
        var login = await LoginAsync(client, user.Email, Password);

        factory.Time.Advance(TimeSpan.FromMinutes(31));

        using var denied = await SendAsync(client, HttpMethod.Get, "/", login.Cookie);
        Assert.Equal(HttpStatusCode.Redirect, denied.StatusCode);
        Assert.StartsWith("/auth/login?", denied.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task ActivityCannotExtendSessionBeyondThirtyDayAbsoluteLifetime()
    {
        await using var factory = new TimeFullWorthWebFactory(longIdle: true);
        var user = await CreateUserAsync(factory);
        using var client = CreateClient(factory);
        var login = await LoginAsync(client, user.Email, Password);

        for (var i = 0; i < 3; i++)
        {
            factory.Time.Advance(TimeSpan.FromDays(9));
            using var active = await SendAsync(client, HttpMethod.Get, "/", login.Cookie);
            Assert.Equal(HttpStatusCode.OK, active.StatusCode);
        }

        factory.Time.Advance(TimeSpan.FromDays(4));
        await AssertCookieDeniedAsync(client, login.Cookie);
    }

    [Fact]
    public async Task PasswordChangeRevokesAllSessionsAndRequiresSignInAgain()
    {
        await using var factory = new FullWorthWebFactory();
        var user = await CreateUserAsync(factory);
        using var client = CreateClient(factory);
        var sessionA = await LoginAsync(client, user.Email, Password);
        var sessionB = await LoginAsync(client, user.Email, Password);

        using var change = await SendJsonAsync(client, HttpMethod.Post, "/auth/change-password", sessionA.Cookie,
            new { currentPassword = Password, newPassword = NewPassword });
        Assert.Equal(HttpStatusCode.NoContent, change.StatusCode);

        await AssertCookieDeniedAsync(client, sessionA.Cookie);
        await AssertCookieDeniedAsync(client, sessionB.Cookie);

        using var oldPassword = await client.PostAsJsonAsync("/auth/login", new { email = user.Email, password = Password });
        Assert.Equal(HttpStatusCode.Unauthorized, oldPassword.StatusCode);
        var newLogin = await LoginAsync(client, user.Email, NewPassword);
        Assert.False(string.IsNullOrWhiteSpace(newLogin.Cookie));
    }

    [Fact]
    public async Task PasswordResetRevokesAllSessionsAndTokenCannotBeReused()
    {
        await using var factory = new FullWorthWebFactory();
        var user = await CreateUserAsync(factory);
        using var client = CreateClient(factory);
        var login = await LoginAsync(client, user.Email, Password);
        string token;

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var auth = scope.ServiceProvider.GetRequiredService<AuthService>();
            token = (await auth.GeneratePasswordResetTokenAsync(user.Email))!.Token;
        }

        using var reset = await client.PostAsJsonAsync("/auth/password-reset/complete", new
        {
            email = user.Email,
            token,
            newPassword = NewPassword
        });
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);

        await AssertCookieDeniedAsync(client, login.Cookie);

        using var reuse = await client.PostAsJsonAsync("/auth/password-reset/complete", new
        {
            email = user.Email,
            token,
            newPassword = "third horse battery staple"
        });
        Assert.Equal(HttpStatusCode.BadRequest, reuse.StatusCode);

        using var oldPassword = await client.PostAsJsonAsync("/auth/login", new { email = user.Email, password = Password });
        Assert.Equal(HttpStatusCode.Unauthorized, oldPassword.StatusCode);
        var newLogin = await LoginAsync(client, user.Email, NewPassword);
        Assert.False(string.IsNullOrWhiteSpace(newLogin.Cookie));
    }


    [Fact]
    public async Task AccountDeletion_HasSevenDayRecoveryWindow_BlocksFinance_AndCanReactivate()
    {
        await using var factory = new FullWorthWebFactory();
        var user = await CreateUserAsync(factory);
        using var client = CreateClient(factory);
        var login = await LoginAsync(client, user.Email, Password);

        using (var wrong = await SendJsonAsync(
                   client,
                   HttpMethod.Post,
                   "/auth/account-deletion/request",
                   login.Cookie,
                   new { currentPassword = "wrong-password" }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        }

        var before = DateTimeOffset.UtcNow;
        using var request = await SendJsonAsync(
            client,
            HttpMethod.Post,
            "/auth/account-deletion/request",
            login.Cookie,
            new { currentPassword = Password });
        var after = DateTimeOffset.UtcNow;

        Assert.Equal(HttpStatusCode.OK, request.StatusCode);
        using (var payload = JsonDocument.Parse(await request.Content.ReadAsStringAsync()))
        {
            Assert.True(payload.RootElement.GetProperty("pending").GetBoolean());
            var scheduled = payload.RootElement.GetProperty("scheduledFor").GetDateTimeOffset();
            Assert.InRange(
                scheduled,
                before.AddDays(7).AddSeconds(-2),
                after.AddDays(7).AddSeconds(2));
        }

        using (var finance = await SendAsync(client, HttpMethod.Get, "/", login.Cookie))
        {
            Assert.Equal(HttpStatusCode.Redirect, finance.StatusCode);
            Assert.Equal("/account/deletion", finance.Headers.Location?.OriginalString);
        }

        using (var bff = await SendAsync(client, HttpMethod.Get, "/bff/backend/api/accounts", login.Cookie))
            Assert.Equal((HttpStatusCode)423, bff.StatusCode);

        using (var logout = await SendAsync(client, HttpMethod.Post, "/auth/logout", login.Cookie))
            Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        var recoveryLogin = await LoginAsync(client, user.Email, Password);
        Assert.Equal("/account/deletion", recoveryLogin.ReturnUrl);

        using var cancel = await SendAsync(
            client,
            HttpMethod.Post,
            "/auth/account-deletion/cancel",
            recoveryLogin.Cookie);
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);

        using var restored = await SendAsync(client, HttpMethod.Get, "/", recoveryLogin.Cookie);
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);

        Assert.Contains(factory.BackendRequests, request =>
            request.Method == "POST" &&
            request.Uri?.AbsolutePath.EndsWith("/api/bootstrap/deactivate-user", StringComparison.Ordinal) == true);
        Assert.Contains(factory.BackendRequests, request =>
            request.Method == "POST" &&
            request.Uri?.AbsolutePath.EndsWith("/api/bootstrap/reactivate-user", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task ProductionCookieIsAlwaysSecure()
    {
        await using var factory = new ProductionFullWorthWebFactory();
        var user = await CreateUserAsync(factory);
        using var client = CreateClient(factory);

        var login = await LoginAsync(client, user.Email, Password);

        Assert.Contains("; secure", login.SetCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", login.SetCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", login.SetCookie, StringComparison.OrdinalIgnoreCase);
    }

    private static HttpClient CreateClient(FullWorthWebFactory factory) => factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        HandleCookies = false
    });

    private static async Task<(Guid Id, string Email)> CreateUserAsync(FullWorthWebFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var auth = scope.ServiceProvider.GetRequiredService<AuthService>();
        var email = $"integration-{Guid.NewGuid():N}@example.com";
        var created = await auth.CreateUserAsync(new CreateAuthUserRequest(Guid.NewGuid(), email, Password));
        Assert.True(created.Succeeded, string.Join("; ", created.Errors));
        return (created.User!.Id, email);
    }

    private static async Task<LoginSession> LoginAsync(HttpClient client, string email, string password, string? returnUrl = null)
    {
        var path = "/auth/login";
        if (!string.IsNullOrWhiteSpace(returnUrl))
            path += $"?returnUrl={Uri.EscapeDataString(returnUrl)}";

        using var response = await client.PostAsJsonAsync(path, new { email, password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // Match both the dev name (Finance.Auth) and the Production __Host- prefixed name.
        var setCookie = response.Headers.GetValues("Set-Cookie")
            .Single(x => x.Contains("Finance.Auth=", StringComparison.Ordinal));
        var cookie = setCookie.Split(';', 2)[0];
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var safeReturnUrl = json.RootElement.GetProperty("returnUrl").GetString()!;
        return new LoginSession(cookie, setCookie, safeReturnUrl);
    }

    private static async Task<SessionListDto> GetSessionsAsync(HttpClient client, string cookie)
    {
        using var response = await SendAsync(client, HttpMethod.Get, "/auth/sessions/", cookie);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<SessionListDto>())!;
    }

    private static async Task AssertCookieDeniedAsync(HttpClient client, string cookie)
    {
        using var response = await SendAsync(client, HttpMethod.Get, "/", cookie);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("status=session-expired", response.Headers.Location?.OriginalString ?? string.Empty, StringComparison.Ordinal);
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, string cookie)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Cookie", cookie);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> SendJsonAsync(HttpClient client, HttpMethod method, string path, string cookie, object body)
    {
        var request = new HttpRequestMessage(method, path)
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Add("Cookie", cookie);
        return await client.SendAsync(request);
    }

    private static async Task<string?> PublicErrorAsync(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("error").GetString();
    }

    private sealed record LoginSession(string Cookie, string SetCookie, string ReturnUrl);

    private sealed class MutableTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = new(2026, 8, 12, 20, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void Advance(TimeSpan amount) => _utcNow = _utcNow.Add(amount);
    }

    private sealed class TimeFullWorthWebFactory(bool longIdle = false) : FullWorthWebFactory
    {
        public MutableTimeProvider Time { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                // Pin the session windows explicitly so these time-travel tests do not depend on the
                // production appsettings default (a multi-day idle for "stay logged in"). The default
                // factory uses a short 30-minute idle; longIdle exercises the 30-day absolute cap.
                services.RemoveAll<IOptions<SessionOptions>>();
                services.AddSingleton<IOptions<SessionOptions>>(Options.Create(new SessionOptions
                {
                    IdleTimeout = longIdle ? TimeSpan.FromDays(10) : TimeSpan.FromMinutes(30),
                    AbsoluteLifetime = TimeSpan.FromDays(30),
                    TouchInterval = longIdle ? TimeSpan.FromDays(1) : TimeSpan.FromMinutes(5),
                    CleanupRetention = TimeSpan.FromDays(30)
                }));
                services.PostConfigure<CookieAuthenticationOptions>(IdentityConstants.ApplicationScheme, options =>
                {
                    options.ExpireTimeSpan = longIdle ? TimeSpan.FromDays(40) : TimeSpan.FromMinutes(30);
                    options.SlidingExpiration = !longIdle;
                });

                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(Time);
            });
        }
    }

    private sealed class ProductionFullWorthWebFactory : FullWorthWebFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseEnvironment("Production");
            // Production requires real secrets + a pinned host allow-list (P0.3/P1.2a). The base factory
            // already supplies strong dummy secrets + the test DB; pin a non-wildcard host so the
            // production host-filtering guard is satisfied with a dummy value.
            // Use the TestServer's own host so host-filtering admits the in-memory client while still
            // exercising a non-wildcard AllowedHosts (the production guard rejects '*').
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["AllowedHosts"] = "localhost" }));
        }
    }
}
