using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Web.Tests;

public sealed class FrontendBaselineTests : IClassFixture<FullWorthWebFactory>
{
    private readonly FullWorthWebFactory _factory;
    private readonly HttpClient _client;

    public FrontendBaselineTests(FullWorthWebFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Theme_SystemLightAndDark_AreRepresented()
    {
        var html = await GetAsync("/");
        var js = await GetAsync("/app.js");
        var css = await GetAsync("/app.css");

        Assert.Contains("value=\"system\"", html);
        Assert.Contains("value=\"light\"", html);
        Assert.Contains("value=\"dark\"", html);
        Assert.Contains("state.theme==='system'", js);
        Assert.Contains("prefers-color-scheme: dark", js);
        Assert.Contains("html[data-theme=\"dark\"]", css);
    }

    [Fact]
    public async Task BrowserApiCalls_UseBffRoutes_NotInternalServiceUrls()
    {
        var js = await GetAsync("/app.js");

        Assert.Contains("/bff/backend/", js);
        Assert.Contains("/bff/banking/", js);
        AssertNoInternalServiceUrl(js, "app.js");
    }

    [Fact]
    public async Task ReceiptUpload_UsesFinanceWebBackendBff()
    {
        // The receipt upload now lives in the purchases feature module and routes through the shared
        // api() BFF helper in app.js; assert both the call site and the helper, and that neither leaks
        // an internal service URL.
        var purchasesJs = await GetAsync("/features/purchases.js");
        var appJs = await GetAsync("/app.js");

        Assert.Contains("api('api/purchases/receipt-scan'", purchasesJs);
        Assert.Contains("fetch(`/bff/backend/", appJs);
        AssertNoInternalServiceUrl(purchasesJs, "features/purchases.js");
        AssertNoInternalServiceUrl(appJs, "app.js");
    }

    [Fact]
    public async Task PublicResponses_DoNotExposeConfiguredSecretsOrInternalUrls()
    {
        foreach (var path in new[] { "/", "/app.js", "/app.css", "/dialogs.css", "/locales/de.json", "/locales/en.json", "/health" })
        {
            var content = await GetAsync(path);
            AssertDoesNotContain(content, FullWorthWebFactory.BackendSecret, path);
            AssertDoesNotContain(content, FullWorthWebFactory.BankingSecret, path);
            AssertDoesNotContain(content, FullWorthWebFactory.BackendUrl, path);
            AssertDoesNotContain(content, FullWorthWebFactory.BankingUrl, path);
        }

        using var appSettingsResponse = await _client.GetAsync("/appsettings.json");
        Assert.Equal(HttpStatusCode.NotFound, appSettingsResponse.StatusCode);
    }

    [Fact]
    public void PublicFrontendFiles_DoNotContainInternalUrlsOrServiceKeyPlaceholders()
    {
        foreach (var file in GetPublicTextFiles())
        {
            var content = File.ReadAllText(file);
            AssertNoInternalServiceUrl(content, file);

            if (!file.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var token in new[]
                     {
                         "BackendApiKey", "BankingApiKey", "X-FullWorth-Key", "X-FullWorth-Banking-Key",
                         "FULLWORTH_BACKEND_API_KEY", "FULLWORTH_BANKING_API_KEY"
                     })
            {
                AssertDoesNotContain(content, token, file);
            }
        }
    }

    [Fact]
    public void OfflineStorageLogic_DoesNotReferenceSensitiveFinanceApiRoutes()
    {
        var offlineMarkers = new[] { "caches.open", "caches.put", "indexedDB", "localForage" };
        var sensitiveRoutes = new[]
        {
            "/bff/backend/", "/bff/banking/", "api/accounts", "api/transactions", "api/purchases",
            "api/analytics", "api/budgets", "api/contracts", "api/assets", "api/liabilities"
        };

        foreach (var file in GetPublicTextFiles().Where(path => path.EndsWith(".js", StringComparison.OrdinalIgnoreCase)))
        {
            var content = File.ReadAllText(file);
            if (!offlineMarkers.Any(marker => content.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            foreach (var route in sensitiveRoutes)
            {
                AssertDoesNotContain(content, route, file);
            }
        }
    }

    private IEnumerable<string> GetPublicTextFiles()
    {
        var environment = _factory.Services.GetRequiredService<IWebHostEnvironment>();
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".html", ".js", ".mjs", ".css", ".json", ".webmanifest"
        };

        return Directory.EnumerateFiles(environment.WebRootPath, "*", SearchOption.AllDirectories)
            .Where(path => extensions.Contains(Path.GetExtension(path)));
    }

    private async Task<string> GetAsync(string path)
    {
        using var response = await _client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    private static void AssertNoInternalServiceUrl(string content, string source)
    {
        AssertDoesNotContain(content, "http://fullworth-backend:8080", source);
        AssertDoesNotContain(content, "http://fullworth-banking:8080", source);
    }

    private static void AssertDoesNotContain(string content, string token, string source)
    {
        Assert.False(content.Contains(token, StringComparison.OrdinalIgnoreCase), $"{source} exposed forbidden token: {token}");
    }
}
