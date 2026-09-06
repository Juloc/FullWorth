using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Web.Tests;

public sealed class AccountsUxBaselineTests : IClassFixture<FullWorthWebFactory>
{
    private readonly FullWorthWebFactory _factory;
    private readonly HttpClient _client;

    public AccountsUxBaselineTests(FullWorthWebFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public void AccountsUx_IsLoadedBeforeApp_AndIncludedInPwaShell()
    {
        // Read the shipped app shell directly. The served "/" is behind RequireAuthorization, so an
        // unauthenticated test client is redirected to the auth shell instead of index.html; the static
        // index.html read here is exactly what an authenticated user receives via MapFallbackToFile.
        var html = ReadAsset("index.html");
        var sw = ReadAsset("sw.js");

        var uxIndex = html.IndexOf("/features/accounts-ux.js", StringComparison.Ordinal);
        var appIndex = html.IndexOf("/app.js", StringComparison.Ordinal);
        Assert.True(uxIndex >= 0 && appIndex > uxIndex, "Accounts UX must load before app.js so deep links are captured before boot.");

        Assert.Contains("/features/accounts-ux.js", sw);
        Assert.Contains("/features/accounts-ux.css", sw);
    }

    private string ReadAsset(params string[] path)
    {
        var environment = _factory.Services.GetRequiredService<IWebHostEnvironment>();
        return File.ReadAllText(Path.Combine(new[] { environment.WebRootPath }.Concat(path).ToArray()));
    }

    [Fact]
    public async Task AccountsUx_UsesBffAndPersistentAccountGroupApis()
    {
        var js = await GetAsync("/features/accounts-ux.js");

        Assert.Contains("/bff/", js);
        Assert.Contains("api/accounts", js);
        Assert.Contains("api/account-groups", js);
        Assert.Contains("api/preferences/", js);
        Assert.Contains("accounts.visuals", js);
        Assert.Contains("account-groups.visuals", js);
        Assert.Contains("transactions.seenAt", js);

        Assert.DoesNotContain("http://fullworth-backend:8080", js, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http://fullworth-banking:8080", js, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("X-FullWorth-Banking-Key", js, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConnectedAccounts_DefaultToBankLogo_AndVisualOverrideCanBeReset()
    {
        var js = await GetAsync("/features/accounts-ux.js");

        Assert.Contains("bankForAccount", js);
        Assert.Contains("hasVisualOverride", js);
        Assert.Contains("restoreDefault", js);
        Assert.Contains("delete S.prefs.accounts[a.id]", js);
        Assert.Contains("root.querySelectorAll('[data-acct]')", js);
        Assert.Contains("bankDefault=!!a.bankConnectionId||!!lg", js);
    }

    [Fact]
    public async Task AccountsUx_HoverDoesNotMoveLargeInteractiveSurfaces_AndMobileEditorExists()
    {
        var css = await GetAsync("/features/accounts-ux.css");

        Assert.Contains("transform: none !important", css);
        Assert.Contains(".panel:hover", css);
        Assert.Contains(".table-panel:hover", css);
        Assert.Contains(".account-group-savebar", css);
        Assert.Contains(".account-bank-badge", css);
        Assert.Contains("@media (max-width: 760px)", css);
        Assert.Contains("height: 100dvh", css);
    }

    [Fact]
    public async Task BankLogoCsp_ExtendsImagesOnly()
    {
        using var response = await _client.GetAsync("/");
        response.EnsureSuccessStatusCode();

        Assert.True(response.Headers.TryGetValues("Content-Security-Policy", out var values));
        var csp = string.Join(" ", values!);

        Assert.Contains("img-src 'self' data: https://enablebanking.com https://*.enablebanking.com", csp);
        Assert.Contains("script-src 'self'", csp);
        Assert.Contains("connect-src 'self'", csp);
        Assert.DoesNotContain("script-src 'self' https://", csp, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("connect-src 'self' https://", csp, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Ing_DefaultsToOwnedFinTs_WithoutRequiringEnableBanking()
    {
        var app = ReadAsset("app.js");
        var de = ReadAsset("locales", "de.json");

        Assert.Contains("fullworthProvider:'fints'", app);
        Assert.Contains("api/banking/fints/ing/connect", app);
        Assert.Contains("api/banking/fints/connections/", app);
        Assert.Contains("ingFinTsFull", de);
        Assert.Contains("ingEnableBankingOnly", de);
    }

    private async Task<string> GetAsync(string path)
    {
        using var response = await _client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }
}
