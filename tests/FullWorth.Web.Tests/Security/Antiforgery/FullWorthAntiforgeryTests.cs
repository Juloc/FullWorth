using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FullWorth.Web.Security.Antiforgery;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace FullWorth.Web.Tests.Security.Antiforgery;

public sealed class FullWorthAntiforgeryTests
{
    [Fact]
    public async Task SafeGetWithoutTokenSucceeds()
    {
        await using var host = await AntiforgeryTestApplication.StartAsync();

        using var response = await host.Client.GetAsync("/auth/probe");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task UnsafeMethodsWithoutTokenFail(string method)
    {
        await using var host = await AntiforgeryTestApplication.StartAsync();
        using var request = new HttpRequestMessage(new HttpMethod(method), "/auth/probe");

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task UnsafePostWithValidTokenSucceeds()
    {
        await using var host = await AntiforgeryTestApplication.StartAsync();
        var pair = await GetTokenPairAsync(host.Client);
        using var request = CreateRequest(HttpMethod.Post, "/auth/probe", pair: pair);

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task InvalidTokenFails()
    {
        await using var host = await AntiforgeryTestApplication.StartAsync();
        var pair = await GetTokenPairAsync(host.Client);
        using var request = CreateRequest(
            HttpMethod.Post,
            "/auth/probe",
            antiforgeryCookie: pair.Cookie,
            requestToken: "invalid-token");

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task TokenFromWrongCookiePairFails()
    {
        await using var host = await AntiforgeryTestApplication.StartAsync();
        var first = await GetTokenPairAsync(host.Client);
        var second = await GetTokenPairAsync(host.Client);
        using var request = CreateRequest(
            HttpMethod.Post,
            "/auth/probe",
            antiforgeryCookie: second.Cookie,
            requestToken: first.Token);

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task TokenEndpointReturnsRequestTokenAndFrameworkCookie()
    {
        await using var host = await AntiforgeryTestApplication.StartAsync();

        using var response = await host.Client.GetAsync(FullWorthAntiforgeryDefaults.TokenEndpointPath);
        var payload = await response.Content.ReadFromJsonAsync<FullWorthAntiforgeryTokenResponse>();
        var cookie = GetSetCookie(response, FullWorthAntiforgeryDefaults.CookieName);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(payload);
        Assert.False(string.IsNullOrWhiteSpace(payload.Token));
        Assert.StartsWith(FullWorthAntiforgeryDefaults.CookieName + "=", cookie, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TokenEndpointDoesNotExposeAuthenticationCookieValue()
    {
        await using var host = await AntiforgeryTestApplication.StartAsync();
        var authCookie = await SignInAsync(host.Client);
        using var request = new HttpRequestMessage(HttpMethod.Get, FullWorthAntiforgeryDefaults.TokenEndpointPath);
        AddCookies(request, authCookie);

        using var response = await host.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(GetCookieValue(authCookie), body, StringComparison.Ordinal);
        Assert.DoesNotContain(AntiforgeryTestApplication.AuthCookieName, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TokenEndpointDoesNotExposeBackendOrBankingSecrets()
    {
        await using var host = await AntiforgeryTestApplication.StartAsync();

        using var response = await host.Client.GetAsync(FullWorthAntiforgeryDefaults.TokenEndpointPath);
        var body = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain(AntiforgeryTestApplication.BackendSecret, body, StringComparison.Ordinal);
        Assert.DoesNotContain(AntiforgeryTestApplication.BankingSecret, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnonymousLoginPostRequiresAndAcceptsAntiforgeryToken()
    {
        await using var host = await AntiforgeryTestApplication.StartAsync();

        using var missing = await host.Client.PostAsync("/auth/login", content: null);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);

        var pair = await GetTokenPairAsync(host.Client);
        using var validRequest = CreateRequest(HttpMethod.Post, "/auth/login", pair: pair);
        using var valid = await host.Client.SendAsync(validRequest);
        Assert.Equal(HttpStatusCode.NoContent, valid.StatusCode);
    }

    [Theory]
    [InlineData("/auth/password-reset/request")]
    [InlineData("/auth/password-reset/complete")]
    public async Task AnonymousPasswordResetPostsRequireAntiforgeryToken(string path)
    {
        await using var host = await AntiforgeryTestApplication.StartAsync();

        using var response = await host.Client.PostAsync(path, content: null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task LogoutRequiresToken()
    {
        await using var host = await AntiforgeryTestApplication.StartAsync();
        var authCookie = await SignInAsync(host.Client);
        using var request = CreateRequest(HttpMethod.Post, "/auth/logout", authCookie: authCookie);

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task LogoutWithValidTokenSucceeds()
    {
        await using var host = await AntiforgeryTestApplication.StartAsync();
        var authCookie = await SignInAsync(host.Client);
        var pair = await GetTokenPairAsync(host.Client, authCookie);
        using var request = CreateRequest(HttpMethod.Post, "/auth/logout", authCookie, pair);

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Theory]
    [InlineData("DELETE", "/auth/sessions/3a58ec22-7d0d-4b0a-98a6-c7bd00f4f28c")]
    [InlineData("POST", "/auth/sessions/revoke-others")]
    public async Task SessionRevocationRequiresToken(string method, string path)
    {
        await using var host = await AntiforgeryTestApplication.StartAsync();
        var authCookie = await SignInAsync(host.Client);
        using var request = CreateRequest(new HttpMethod(method), path, authCookie: authCookie);

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PasswordChangeRequiresToken()
    {
        await using var host = await AntiforgeryTestApplication.StartAsync();
        var authCookie = await SignInAsync(host.Client);
        using var request = CreateRequest(HttpMethod.Post, "/auth/change-password", authCookie: authCookie);

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task RecoveryRegenerationRequiresToken()
    {
        await using var host = await AntiforgeryTestApplication.StartAsync();
        var authCookie = await SignInAsync(host.Client);
        using var request = CreateRequest(
            HttpMethod.Post,
            "/auth/recovery-codes/regenerate",
            authCookie: authCookie);

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AuthenticatedBffUnsafePostRequiresToken()
    {
        await using var host = await AntiforgeryTestApplication.StartAsync();
        var authCookie = await SignInAsync(host.Client);
        using var request = CreateRequest(HttpMethod.Post, "/bff/backend/probe", authCookie: authCookie);

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task BankingBffUnsafePostRequiresToken()
    {
        await using var host = await AntiforgeryTestApplication.StartAsync();
        var authCookie = await SignInAsync(host.Client);
        using var request = CreateRequest(HttpMethod.Post, "/bff/banking/probe", authCookie: authCookie);

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task BffGetDoesNotRequireToken()
    {
        await using var host = await AntiforgeryTestApplication.StartAsync();
        var authCookie = await SignInAsync(host.Client);
        using var request = CreateRequest(HttpMethod.Get, "/bff/backend/probe", authCookie: authCookie);

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task HealthDoesNotRequireToken()
    {
        await using var host = await AntiforgeryTestApplication.StartAsync();

        using var response = await host.Client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task StaticAuthAssetDoesNotRequireToken()
    {
        await using var host = await AntiforgeryTestApplication.StartAsync();

        using var response = await host.Client.GetAsync("/auth/app.js");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task TokenIsReturnedInBodyNotUrl()
    {
        await using var host = await AntiforgeryTestApplication.StartAsync();

        using var response = await host.Client.GetAsync(FullWorthAntiforgeryDefaults.TokenEndpointPath);
        var payload = await response.Content.ReadFromJsonAsync<FullWorthAntiforgeryTokenResponse>();

        Assert.NotNull(payload);
        Assert.Equal(string.Empty, response.RequestMessage?.RequestUri?.Query);
        Assert.DoesNotContain(payload.Token, response.RequestMessage?.RequestUri?.ToString() ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailureResponseContainsNoTokenCookieOrServiceSecret()
    {
        await using var host = await AntiforgeryTestApplication.StartAsync();
        var pair = await GetTokenPairAsync(host.Client);
        using var request = CreateRequest(
            HttpMethod.Post,
            "/auth/probe",
            antiforgeryCookie: pair.Cookie,
            requestToken: "invalid-token");

        using var response = await host.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            FullWorthAntiforgeryDefaults.InvalidTokenMessage,
            json.RootElement.GetProperty("error").GetString());
        Assert.DoesNotContain(pair.Token, body, StringComparison.Ordinal);
        Assert.DoesNotContain(GetCookieValue(pair.Cookie), body, StringComparison.Ordinal);
        Assert.DoesNotContain(AntiforgeryTestApplication.BackendSecret, body, StringComparison.Ordinal);
        Assert.DoesNotContain(AntiforgeryTestApplication.BankingSecret, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task JsonRequestWithHeaderTokenWorks()
    {
        await using var host = await AntiforgeryTestApplication.StartAsync();
        var authCookie = await SignInAsync(host.Client);
        var pair = await GetTokenPairAsync(host.Client, authCookie);
        using var content = JsonContent.Create(new AntiforgeryTestApplication.ProbePayload("accepted"));
        using var request = CreateRequest(
            HttpMethod.Post,
            "/bff/backend/json",
            authCookie,
            pair,
            content: content);

        using var response = await host.Client.SendAsync(request);
        var payload = await response.Content.ReadFromJsonAsync<AntiforgeryTestApplication.ProbePayload>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("accepted", payload?.Value);
    }

    [Fact]
    public async Task MultipartRequestWithHeaderTokenWorks()
    {
        await using var host = await AntiforgeryTestApplication.StartAsync();
        var authCookie = await SignInAsync(host.Client);
        var pair = await GetTokenPairAsync(host.Client, authCookie);
        using var content = new MultipartFormDataContent
        {
            { new StringContent("receipt"), "kind" },
            { new ByteArrayContent([1, 2, 3]), "file", "receipt.bin" }
        };
        using var request = CreateRequest(
            HttpMethod.Post,
            "/bff/backend/upload",
            authCookie,
            pair,
            content: content);

        using var response = await host.Client.SendAsync(request);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, json.RootElement.GetProperty("fields").GetInt32());
        Assert.Equal(1, json.RootElement.GetProperty("files").GetInt32());
    }

    [Fact]
    public async Task TokenEndpointIsNonCacheable()
    {
        await using var host = await AntiforgeryTestApplication.StartAsync();

        using var response = await host.Client.GetAsync(FullWorthAntiforgeryDefaults.TokenEndpointPath);
        var cacheControl = response.Headers.CacheControl;

        Assert.NotNull(cacheControl);
        Assert.True(cacheControl.NoStore);
        Assert.True(cacheControl.Private);
        Assert.Contains("no-cache", response.Headers.Pragma.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("POST", "/auth/passkeys/register/begin", true)]
    [InlineData("POST", "/auth/passkeys/register/complete", true)]
    [InlineData("DELETE", "/auth/passkeys/credential-1", true)]
    [InlineData("POST", "/auth/passkeys/login/begin", false)]
    [InlineData("POST", "/auth/passkeys/login/complete", false)]
    public async Task PasskeyBrowserMutationsAreCompatibleWithSamePolicy(
        string method,
        string path,
        bool authenticated)
    {
        await using var host = await AntiforgeryTestApplication.StartAsync();
        var authCookie = authenticated ? await SignInAsync(host.Client) : null;
        using var request = CreateRequest(new HttpMethod(method), path, authCookie: authCookie);

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AnonymousTokenShouldBeRefetchedAfterLogin()
    {
        await using var host = await AntiforgeryTestApplication.StartAsync();
        var anonymousPair = await GetTokenPairAsync(host.Client);
        var authCookie = await SignInAsync(host.Client);
        using var staleRequest = CreateRequest(HttpMethod.Post, "/auth/logout", authCookie, anonymousPair);

        using var staleResponse = await host.Client.SendAsync(staleRequest);

        Assert.Equal(HttpStatusCode.BadRequest, staleResponse.StatusCode);

        var authenticatedPair = await GetTokenPairAsync(host.Client, authCookie);
        using var freshRequest = CreateRequest(HttpMethod.Post, "/auth/logout", authCookie, authenticatedPair);
        using var freshResponse = await host.Client.SendAsync(freshRequest);
        Assert.Equal(HttpStatusCode.NoContent, freshResponse.StatusCode);
    }

    [Fact]
    public void ProductionCookieContractUsesHttpOnlySecureSameSiteLaxCookie()
    {
        var services = new ServiceCollection();
        services.AddFullWorthAntiforgery(secureCookie: true);
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<AntiforgeryOptions>>().Value;

        Assert.Equal(FullWorthAntiforgeryDefaults.HeaderName, options.HeaderName);
        Assert.Equal(FullWorthAntiforgeryDefaults.CookieName, options.Cookie.Name);
        Assert.True(options.Cookie.HttpOnly);
        Assert.True(options.Cookie.IsEssential);
        Assert.Equal(SameSiteMode.Lax, options.Cookie.SameSite);
        Assert.Equal("/", options.Cookie.Path);
        Assert.Equal(CookieSecurePolicy.Always, options.Cookie.SecurePolicy);
    }

    private static async Task<string> SignInAsync(HttpClient client)
    {
        using var response = await client.PostAsync("/test/sign-in", content: null);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        return GetSetCookie(response, AntiforgeryTestApplication.AuthCookieName);
    }

    private static async Task<TokenPair> GetTokenPairAsync(HttpClient client, string? authCookie = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, FullWorthAntiforgeryDefaults.TokenEndpointPath);
        AddCookies(request, authCookie);
        using var response = await client.SendAsync(request);
        var payload = await response.Content.ReadFromJsonAsync<FullWorthAntiforgeryTokenResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(payload);
        Assert.False(string.IsNullOrWhiteSpace(payload.Token));

        return new TokenPair(
            GetSetCookie(response, FullWorthAntiforgeryDefaults.CookieName),
            payload.Token);
    }

    private static HttpRequestMessage CreateRequest(
        HttpMethod method,
        string path,
        string? authCookie = null,
        TokenPair? pair = null,
        HttpContent? content = null,
        string? antiforgeryCookie = null,
        string? requestToken = null)
    {
        var request = new HttpRequestMessage(method, path)
        {
            Content = content
        };

        AddCookies(request, authCookie, pair?.Cookie, antiforgeryCookie);

        var token = requestToken ?? pair?.Token;
        if (!string.IsNullOrWhiteSpace(token))
            request.Headers.TryAddWithoutValidation(FullWorthAntiforgeryDefaults.HeaderName, token);

        return request;
    }

    private static void AddCookies(HttpRequestMessage request, params string?[] cookies)
    {
        var values = cookies.Where(static cookie => !string.IsNullOrWhiteSpace(cookie)).ToArray();
        if (values.Length > 0)
            request.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", values));
    }

    private static string GetSetCookie(HttpResponseMessage response, string cookieName)
    {
        Assert.True(response.Headers.TryGetValues("Set-Cookie", out var values));
        var prefix = cookieName + "=";
        var header = values.Single(value => value.StartsWith(prefix, StringComparison.Ordinal));
        return header.Split(';', 2)[0];
    }

    private static string GetCookieValue(string cookie)
    {
        var separator = cookie.IndexOf('=');
        Assert.True(separator > 0 && separator < cookie.Length - 1);
        return cookie[(separator + 1)..];
    }

    private sealed record TokenPair(string Cookie, string Token);
}
