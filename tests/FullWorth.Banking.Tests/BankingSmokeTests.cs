using System.Net;
using System.Text.Json;
using FullWorth.Banking.Tests.Infrastructure;

namespace FullWorth.Banking.Tests;

public sealed class BankingSmokeTests
{
    [Fact]
    public async Task HealthEndpointReturnsBankingStatus()
    {
        using var factory = new BankingWebApplicationFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ok", json.RootElement.GetProperty("status").GetString());
        Assert.Equal("fullworth-banking", json.RootElement.GetProperty("service").GetString());
    }
}
