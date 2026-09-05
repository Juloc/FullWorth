using System.Text.Json;
using FullWorth.Web.Tests;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Web.Tests.AuthUi;

public sealed class AuthUiTests : IClassFixture<FullWorthWebFactory>
{
    private static readonly string[] RequiredAuthKeys =
    [
        "appDescription", "language", "theme", "email", "password", "showPassword", "hidePassword",
        "signIn", "createAccount", "register", "registering", "displayName", "acceptTerms",
        "forgotPassword", "continue", "backToSignIn", "invalidCredentials",
        "forgotConfirmation", "newPassword", "confirmPassword", "resetPassword", "passwordChanged",
        "passkey", "recoveryCode", "recoveryCodesShownOnce", "recoveryCodesStoreSecurely", "genericError"
    ];

    private readonly FullWorthWebFactory _factory;
    private readonly HttpClient _client;

    public AuthUiTests(FullWorthWebFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task AuthShell_ContainsLoginRegisterForgotResetRecoveryAndPasskeyViews()
    {
        var html = await GetAsync("/auth/index.html");

        Assert.Contains("data-auth-view=\"login\"", html);
        Assert.Contains("data-auth-view=\"register\"", html);
        Assert.Contains("data-auth-view=\"forgot-password\"", html);
        Assert.Contains("data-auth-view=\"reset-password\"", html);
        Assert.Contains("data-auth-view=\"recovery-code\"", html);
        Assert.Contains("data-auth-view=\"recovery-codes\"", html);
        Assert.Contains("data-auth-action=\"passkey-login\"", html);
    }

    [Fact]
    public async Task Login_UsesPasswordManagerCompatibleAccessibleFields()
    {
        var html = await GetAsync("/auth/index.html");

        Assert.Contains("<label for=\"login-email\">", html);
        Assert.Contains("id=\"login-email\"", html);
        Assert.Contains("type=\"email\"", html);
        Assert.Contains("autocomplete=\"username\"", html);

        Assert.Contains("<label for=\"login-password\">", html);
        Assert.Contains("id=\"login-password\"", html);
        Assert.Contains("type=\"password\"", html);
        Assert.Contains("autocomplete=\"current-password\"", html);
        Assert.Contains("data-auth-action=\"toggle-password\"", html);
    }

    [Fact]
    public async Task Reset_UsesNewPasswordSemanticsAndMismatchState()
    {
        var html = await GetAsync("/auth/index.html");

        Assert.Contains("id=\"new-password\"", html);
        Assert.Contains("id=\"confirm-password\"", html);
        // Two fields each in registration, reset, and invite-claim views.
        Assert.Equal(6, CountOccurrences(html, "autocomplete=\"new-password\""));
        Assert.Contains("id=\"password-mismatch\"", html);
        Assert.Contains("aria-describedby=\"password-mismatch\"", html);
        Assert.Contains("data-i18n=\"auth.passwordHelp\"", html);
    }

    [Fact]
    public async Task PublicRegistrationUi_IsPresentAndLinksToLegalTerms()
    {
        var html = await GetAsync("/auth/index.html");
        var js = await GetAsync("/auth/auth.js");

        Assert.Contains("data-auth-view=\"register\"", html);
        Assert.Contains("id=\"register-form\"", html);
        Assert.Contains("action=\"/auth/register\"", html);
        Assert.Contains("https://fullworth.de/privacy/", html);
        Assert.Contains("https://fullworth.de/terms/", html);
        Assert.Contains("register: '/auth/register'", js);
        Assert.Contains("acceptTerms", js);
    }

    [Fact]
    public async Task EnumerationSafeLoginAndForgotPasswordMessages_ArePresent()
    {
        var html = await GetAsync("/auth/index.html");
        using var en = await GetLocaleAsync("en");

        Assert.Contains("data-i18n=\"auth.invalidCredentials\"", html);
        Assert.Equal("Email address or password is incorrect.",
            en.RootElement.GetProperty("auth").GetProperty("invalidCredentials").GetString());

        var confirmation = en.RootElement.GetProperty("auth").GetProperty("forgotConfirmation").GetString();
        Assert.Equal("If an account exists for this email address, recovery instructions will be sent.", confirmation);
        AssertNotContains(confirmation!, "No account found");
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task AuthLocalization_ParsesAndContainsRequiredKeys(string locale)
    {
        using var json = await GetLocaleAsync(locale);
        var auth = json.RootElement.GetProperty("auth");
        var pages = auth.GetProperty("pages");

        foreach (var key in RequiredAuthKeys)
        {
            Assert.True(auth.TryGetProperty(key, out var value), $"Missing auth.{key} in {locale}.json");
            Assert.False(string.IsNullOrWhiteSpace(value.GetString()), $"Empty auth.{key} in {locale}.json");
        }

        foreach (var page in new[] { "login", "register", "forgot-password", "reset-password", "recovery-code", "recovery-codes" })
        {
            Assert.False(string.IsNullOrWhiteSpace(pages.GetProperty(page).GetProperty("title").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(pages.GetProperty(page).GetProperty("subtitle").GetString()));
        }
    }

    [Fact]
    public async Task ThemeAndLanguage_WorkBeforeLoginWithExistingPreferenceKeys()
    {
        var html = await GetAsync("/auth/index.html");
        var js = await GetAsync("/auth/auth.js");

        Assert.Contains("data-theme-icon=\"system\"", html);
        Assert.Contains("data-theme-icon=\"light\"", html);
        Assert.Contains("data-theme-icon=\"dark\"", html);
        Assert.Contains("finance.theme", js);
        Assert.Contains("finance.language", js);
        Assert.Contains("prefers-color-scheme: dark", js);
    }

    [Fact]
    public async Task AuthJavaScript_OnlyPersistsNonSensitivePreferences()
    {
        var js = await GetAsync("/auth/auth.js");

        foreach (var line in js.Split('\n').Where(line => line.Contains("localStorage.setItem", StringComparison.Ordinal)))
        {
            Assert.True(
                line.Contains("finance.theme", StringComparison.Ordinal) ||
                line.Contains("finance.language", StringComparison.Ordinal),
                $"Unexpected localStorage write: {line.Trim()}");
        }

        AssertNotContains(js, "sessionStorage");
        AssertNotContains(js, "indexedDB");
        AssertNotContains(js, "localStorage.setItem('password");
        AssertNotContains(js, "localStorage.setItem('token");
        AssertNotContains(js, "localStorage.setItem('recovery");
    }

    [Fact]
    public async Task AuthRoutes_AreSameOriginAndAreNotIntentionallyCached()
    {
        var js = await GetAsync("/auth/auth.js");

        Assert.Contains("login: '/auth/login'", js);
        Assert.Contains("register: '/auth/register'", js);
        Assert.Contains("passwordResetRequest: '/auth/password-reset/request'", js);
        Assert.Contains("passwordResetComplete: '/auth/password-reset/complete'", js);
        AssertNotContains(js, "caches.open");
        AssertNotContains(js, "caches.put");
        AssertNotContains(js, "cache-first");
    }

    [Fact]
    public async Task AuthAssets_DoNotExposeInternalSecretsUrlsOrFinanceData()
    {
        var content = await GetAuthPublicContentAsync();

        foreach (var forbidden in new[]
                 {
                     FullWorthWebFactory.BackendSecret,
                     FullWorthWebFactory.BankingSecret,
                     FullWorthWebFactory.BackendUrl,
                     FullWorthWebFactory.BankingUrl,
                     "BackendApiKey",
                     "BankingApiKey",
                     "X-FullWorth-Key",
                     "X-FullWorth-Banking-Key",
                     "/bff/backend/",
                     "/bff/banking/",
                     "account-total",
                     "transactions-body",
                     "net-worth",
                     "budget-list"
                 })
        {
            AssertNotContains(content, forbidden);
        }
    }

    [Fact]
    public async Task RecoveryCodeDisplay_IsExplicitCopyOnlyAndNotPersisted()
    {
        var html = await GetAsync("/auth/index.html");
        var js = await GetAsync("/auth/auth.js");

        Assert.Contains("id=\"copy-recovery-codes\"", html);
        Assert.Contains("disabled", html);
        Assert.Contains("data-i18n=\"auth.recoveryCodesShownOnce\"", html);
        Assert.Contains("navigator.clipboard.writeText", js);
        Assert.Contains("finance:recovery-codes", js);
        AssertNotContains(js, "localStorage.setItem('recovery");
    }

    [Fact]
    public async Task SessionExpiredAndSignedOutGenericStates_AreSupported()
    {
        var js = await GetAsync("/auth/auth.js");
        using var en = await GetLocaleAsync("en");

        Assert.Contains("session-expired", js);
        Assert.Contains("signed-out", js);
        Assert.Equal("Your session has expired. Please sign in again.",
            en.RootElement.GetProperty("auth").GetProperty("sessionExpired").GetString());
        Assert.Equal("You have been signed out.",
            en.RootElement.GetProperty("auth").GetProperty("signedOut").GetString());
    }

    [Fact]
    public async Task AuthVisualStatesAndResponsiveAssets_AreServed()
    {
        var html = await GetAsync("/auth/index.html");
        var css = await GetAsync("/auth/auth.css");

        Assert.Contains("auth-message-error", html);
        Assert.Contains("auth-message-success", html);
        Assert.Contains("hidden", html);
        Assert.Contains(".is-loading", css);
        Assert.Contains("button:disabled", css);
        Assert.Contains("@media(max-width:600px)", css);
        Assert.Contains("100dvh", css);
        Assert.Contains(":focus-visible", css);
    }

    [Fact]
    public async Task AuthShell_HasNoInlineScriptAndKeepsPasskeyAsPlaceholder()
    {
        var html = await GetAsync("/auth/index.html");
        var js = await GetAsync("/auth/auth.js");

        Assert.Contains("<script type=\"module\" src=\"/auth/auth.js\"></script>", html);
        AssertNotContains(html, "<script>");
        AssertNotContains(js, "navigator.credentials");
        AssertNotContains(js, "PublicKeyCredential");
        AssertNotContains(js, "webauthn");
    }

    [Fact]
    public void ProgramCs_IntegratorWiresAuthentication()
    {
        var environment = _factory.Services.GetRequiredService<IWebHostEnvironment>();
        var projectRoot = Directory.GetParent(environment.WebRootPath)!.FullName;
        var program = File.ReadAllText(Path.Combine(projectRoot, "Program.cs"));

        Assert.Contains("/auth/login", program);
        Assert.Contains("/auth/register", program);
        Assert.Contains("UseAuthentication", program);
        Assert.Contains("RequireAuthorization", program);
    }

    private async Task<string> GetAuthPublicContentAsync()
    {
        return string.Join("\n",
            await GetAsync("/auth/index.html"),
            await GetAsync("/auth/auth.css"),
            await GetAsync("/auth/auth.js"),
            await GetAsync("/locales/de.json"),
            await GetAsync("/locales/en.json"));
    }

    private async Task<string> GetAuthLocaleSectionAsync(string locale)
    {
        using var json = await GetLocaleAsync(locale);
        return json.RootElement.TryGetProperty("auth", out var auth) ? auth.GetRawText() : string.Empty;
    }

    private async Task<string> GetAsync(string path)
    {
        using var response = await _client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    private async Task<JsonDocument> GetLocaleAsync(string locale)
    {
        var content = await GetAsync($"/locales/{locale}.json");
        return JsonDocument.Parse(content);
    }

    private static void AssertNotContains(string source, string value)
    {
        Assert.False(source.Contains(value, StringComparison.OrdinalIgnoreCase),
            $"Unexpected content found: {value}");
    }

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var start = 0;
        while ((start = source.IndexOf(value, start, StringComparison.Ordinal)) >= 0)
        {
            count++;
            start += value.Length;
        }

        return count;
    }
}
