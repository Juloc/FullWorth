using System.Net;
using System.Net.Http.Json;
using FullWorth.Banking.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;

namespace FullWorth.Banking.Tests;

/// <summary>
/// When Enable Banking credentials are missing, /api/banking must answer with an explicit
/// machine-readable 503 ("banking_not_configured") instead of dying in an unhandled 500, and
/// /api/banking/status must report configured=false so the UI can guide the operator.
/// </summary>
public sealed class BankingNotConfiguredTests
{
    private const string BankingKey = "test-banking-key-4c2f9a1e";

    [Fact]
    public async Task StatusReportsNotConfigured()
    {
        using var client = CreateClient();
        using var response = await client.SendAsync(Request(HttpMethod.Get, "/api/banking/status"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<StatusResponse>();
        Assert.NotNull(body);
        Assert.False(body!.Configured);
    }

    [Fact]
    public async Task ProviderEndpointsAnswer503WithStableErrorCode()
    {
        using var client = CreateClient();

        using var institutions = await client.SendAsync(Request(HttpMethod.Get, "/api/banking/institutions?country=DE"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, institutions.StatusCode);
        var body = await institutions.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.Equal("banking_not_configured", body?.Error);

        using var connect = await client.SendAsync(Request(HttpMethod.Post, "/api/banking/connect"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, connect.StatusCode);
    }

    [Fact]
    public async Task GateStillRunsBeforeTheConfigurationCheck()
    {
        using var client = CreateClient();
        // No banking key at all: the 401 must win over the 503 so the gate cannot be probed.
        using var response = await client.GetAsync("/api/banking/institutions?country=DE");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        // The status probe is carved out of the 503 check but must NOT be carved out of the key gate.
        using var status = await client.GetAsync("/api/banking/status");
        Assert.Equal(HttpStatusCode.Unauthorized, status.StatusCode);
    }

    [Fact]
    public async Task PublicCallbackRedirectsIntoTheAppWhenUnconfigured()
    {
        // The OAuth callback is reachable without the banking key (the bank redirects the BROWSER
        // here) — every outcome must land back in the app UI, never as raw JSON or a 500.
        using var client = CreateClient(allowAutoRedirect: false);
        using var response = await client.GetAsync("/connect/enable-banking/callback?code=x&state=y");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/?bankError=app_not_configured", response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task RedirectUrlIsRequiredForConfigured()
    {
        // Regression: with ApplicationId + private key present but EnableBanking:RedirectUrl unset, the
        // connect flow used to throw an unhandled 500 ("RedirectUrl is not configured"). Now the service
        // reports not-configured and connect answers a clean 503 instead.
        var keyFile = Path.Combine(Path.GetTempPath(), "eb-" + Guid.NewGuid().ToString("N") + ".pem");
        await File.WriteAllTextAsync(keyFile, "dummy-key");
        try
        {
            using (var client = ConfiguredClient(keyFile, redirectUrl: ""))
            {
                var status = await (await client.SendAsync(Request(HttpMethod.Get, "/api/banking/status"))).Content.ReadFromJsonAsync<StatusResponse>();
                Assert.False(status!.Configured);
                using var connect = await client.SendAsync(Request(HttpMethod.Post, "/api/banking/connect"));
                Assert.Equal(HttpStatusCode.ServiceUnavailable, connect.StatusCode);
            }
            using (var client = ConfiguredClient(keyFile, redirectUrl: "https://finance.example/connect/enable-banking/callback"))
            {
                var status = await (await client.SendAsync(Request(HttpMethod.Get, "/api/banking/status"))).Content.ReadFromJsonAsync<StatusResponse>();
                Assert.True(status!.Configured);
            }
        }
        finally { File.Delete(keyFile); }
    }

    private static HttpClient ConfiguredClient(string keyFile, string redirectUrl)
    {
        var factory = new BankingWebApplicationFactory();
        var configured = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Security:ApiKey", BankingKey);
            builder.UseSetting("EnableBanking:ApplicationId", "test-app");
            builder.UseSetting("EnableBanking:PrivateKeyPath", keyFile);
            builder.UseSetting("EnableBanking:RedirectUrl", redirectUrl);
        });
        return configured.CreateClient();
    }

    private sealed record StatusResponse(bool Configured);
    private sealed record ErrorResponse(string? Error, string? Message);

    private static HttpClient CreateClient(bool allowAutoRedirect)
    {
        var factory = new BankingWebApplicationFactory();
        var configured = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Security:ApiKey", BankingKey);
            builder.UseSetting("EnableBanking:ApplicationId", "");
            builder.UseSetting("EnableBanking:PrivateKeyPath", "/nonexistent/enable-banking.pem");
        });
        return configured.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = allowAutoRedirect });
    }

    private static HttpRequestMessage Request(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-FullWorth-Banking-Key", BankingKey);
        return request;
    }

    private static HttpClient CreateClient()
    {
        var factory = new BankingWebApplicationFactory();
        var configured = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Security:ApiKey", BankingKey);
            builder.UseSetting("EnableBanking:ApplicationId", "");
            builder.UseSetting("EnableBanking:PrivateKeyPath", "/nonexistent/enable-banking.pem");
        });
        return configured.CreateClient();
    }
}
