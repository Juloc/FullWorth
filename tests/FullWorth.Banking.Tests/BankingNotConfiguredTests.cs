using System.Net;
using System.Net.Http.Json;
using FullWorth.Banking.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;

namespace FullWorth.Banking.Tests;

/// <summary>
/// BYO Enable Banking configuration is user-scoped. Missing credentials are a normal setup state:
/// status reports configured=false and provider operations return a stable profile-not-ready conflict.
/// </summary>
public sealed class BankingNotConfiguredTests
{
    private const string BankingKey = "test-banking-key-4c2f9a1e";
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid SpaceId = Guid.NewGuid();

    [Fact]
    public async Task StatusReportsNotConfiguredForCurrentUser()
    {
        using var client = CreateClient();
        using var response = await client.SendAsync(Request(HttpMethod.Get, "/api/banking/status"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<StatusResponse>();
        Assert.NotNull(body);
        Assert.False(body!.Configured);
        Assert.False(body.LegacyConfigured);
    }

    [Fact]
    public async Task ProviderEndpointsReportUserProfileNotReady()
    {
        using var client = CreateClient();

        using var institutions = await client.SendAsync(
            Request(HttpMethod.Get, "/api/banking/institutions?country=DE"));
        Assert.Equal(HttpStatusCode.Conflict, institutions.StatusCode);
        var body = await institutions.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.Equal("banking_profile_not_ready", body?.Error);

        using var connect = Request(HttpMethod.Post, "/api/banking/connect");
        connect.Content = JsonContent.Create(new
        {
            institutionName = "Test Bank",
            country = "DE",
            validDays = 365
        });
        using var connectResponse = await client.SendAsync(connect);
        Assert.Equal(HttpStatusCode.Conflict, connectResponse.StatusCode);
    }

    [Fact]
    public async Task BankingKeyGateStillRunsBeforeUserSetup()
    {
        using var client = CreateClient();

        using var institutions = await client.GetAsync("/api/banking/institutions?country=DE");
        Assert.Equal(HttpStatusCode.Unauthorized, institutions.StatusCode);

        using var status = await client.GetAsync("/api/banking/status");
        Assert.Equal(HttpStatusCode.Unauthorized, status.StatusCode);
    }

    [Fact]
    public async Task PublicCallbackWithUnknownStateRedirectsIntoApp()
    {
        using var client = CreateClient(allowAutoRedirect: false);
        using var response = await client.GetAsync("/connect/enable-banking/callback?code=x&state=y");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/?bankError=app_invalid_callback", response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task LegacyGlobalCredentialsDoNotAuthorizeNewUserConnections()
    {
        var keyFile = Path.Combine(Path.GetTempPath(), "eb-" + Guid.NewGuid().ToString("N") + ".pem");
        await File.WriteAllTextAsync(keyFile, "legacy-key-placeholder");
        try
        {
            using var client = ConfiguredClient(
                keyFile,
                "https://finance.example/connect/enable-banking/callback");

            using var response = await client.SendAsync(
                Request(HttpMethod.Get, "/api/banking/institutions?country=DE"));

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<ErrorResponse>();
            Assert.Equal("banking_profile_not_ready", body?.Error);
        }
        finally
        {
            File.Delete(keyFile);
        }
    }

    [Fact]
    public async Task LegacyGlobalSetupStillRequiresRedirectUrl()
    {
        var keyFile = Path.Combine(Path.GetTempPath(), "eb-" + Guid.NewGuid().ToString("N") + ".pem");
        await File.WriteAllTextAsync(keyFile, "dummy-key");
        try
        {
            using (var client = ConfiguredClient(keyFile, redirectUrl: ""))
            {
                var status = await (await client.SendAsync(Request(HttpMethod.Get, "/api/banking/status")))
                    .Content.ReadFromJsonAsync<StatusResponse>();
                Assert.False(status!.Configured);
            }

            using (var client = ConfiguredClient(
                       keyFile,
                       "https://finance.example/connect/enable-banking/callback"))
            {
                var status = await (await client.SendAsync(Request(HttpMethod.Get, "/api/banking/status")))
                    .Content.ReadFromJsonAsync<StatusResponse>();
                Assert.True(status!.Configured);
                Assert.True(status.LegacyConfigured);
            }
        }
        finally
        {
            File.Delete(keyFile);
        }
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

    private sealed record StatusResponse(bool Configured, bool LegacyConfigured);
    private sealed record ErrorResponse(string? Error, string? Message);

    private static HttpRequestMessage Request(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-FullWorth-Banking-Key", BankingKey);
        request.Headers.Add("X-FullWorth-User-Id", UserId.ToString("D"));
        request.Headers.Add("X-FullWorth-Space-Id", SpaceId.ToString("D"));
        return request;
    }

    private static HttpClient CreateClient(bool allowAutoRedirect = true)
    {
        var factory = new BankingWebApplicationFactory();
        var configured = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Security:ApiKey", BankingKey);
            builder.UseSetting("EnableBanking:ApplicationId", "");
            builder.UseSetting("EnableBanking:PrivateKeyPath", "/nonexistent/enable-banking.pem");
            builder.UseSetting("EnableBanking:RedirectUrl", "https://finance.example/connect/enable-banking/callback");
        });
        return configured.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = allowAutoRedirect
        });
    }
}
