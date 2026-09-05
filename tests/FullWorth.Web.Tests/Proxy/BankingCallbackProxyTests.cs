using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Web.Tests.Proxy;

/// <summary>
/// The Enable Banking callback answers with a 302 into the app (/?bankConnected=… or /?bankError=…).
/// That redirect must reach the BROWSER: if the proxy's HttpClient followed it internally (the
/// HttpClientHandler default), it would chase "/" on the internal banking service, get a 404, and
/// the user would never land back in the app. This pins the pass-through behavior.
/// </summary>
public sealed class BankingCallbackProxyTests : IClassFixture<FullWorthWebFactory>
{
    private readonly FullWorthWebFactory factory;

    public BankingCallbackProxyTests(FullWorthWebFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task SetupCallbackHtmlFromBankingServicePassesThroughToTheBrowser()
    {
        var responding = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddHttpClient("banking").ConfigurePrimaryHttpMessageHandler(() => new SetupCallbackStub())));

        using var client = responding.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var response = await client.GetAsync("/connect/enable-banking/setup-callback?state=x&oobCode=y");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("Enable Banking setup completed", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallbackRedirectFromBankingServicePassesThroughToTheBrowser()
    {
        var redirecting = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddHttpClient("banking").ConfigurePrimaryHttpMessageHandler(() => new RedirectingStub())));

        using var client = redirecting.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var response = await client.GetAsync("/connect/enable-banking/callback?code=x&state=y");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/?bankConnected=Testbank", response.Headers.GetValues("Location").Single());
    }

    private sealed class SetupCallbackStub : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("/connect/enable-banking/setup-callback", request.RequestUri!.AbsolutePath);
            Assert.Equal("?state=x&oobCode=y", request.RequestUri.Query);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<h1>Enable Banking setup completed</h1>", System.Text.Encoding.UTF8, "text/html")
            });
        }
    }

    private sealed class RedirectingStub : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("/?bankConnected=Testbank", UriKind.Relative);
            return Task.FromResult(response);
        }
    }
}
