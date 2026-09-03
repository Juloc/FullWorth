using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace FullWorth.Web.Tests.Security;

public sealed class WaveDLoginCsrfIntegrationTests
{
    [Fact]
    public async Task LoginWithoutAntiforgeryTokenFailsInRealProgram()
    {
        await using var factory = new FullWorthWebFactory();
        using var client = factory.CreateRawClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false
        });

        using var response = await client.PostAsJsonAsync("/auth/login", new
        {
            email = "csrf-test@example.invalid",
            password = "not-a-real-password"
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(response.Headers.Contains("Content-Security-Policy"));
        Assert.True(response.Headers.Contains("X-Content-Type-Options"));
    }
}
