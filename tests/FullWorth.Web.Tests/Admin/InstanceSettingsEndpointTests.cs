using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FullWorth.Web.Modules.Auth;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Web.Tests.Admin;

/// <summary>
/// Saving a setting the way the browser actually saves it — over HTTP, as a signed-in administrator.
///
/// The existing tests for this feature all call <c>InstanceConfigurationService</c> directly. That
/// proved the store, the configuration source and the precedence, and it proved none of the things
/// between the form and the service: the route, the authorisation, the status code, the response
/// shape. So a setting could round-trip perfectly in every test and still be impossible to save from
/// the screen, which is exactly what happened — the form sent a JSON body without a JSON content
/// type, ASP.NET answered 415, and the field simply came back empty.
/// </summary>
public sealed class InstanceSettingsEndpointTests
{
    private const string TestPassword = "correct horse battery staple";
    private const string Endpoint = "/auth/admin/instance-settings";

    /// <summary>
    /// The whole round trip: type it, save it, and it is still there when the page reloads.
    /// </summary>
    [Fact]
    public async Task A_setting_saved_over_http_comes_back_on_the_next_load()
    {
        await using var factory = new FullWorthWebFactory();
        var admin = await CreateAdminAsync(factory);
        using var client = CreateClient(factory);
        var cookie = await LoginAsync(client, admin.Email);

        using var saved = await SendJsonAsync(
            client, HttpMethod.Put, Endpoint, cookie, new { key = "FinTs:ProductId", value = "PRODUCT-42" });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        using var reloaded = await SendAsync(client, HttpMethod.Get, Endpoint, cookie);
        Assert.Equal(HttpStatusCode.OK, reloaded.StatusCode);

        Assert.Equal("PRODUCT-42", ValueOf(await reloaded.Content.ReadAsStringAsync(), "FinTs:ProductId"));
    }

    /// <summary>
    /// The response to the save is the list again, so the form can re-render the source column
    /// straight away. If it were 204 the panel would render nothing and look like it had lost the
    /// value it had just stored.
    /// </summary>
    [Fact]
    public async Task Saving_answers_with_the_whole_list()
    {
        await using var factory = new FullWorthWebFactory();
        var admin = await CreateAdminAsync(factory);
        using var client = CreateClient(factory);
        var cookie = await LoginAsync(client, admin.Email);

        using var response = await SendJsonAsync(
            client, HttpMethod.Put, Endpoint, cookie, new { key = "FinTs:ProductId", value = "PRODUCT-7" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("PRODUCT-7", ValueOf(await response.Content.ReadAsStringAsync(), "FinTs:ProductId"));
    }

    /// <summary>
    /// The regression, spelled out: a JSON body sent as text/plain — which is what fetch() does when
    /// nobody sets the header — is refused by the framework before any of this code runs. The form
    /// did exactly that, and the only symptom was a field that emptied itself.
    /// </summary>
    [Fact]
    public async Task A_body_without_a_json_content_type_is_refused_by_the_framework()
    {
        await using var factory = new FullWorthWebFactory();
        var admin = await CreateAdminAsync(factory);
        using var client = CreateClient(factory);
        var cookie = await LoginAsync(client, admin.Email);

        using var request = new HttpRequestMessage(HttpMethod.Put, Endpoint)
        {
            Content = new StringContent(
                """{"key":"FinTs:ProductId","value":"PRODUCT-9"}""", Encoding.UTF8, "text/plain")
        };
        request.Headers.Add("Cookie", cookie);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    [Fact]
    public async Task A_normal_user_cannot_save_a_setting()
    {
        await using var factory = new FullWorthWebFactory();
        var user = await CreateUserAsync(factory);
        using var client = CreateClient(factory);
        var cookie = await LoginAsync(client, user.Email);

        using var response = await SendJsonAsync(
            client, HttpMethod.Put, Endpoint, cookie, new { key = "FinTs:ProductId", value = "NOPE" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>A value the descriptor refuses comes back as a reason, not as a 500.</summary>
    [Fact]
    public async Task An_impossible_value_is_refused_with_a_reason()
    {
        await using var factory = new FullWorthWebFactory();
        var admin = await CreateAdminAsync(factory);
        using var client = CreateClient(factory);
        var cookie = await LoginAsync(client, admin.Email);

        using var response = await SendJsonAsync(
            client, HttpMethod.Put, Endpoint, cookie,
            new { key = "Sync:IntervalMinutes", value = "not-a-number" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(
            "setting_not_a_number", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    /// <summary>Clearing hands the key back to the default, over HTTP as well as in the service.</summary>
    [Fact]
    public async Task Clearing_a_setting_over_http_removes_it()
    {
        await using var factory = new FullWorthWebFactory();
        var admin = await CreateAdminAsync(factory);
        using var client = CreateClient(factory);
        var cookie = await LoginAsync(client, admin.Email);

        using (var saved = await SendJsonAsync(
            client, HttpMethod.Put, Endpoint, cookie, new { key = "FinTs:ProductId", value = "PRODUCT-42" }))
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        using var cleared = await SendJsonAsync(
            client, HttpMethod.Put, Endpoint, cookie, new { key = "FinTs:ProductId", value = "" });

        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        Assert.True(string.IsNullOrEmpty(
            ValueOf(await cleared.Content.ReadAsStringAsync(), "FinTs:ProductId")));
    }

    private static string? ValueOf(string json, string key)
    {
        using var document = JsonDocument.Parse(json);
        var entry = document.RootElement.EnumerateArray()
            .Single(x => x.GetProperty("key").GetString() == key);
        return entry.GetProperty("value").GetString();
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
        var email = $"settings-test-{Guid.NewGuid():N}@example.com";
        var created = await auth.CreateUserAsync(new CreateAuthUserRequest(Guid.NewGuid(), email, TestPassword));
        Assert.True(created.Succeeded);
        return (created.User!.Id, email);
    }

    private static async Task<(Guid Id, string Email)> CreateAdminAsync(FullWorthWebFactory factory)
    {
        var user = await CreateUserAsync(factory);
        await using var scope = factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AuthUser>>();
        var found = await users.FindByIdAsync(user.Id.ToString());
        found!.IsAdmin = true;
        Assert.True((await users.UpdateAsync(found)).Succeeded);
        return user;
    }

    private static async Task<string> LoginAsync(HttpClient client, string email)
    {
        using var response = await client.PostAsJsonAsync("/auth/login", new { email, password = TestPassword });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return response.Headers.GetValues("Set-Cookie")
            .Single(x => x.Contains("Finance.Auth=", StringComparison.Ordinal))
            .Split(';', 2)[0];
    }

    private static Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpMethod method, string path, string cookie)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Cookie", cookie);
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> SendJsonAsync(
        HttpClient client, HttpMethod method, string path, string cookie, object body)
    {
        var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("Cookie", cookie);
        return client.SendAsync(request);
    }
}
