using System.Net;
using System.Text.Json;

namespace FullWorth.Web.Tests;

public sealed class WebSmokeTests : IClassFixture<FullWorthWebFactory>
{
    private readonly HttpClient _client;

    public WebSmokeTests(FullWorthWebFactory factory)
    {
        _client = factory.CreateClient(new() { AllowAutoRedirect = false });
    }

    [Fact]
    public async Task Health_ReturnsSuccess()
    {
        using var response = await _client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ok", json.RootElement.GetProperty("status").GetString());
        Assert.Equal("fullworth-web", json.RootElement.GetProperty("service").GetString());
    }

    [Fact]
    public async Task AnonymousAppShell_RedirectsToLogin_WhileAuthShellAndAssetsAreServed()
    {
        using var appResponse = await _client.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, appResponse.StatusCode);
        Assert.StartsWith("/auth/login?", appResponse.Headers.Location?.OriginalString);

        var login = await GetSuccessAsync("/auth/login");
        Assert.Contains("id=\"login-form\"", login);

        foreach (var path in new[]
        {
            "/app.js", "/app.css", "/dialogs.css", "/auth/auth.js", "/auth/auth.css", "/locales/de.json", "/locales/en.json"
        })
        {
            var content = await GetSuccessAsync(path);
            Assert.False(string.IsNullOrWhiteSpace(content), $"Expected {path} to contain content.");
        }
    }

    private async Task<string> GetSuccessAsync(string path)
    {
        using var response = await _client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }
}
