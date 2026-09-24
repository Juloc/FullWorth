using System.Text.Json;
using FullWorth.Web.Tests;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Web.Tests.PasskeyUi;

public sealed class PasskeyUiTests : IClassFixture<FullWorthWebFactory>
{
    private readonly FullWorthWebFactory _factory;
    private readonly HttpClient _client;

    public PasskeyUiTests(FullWorthWebFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task LoginHook_RemainsOnExistingAuthButton()
    {
        var html = await GetAsync("/auth/index.html");
        var authJs = await GetAsync("/auth/auth.js");
        Assert.Contains("data-auth-action=\"passkey-login\"", html);
        Assert.Contains("initializePasskeyLogin", authJs);
    }

    [Fact]
    public async Task Login_UsesNativeNavigatorCredentialsGet()
    {
        var js = await GetAsync("/passkeys/passkeys.js");
        Assert.Contains("navigator.credentials.get({ publicKey })", js);
    }

    [Fact]
    public async Task Registration_UsesNativeNavigatorCredentialsCreate()
    {
        var js = await GetAsync("/passkeys/passkeys.js");
        Assert.Contains("navigator.credentials.create({ publicKey })", js);
    }

    [Fact]
    public async Task FeatureDetection_RequiresPublicKeyCredentialCredentialsAndSecureContext()
    {
        var js = await GetAsync("/passkeys/passkeys.js");
        Assert.Contains("window.PublicKeyCredential", js);
        Assert.Contains("navigator.credentials", js);
        Assert.Contains("window.isSecureContext", js);
    }

    [Fact]
    public async Task Base64Url_HelperIsSingleDedicatedModule()
    {
        var helper = await GetAsync("/passkeys/base64url.js");
        var core = await GetAsync("/passkeys/passkeys.js");
        Assert.Contains("decodeBase64Url", helper);
        Assert.Contains("encodeBase64Url", helper);
        Assert.Contains("from './base64url.js'", core);
    }

    [Fact]
    public async Task Base64UrlDecode_HandlesUrlSafeAlphabetAndPadding()
    {
        var helper = await GetAsync("/passkeys/base64url.js");
        Assert.Contains("replace(/-/g, '+')", helper);
        Assert.Contains("replace(/_/g, '/')", helper);
        Assert.Contains("padEnd", helper);
        Assert.Contains("atob(padded)", helper);
    }

    [Fact]
    public async Task Base64UrlEncode_ProducesUrlSafeAlphabetWithoutPadding()
    {
        var helper = await GetAsync("/passkeys/base64url.js");
        Assert.Contains("replace(/\\+/g, '-')", helper);
        Assert.Contains("replace(/\\//g, '_')", helper);
        Assert.Contains("replace(/=+$/g, '')", helper);
    }

    [Fact]
    public async Task RegistrationChallengeAndUserId_AreConvertedFromBase64Url()
    {
        var js = await GetAsync("/passkeys/passkeys.js");
        Assert.Contains("challenge: decodeBase64Url(source.challenge)", js);
        Assert.Contains("id: decodeBase64Url(source.user.id)", js);
        Assert.Contains("excludeCredentials", js);
    }

    [Fact]
    public async Task LoginChallengeAndAllowedCredentialIds_AreConvertedFromBase64Url()
    {
        var js = await GetAsync("/passkeys/passkeys.js");
        Assert.Contains("challenge: decodeBase64Url(source.challenge)", js);
        Assert.Contains("allowCredentials", js);
        Assert.Contains("id: decodeBase64Url(credential.id)", js);
    }

    [Fact]
    public async Task RegistrationSerialization_ContainsRequiredWebAuthnFields()
    {
        var js = await GetAsync("/passkeys/passkeys.js");
        foreach (var field in new[] { "id: credential.id", "rawId:", "type: credential.type", "clientDataJSON:", "attestationObject:", "getTransports", "clientExtensionResults" })
            Assert.Contains(field, js);
    }

    [Fact]
    public async Task AssertionSerialization_ContainsRequiredWebAuthnFields()
    {
        var js = await GetAsync("/passkeys/passkeys.js");
        foreach (var field in new[] { "id: credential.id", "rawId:", "type: credential.type", "clientDataJSON:", "authenticatorData:", "signature:", "userHandle:", "clientExtensionResults" })
            Assert.Contains(field, js);
    }

    [Fact]
    public async Task BrowserContract_DoesNotUseInternalUserIdentifiers()
    {
        var content = await GetPasskeyContentAsync();
        AssertNotContains(content, "AuthUserId");
        AssertNotContains(content, "FinanceUserId");
    }

    [Fact]
    public async Task PasskeyCode_DoesNotPersistSecretsInLocalStorage()
    {
        var content = await GetPasskeyContentAsync();
        AssertNotContains(content, "localStorage.setItem");
    }

    [Fact]
    public async Task PasskeyCode_DoesNotUseSessionStorage()
    {
        var content = await GetPasskeyContentAsync();
        AssertNotContains(content, "sessionStorage");
    }

    [Fact]
    public async Task PasskeyCode_DoesNotUseIndexedDb()
    {
        var content = await GetPasskeyContentAsync();
        AssertNotContains(content, "indexedDB");
    }

    [Fact]
    public async Task PasskeyCode_DoesNotAddJwtOrBearerSessionMechanism()
    {
        var content = await GetPasskeyContentAsync();
        AssertNotContains(content, "document.cookie");
        AssertNotContains(content, "Bearer ");
        AssertNotContains(content, "jwt");
        AssertNotContains(content, "Authorization");
    }

    [Fact]
    public async Task PasskeyAction_RemainsSeparateFromPasswordLogin()
    {
        var html = await GetAsync("/auth/index.html");
        Assert.Contains("id=\"login-form\"", html);
        Assert.Contains("type=\"password\"", html);
        Assert.Contains("data-auth-action=\"passkey-login\"", html);
    }

    [Fact]
    public async Task UnsupportedBrowserFallback_DisablesPasskeyActionAndShowsStatus()
    {
        var js = await GetAsync("/passkeys/passkeys.js");
        Assert.Contains("if (!isPasskeySupported())", js);
        Assert.Contains("button.disabled = true", js);
        Assert.Contains("auth.passkeyUnsupported", js);
    }

    [Fact]
    public async Task PasswordFallback_RemainsVisibleAndTranslated()
    {
        var html = await GetAsync("/auth/index.html");
        using var en = JsonDocument.Parse(await GetAsync("/locales/en.json"));
        Assert.Contains("data-i18n=\"auth.signIn\"", html);
        Assert.False(string.IsNullOrWhiteSpace(en.RootElement.GetProperty("auth").GetProperty("password").GetString()));
    }

    [Fact]
    public async Task RateLimit429_HasDedicatedGenericUiHandling()
    {
        var js = await GetAsync("/passkeys/passkeys.js");
        Assert.Contains("error?.status === 429", js);
        Assert.Contains("passkeyRetryLater", js);
        Assert.Contains("passkeys.retryLater", js);
    }

    [Fact]
    public async Task CancellationAndExpectedWebAuthnErrors_AreHandled()
    {
        var js = await GetAsync("/passkeys/passkeys.js");
        foreach (var name in new[] { "NotAllowedError", "AbortError", "InvalidStateError", "SecurityError" })
            Assert.Contains(name, js);
    }

    [Fact]
    public async Task GermanTranslations_ContainPasskeyUiKeys()
    {
        using var json = JsonDocument.Parse(await GetAsync("/locales/de.json"));
        AssertPasskeyTranslations(json);
    }

    [Fact]
    public async Task EnglishTranslations_ContainPasskeyUiKeys()
    {
        using var json = JsonDocument.Parse(await GetAsync("/locales/en.json"));
        AssertPasskeyTranslations(json);
    }

    [Fact]
    public async Task PasskeyImplementation_IsExternalModuleBasedWithoutEvalOrInlineHandlers()
    {
        var html = await PageMarkupAsync();
        var content = await GetPasskeyContentAsync();
        AssertNotContains(html, "onclick=");
        AssertNotContains(html, "<script>");
        AssertNotContains(content, "eval(");
        AssertNotContains(content, "new Function");
    }

    [Fact]
    public async Task EndpointContract_UsesOnlySameOriginFinanceWebPaths()
    {
        var js = await GetAsync("/passkeys/passkeys.js");
        foreach (var route in new[]
                 {
                     "/auth/passkeys/login/begin", "/auth/passkeys/login/complete",
                     "/auth/passkeys/register/begin", "/auth/passkeys/register/complete",
                     "credentials: '/auth/passkeys'"
                 })
            Assert.Contains(route, js);
        Assert.Contains("credentials: 'same-origin'", js);
    }

    [Fact]
    public async Task PasskeyAssets_DoNotExposeBackendBankingUrlsOrKeys()
    {
        var content = await GetPasskeyContentAsync();
        foreach (var forbidden in new[] { "FullWorth.Backend", "FullWorth.Banking", "BackendApiKey", "BankingApiKey", "X-FullWorth-Key", "/bff/backend/", "/bff/banking/" })
            AssertNotContains(content, forbidden);
    }

    [Fact]
    public async Task CredentialListUi_ExistsAndUsesSafeDisplayFields()
    {
        var html = await PageMarkupAsync();
        var js = await GetAsync("/passkeys/passkeys.js");
        Assert.Contains("id=\"passkey-list\"", html);
        Assert.Contains("credential.displayName", js);
        Assert.Contains("credential.createdAt", js);
        Assert.Contains("credential.lastUsedAt", js);
        AssertNotContains(js, "credential.publicKey");
        AssertNotContains(js, "signatureCounter");
    }

    [Fact]
    public async Task CredentialRemoval_UsesDeleteAgainstServerProvidedManagementId()
    {
        var js = await GetAsync("/passkeys/passkeys.js");
        Assert.Contains("remove.dataset.passkeyAction = 'remove'", js);
        Assert.Contains("button.dataset.managementId", js);
        Assert.Contains("method: 'DELETE'", js);
        Assert.Contains("encodeURIComponent(managementId)", js);
    }

    [Fact]
    public async Task CredentialRemoval_RequiresExplicitConfirmation()
    {
        // Removing a credential must still require an explicit confirmation, but no longer through
        // window.confirm: it goes through the shared confirm dialog, marked destructive and with
        // localized labels, which is the app's own component rather than a native browser prompt.
        var js = await GetAsync("/passkeys/passkeys.js");
        Assert.Contains("confirmMessage", js);
        Assert.Contains("passkeys.removeConfirm", js);
        Assert.Contains("destructive:true", js);
    }

    [Fact]
    public async Task AccessibilityBasics_ArePresent()
    {
        var html = await PageMarkupAsync();
        Assert.Contains("<button type=\"button\"", html);
        Assert.Contains("<label for=\"passkey-name\">", html);
        Assert.Contains("aria-live=\"polite\"", html);
        Assert.Contains("role=\"status\"", html);

        // Der Fokusring kommt aus den geteilten Komponenten. Er stand hier einmal ein zweites Mal,
        // solange die Seite ihre Knöpfe selbst baute — und wäre damit eine Kopie gewesen, die man
        // hätte vergessen können.
        Assert.Contains(":focus-visible", await GetAsync("/styles/components.css"));
    }

    [Fact]
    public async Task AntiforgeryIntegration_UsesSingleD3BrowserFetchContract()
    {
        // The passkey page must fetch through the one shared, antiforgery-aware wrapper rather than
        // calling fetch itself. That wrapper is security/secure-fetch.js, which the page imports as a
        // module; the antiforgery contract and the ban on client-side token storage are asserted against
        // the module that is actually used.
        var js = await GetAsync("/passkeys/passkeys.js");
        var secureFetch = await GetAsync("/security/secure-fetch.js");
        Assert.Contains("../security/secure-fetch.js", js);
        Assert.Contains("secureFetch", js);
        Assert.Contains("/auth/antiforgery", secureFetch);
        Assert.Contains("X-CSRF-TOKEN", secureFetch);
        AssertNotContains(secureFetch, "localStorage");
        AssertNotContains(secureFetch, "sessionStorage");
        AssertNotContains(secureFetch, "document.cookie");
    }

    [Fact]
    public async Task LoginSuccess_ReusesExistingSafeReturnPathPolicy()
    {
        var authJs = await GetAsync("/auth/auth.js");
        Assert.Contains("onSuccess: payload => location.assign(resolveSafeReturnPath(payload?.returnUrl))", authJs);
        Assert.Contains("function resolveSafeReturnPath(serverValue)", authJs);
        Assert.Contains("candidate.origin === location.origin", authJs);
    }

    [Fact]
    public async Task ManagementShell_UsesExistingThemeAndLanguagePreferenceKeysOnlyForDisplay()
    {
        // Die Seite liest Sprache und Theme nicht mehr selbst — die Hülle hat beide längst gesetzt,
        // bevor diese Seite überhaupt sichtbar wird. Was bleibt, ist die eigentliche Aussage: kein
        // Passkey-Code schreibt in den Speicher des Browsers.
        var js = await PageScriptAsync();
        AssertNotContains(js, "localStorage.setItem");
        AssertNotContains(js, "localStorage.getItem");
    }

    [Fact]
    public async Task RegistrationLabel_IsClientLimitedAndSentInsideServerContract()
    {
        var html = await PageMarkupAsync();
        var js = await GetAsync("/passkeys/passkeys.js");
        Assert.Contains("maxlength=\"80\"", html);
        Assert.Contains("slice(0, 80)", js);
        Assert.Contains("JSON.stringify({ challengeId: begin.challengeId, displayName, credential:", js);
    }

    [Fact]
    public async Task ProtectedManagementShell_IsNotAnonymous()
    {
        using var client = _factory.CreateRawClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var response = await client.GetAsync("/settings/security/passkeys");
        Assert.Equal(System.Net.HttpStatusCode.Redirect, response.StatusCode);
    }

    private async Task<string> GetPasskeyContentAsync()
    {
        return string.Join('\n',
            await GetAsync("/passkeys/base64url.js"),
            await GetAsync("/passkeys/passkeys.js"),
            await PageScriptAsync(),
            await PageMarkupAsync(),
            await PageStyleAsync());
    }

    // Gelesen wird aus der Quelle statt über die Adresse geholt: /settings/security/passkeys lieferte
    // bis #154 die ganze Hülle, und darin stünde jede Zusicherung auch dann, wenn sie mit dieser Seite
    // gar nichts zu tun hat. Das Markup liegt seitdem als Razor-Seite bei der Seite, page.js und
    // page.css weiterhin unter wwwroot.
    private Task<string> PageMarkupAsync() => Task.FromResult(WebSources.Page("Settings/Security/Passkeys"));
    private Task<string> PageScriptAsync() => PageFileAsync("page.js");
    private Task<string> PageStyleAsync() => PageFileAsync("page.css");

    private async Task<string> PageFileAsync(string name)
    {
        var environment = _factory.Services.GetRequiredService<IWebHostEnvironment>();
        return await File.ReadAllTextAsync(
            Path.Combine(environment.WebRootPath, "pages", "settings", "security", "passkeys", name));
    }

    private async Task<string> GetAsync(string path)
    {
        using var response = await _client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    private static void AssertPasskeyTranslations(JsonDocument json)
    {
        var auth = json.RootElement.GetProperty("auth");
        foreach (var key in new[] { "passkey", "passkeySigningIn", "passkeyUnsupported", "passkeyCancelled", "passkeyCouldNotUse", "passkeyRetryLater" })
            Assert.False(string.IsNullOrWhiteSpace(auth.GetProperty(key).GetString()));

        var passkeys = json.RootElement.GetProperty("passkeys");
        foreach (var key in new[] { "title", "add", "yourPasskeys", "remove", "name", "created", "lastUsed", "added", "removed", "unsupported", "retryLater", "empty" })
            Assert.False(string.IsNullOrWhiteSpace(passkeys.GetProperty(key).GetString()));
    }

    private static void AssertNotContains(string source, string value)
    {
        Assert.False(source.Contains(value, StringComparison.OrdinalIgnoreCase), $"Unexpected content found: {value}");
    }
}
