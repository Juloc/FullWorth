using System.Net;
using FullWorth.Banking.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace FullWorth.Banking.Tests;

/// <summary>
/// The Enable Banking OAuth callback faces a human in a browser: every outcome — the user cancelling
/// at the bank, a bogus/expired state, missing parameters — must redirect back into the app UI with a
/// machine-readable bankError code, never surface raw JSON or an unhandled 500.
/// </summary>
public sealed class EnableBankingCallbackTests
{
    [Fact]
    public async Task UserCancellationRedirectsWithErrorCodeAndDescription()
    {
        using var client = CreateConfiguredClient();
        using var response = await client.GetAsync(
            "/connect/enable-banking/callback?state=abc&error=access_denied&error_description=Cancelled+by+user");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location?.ToString();
        Assert.NotNull(location);
        Assert.StartsWith("/?bankError=access_denied", location);
        Assert.Contains("bankErrorDescription=Cancelled%20by%20user", location);
    }

    [Fact]
    public async Task MissingCodeOrStateRedirectsWithErrorCode()
    {
        using var client = CreateConfiguredClient();
        using var response = await client.GetAsync("/connect/enable-banking/callback");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/?bankError=app_missing_parameters", response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task UnknownStateRedirectsInsteadOfCrashing()
    {
        using var client = CreateConfiguredClient();
        // No backend is reachable in this factory, so completing the connection throws — the
        // browser-facing route must translate ANY completion failure into a redirect, not a 500.
        using var response = await client.GetAsync("/connect/enable-banking/callback?code=x&state=unknown");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/?bankError=app_invalid_callback", response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task ErrorCodeAndDescriptionAreSanitizedAndCapped()
    {
        using var client = CreateConfiguredClient();
        var longDescription = Uri.EscapeDataString(new string('x', 400) + "\r\nInjected: header");
        using var response = await client.GetAsync(
            $"/connect/enable-banking/callback?state=abc&error=weird_error&error_description={longDescription}");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location?.ToString();
        Assert.NotNull(location);
        Assert.StartsWith("/?bankError=weird_error", location);
        // Control characters are stripped and the reflected description is length-capped.
        Assert.DoesNotContain("%0D", location, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("%0A", location, StringComparison.OrdinalIgnoreCase);
        Assert.True(location!.Length < 400, $"redirect not capped: {location.Length} chars");
    }

    private static HttpClient CreateConfiguredClient()
    {
        // A syntactically-present key file is enough: these paths never reach the signing code.
        var keyPath = Path.Combine(Path.GetTempPath(), $"eb-test-key-{Guid.NewGuid():N}.pem");
        File.WriteAllText(keyPath, "placeholder");

        var factory = new BankingWebApplicationFactory();
        var configured = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Security:ApiKey", "test-banking-key-4c2f9a1e");
            builder.UseSetting("EnableBanking:ApplicationId", "test-app-id");
            builder.UseSetting("EnableBanking:PrivateKeyPath", keyPath);
            builder.UseSetting("EnableBanking:RedirectUrl", "https://finance.example/connect/enable-banking/callback");
        });
        return configured.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }
}
