using System.Net;
using FullWorth.Banking.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;

namespace FullWorth.Banking.Tests;

/// <summary>
/// Security regression (Wave N5): the FullWorth.Banking internal API must reject any request that does
/// not carry the correct X-FullWorth-Banking-Key, while non-/api routes (health, OAuth callback) stay
/// reachable. This guards the trust boundary between the public Web BFF and the internal banking
/// service against a silent regression that opens /api to unauthenticated callers.
/// </summary>
public sealed class BankingApiKeyGateTests
{
    private const string BankingKey = "test-banking-key-4c2f9a1e";

    [Fact]
    public async Task ApiEndpoints_RejectMissingOrWrongBankingKey()
    {
        using var client = CreateClient();

        using var missing = await client.GetAsync("/api/banking/institutions?country=DE");
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);

        using var wrongRequest = new HttpRequestMessage(HttpMethod.Get, "/api/banking/institutions?country=DE");
        wrongRequest.Headers.Add("X-FullWorth-Banking-Key", "not-the-real-key");
        using var wrong = await client.SendAsync(wrongRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);

        // A mutating /api endpoint is gated the same way.
        using var postRequest = new HttpRequestMessage(HttpMethod.Post, "/api/banking/sync");
        using var post = await client.SendAsync(postRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, post.StatusCode);
    }

    [Fact]
    public async Task HealthEndpoint_IsNotGatedByBankingKey()
    {
        using var client = CreateClient();
        using var health = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    private static HttpClient CreateClient()
    {
        var factory = new BankingWebApplicationFactory();
        var configured = factory.WithWebHostBuilder(builder => builder.UseSetting("Security:ApiKey", BankingKey));
        return configured.CreateClient();
    }
}
