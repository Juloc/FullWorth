using System.Net;
using System.Net.Http.Json;
using FullWorth.Web.Modules.Auth;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Web.Tests.Admin;

public sealed class InstanceAdminAccessTests
{
    private const string TestPassword = "correct horse battery staple";

    [Fact]
    public async Task NormalUserCannotOpenAdminShellApiOrAssets()
    {
        await using var factory = new FullWorthWebFactory();
        var user = await CreateUserAsync(factory);
        using var client = CreateClient(factory);
        var cookie = await LoginAsync(client, user.Email);

        using var shell = await SendAsync(client, HttpMethod.Get, "/admin", cookie);
        using var api = await SendAsync(client, HttpMethod.Get, "/auth/admin/users", cookie);
        using var asset = await SendAsync(client, HttpMethod.Get, "/admin/admin.js", cookie);

        Assert.Equal(HttpStatusCode.Forbidden, shell.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, api.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, asset.StatusCode);
    }

    [Fact]
    public async Task AdminUserListContainsNoFinanceData()
    {
        await using var factory = new FullWorthWebFactory();
        var admin = await CreateUserAsync(factory);
        var normal = await CreateUserAsync(factory);
        await SetAdminAsync(factory, admin.Id);

        using var client = CreateClient(factory);
        var cookie = await LoginAsync(client, admin.Email);

        using var shell = await SendAsync(client, HttpMethod.Get, "/admin", cookie);
        Assert.Equal(HttpStatusCode.OK, shell.StatusCode);

        using var response = await SendAsync(client, HttpMethod.Get, "/auth/admin/users?limit=50", cookie);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();

        Assert.Contains(admin.Email, json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(normal.Email, json, StringComparison.OrdinalIgnoreCase);

        foreach (var forbidden in new[]
                 {
                     "financeUserId", "fullWorthSpace", "spaceCount", "accountId",
                     "accounts", "balance", "transaction", "iban", "receipt",
                     "purchase", "contract", "bankConnection"
                 })
            Assert.DoesNotContain(forbidden, json, StringComparison.OrdinalIgnoreCase);
    }

    private static HttpClient CreateClient(FullWorthWebFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false
        });

    private static async Task<(Guid Id, string Email)> CreateUserAsync(FullWorthWebFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var auth = scope.ServiceProvider.GetRequiredService<AuthService>();
        var email = $"admin-test-{Guid.NewGuid():N}@example.com";
        var created = await auth.CreateUserAsync(new CreateAuthUserRequest(Guid.NewGuid(), email, TestPassword));
        Assert.True(created.Succeeded);
        return (created.User!.Id, email);
    }

    private static async Task SetAdminAsync(FullWorthWebFactory factory, Guid id)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AuthUser>>();
        var user = await users.FindByIdAsync(id.ToString());
        Assert.NotNull(user);
        user!.IsAdmin = true;
        Assert.True((await users.UpdateAsync(user)).Succeeded);
    }

    private static async Task<string> LoginAsync(HttpClient client, string email)
    {
        using var response = await client.PostAsJsonAsync("/auth/login", new { email, password = TestPassword });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return response.Headers.GetValues("Set-Cookie")
            .Single(x => x.Contains("Finance.Auth=", StringComparison.Ordinal))
            .Split(';', 2)[0];
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpMethod method, string path, string cookie)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Cookie", cookie);
        return await client.SendAsync(request);
    }
}
