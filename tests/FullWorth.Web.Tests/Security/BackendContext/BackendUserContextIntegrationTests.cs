using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FullWorth.Web.Modules.Auth;
using FullWorth.Web.Modules.Sessions;
using FullWorth.Web.Security.BackendContext;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Web.Tests.Security.BackendContext;

public sealed class BackendUserContextIntegrationTests
{
    private const string Password = "correct horse battery staple";

    [Fact]
    public async Task BffForwardsFinanceUserIdNotAuthUserIdAndOverridesSpoofedHeaders()
    {
        await using var factory = new FullWorthWebFactory();
        var financeUserId = Guid.NewGuid();
        var user = await CreateUserAsync(factory, financeUserId);
        Assert.NotEqual(user.AuthUserId, user.FinanceUserId);
        using var client = CreateClient(factory);
        var cookie = await LoginAsync(client, user.Email);
        factory.ClearProxyRequests();

        var victimFinanceUserId = Guid.NewGuid();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/bff/backend/api/accounts");
        request.Headers.Add("Cookie", cookie);
        request.Headers.TryAddWithoutValidation(BackendContextHeaders.UserId, victimFinanceUserId.ToString("D"));
        request.Headers.TryAddWithoutValidation(BackendContextHeaders.InternalKey, "attacker-internal-key");
        request.Headers.TryAddWithoutValidation(BackendContextHeaders.LegacyApiKey, "attacker-master-key");
        request.Headers.TryAddWithoutValidation(BackendContextHeaders.LegacyReadKey, "attacker-read-key");
        request.Headers.TryAddWithoutValidation(BackendContextHeaders.IngestKey, "attacker-ingest-key");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "browser-token");

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var backend = Assert.Single(factory.BackendRequests);
        Assert.Equal(financeUserId.ToString("D"), SingleHeader(backend, BackendContextHeaders.UserId));
        Assert.NotEqual(user.AuthUserId.ToString("D"), SingleHeader(backend, BackendContextHeaders.UserId));
        Assert.Equal(FullWorthWebFactory.BackendInternalKey, SingleHeader(backend, BackendContextHeaders.InternalKey));
        Assert.False(backend.Headers.ContainsKey(BackendContextHeaders.LegacyApiKey));
        Assert.False(backend.Headers.ContainsKey(BackendContextHeaders.LegacyReadKey));
        Assert.False(backend.Headers.ContainsKey(BackendContextHeaders.IngestKey));
        Assert.False(backend.Headers.ContainsKey("Authorization"));
        Assert.False(backend.Headers.ContainsKey("Cookie"));
    }

    [Fact]
    public async Task AnonymousBffRequestIsDeniedWithoutBackendCall()
    {
        await using var factory = new FullWorthWebFactory();
        using var client = CreateClient(factory);
        factory.ClearProxyRequests();

        using var response = await client.GetAsync("/bff/backend/api/accounts");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(factory.BackendRequests);
    }

    [Fact]
    public async Task MissingFullWorthUserLinkFailsClosedWithoutBackendCall()
    {
        await using var factory = new FullWorthWebFactory();
        var user = await CreateUserAsync(factory, Guid.NewGuid());
        using var client = CreateClient(factory);
        var cookie = await LoginAsync(client, user.Email);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AuthUser>>();
            var authUser = await users.FindByIdAsync(user.AuthUserId.ToString());
            Assert.NotNull(authUser);
            authUser.FinanceUserId = Guid.Empty;
            Assert.True((await users.UpdateAsync(authUser)).Succeeded);
        }

        factory.ClearProxyRequests();
        using var response = await SendWithCookieAsync(client, "/bff/backend/api/accounts", cookie);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(factory.BackendRequests);
    }

    [Fact]
    public async Task DisabledAuthUserCannotObtainBackendContext()
    {
        await using var factory = new FullWorthWebFactory();
        var user = await CreateUserAsync(factory, Guid.NewGuid());
        using var client = CreateClient(factory);
        var cookie = await LoginAsync(client, user.Email);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AuthUser>>();
            var authUser = await users.FindByIdAsync(user.AuthUserId.ToString());
            Assert.NotNull(authUser);
            authUser.IsDisabled = true;
            Assert.True((await users.UpdateAsync(authUser)).Succeeded);
        }

        factory.ClearProxyRequests();
        using var response = await SendWithCookieAsync(client, "/bff/backend/api/accounts", cookie);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(factory.BackendRequests);
    }

    [Fact]
    public async Task RevokedSessionCannotObtainBackendContext()
    {
        await using var factory = new FullWorthWebFactory();
        var user = await CreateUserAsync(factory, Guid.NewGuid());
        using var client = CreateClient(factory);
        var cookie = await LoginAsync(client, user.Email);

        using var sessionsResponse = await SendWithCookieAsync(client, "/auth/sessions/", cookie);
        Assert.Equal(HttpStatusCode.OK, sessionsResponse.StatusCode);
        var sessions = (await sessionsResponse.Content.ReadFromJsonAsync<SessionListDto>())!;
        var current = Assert.Single(sessions.Sessions, session => session.Current);

        using (var revoke = new HttpRequestMessage(HttpMethod.Delete, $"/auth/sessions/{current.Id}"))
        {
            revoke.Headers.Add("Cookie", cookie);
            using var revokeResponse = await client.SendAsync(revoke);
            Assert.Equal(HttpStatusCode.NoContent, revokeResponse.StatusCode);
        }

        factory.ClearProxyRequests();
        using var response = await SendWithCookieAsync(client, "/bff/backend/api/accounts", cookie);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(factory.BackendRequests);
    }

    private static HttpClient CreateClient(FullWorthWebFactory factory) => factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        HandleCookies = false
    });

    private static async Task<TestUser> CreateUserAsync(FullWorthWebFactory factory, Guid financeUserId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var auth = scope.ServiceProvider.GetRequiredService<AuthService>();
        var email = $"backend-context-{Guid.NewGuid():N}@example.com";
        var created = await auth.CreateUserAsync(new CreateAuthUserRequest(financeUserId, email, Password));
        Assert.True(created.Succeeded, string.Join("; ", created.Errors));
        return new TestUser(created.User!.Id, created.User.FinanceUserId, email);
    }

    private static async Task<string> LoginAsync(HttpClient client, string email)
    {
        using var response = await client.PostAsJsonAsync("/auth/login", new { email, password = Password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var setCookie = response.Headers.GetValues("Set-Cookie")
            .Single(value => value.StartsWith("Finance.Auth=", StringComparison.Ordinal));
        return setCookie.Split(';', 2)[0];
    }

    private static async Task<HttpResponseMessage> SendWithCookieAsync(HttpClient client, string path, string cookie)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Cookie", cookie);
        return await client.SendAsync(request);
    }

    private static string SingleHeader(FullWorthWebFactory.RecordedProxyRequest request, string header)
    {
        Assert.True(request.Headers.TryGetValue(header, out var values));
        return Assert.Single(values!);
    }

    private sealed record TestUser(Guid AuthUserId, Guid FinanceUserId, string Email);
}
