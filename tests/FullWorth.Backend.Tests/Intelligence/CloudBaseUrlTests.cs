using System.Net;
using System.Text;
using System.Text.Json;
using FullWorth.Backend.Modules.Intelligence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace FullWorth.Backend.Tests.Intelligence;

/// <summary>
/// Outside Development this build talks to the official FullWorth Cloud and to nothing else.
///
/// Not a preference: the Cloud SERVER is a private repository. FullWorthCloud:BaseUrl promised a
/// self-hoster they could point their instance at their own Cloud, and nobody outside can build one — so
/// the setting could only ever send finance observations to a host that is not a FullWorth Cloud.
///
/// Of the three possible behaviours, the one this must never go back to is the original: read the value,
/// discard it, and keep sending to api.fullworth.de. The data then went somewhere the operator had not
/// chosen, with no error and no warning. So a foreign endpoint switches the Cloud OFF and says so. The
/// Cloud is optional, so that costs local finance features nothing.
/// </summary>
public sealed class CloudBaseUrlTests
{
    [Fact]
    public void The_official_endpoint_is_the_one_that_is_used()
    {
        Assert.Equal(
            FullWorthCloudClient.OfficialBaseUrl,
            FullWorthCloudClient.ResolveBaseUri(Configuration(null), Environment(Environments.Production)).ToString());
        Assert.Equal(
            FullWorthCloudClient.OfficialBaseUrl,
            FullWorthCloudClient.ResolveBaseUri(Configuration("   "), Environment(Environments.Production)).ToString());

        Assert.False(FullWorthCloudClient.EndpointIsRefused(Configuration(null), Environment(Environments.Production)));
    }

    /// <summary>Spelling the official endpoint out, with or without the trailing slash, is not "foreign".</summary>
    [Theory]
    [InlineData("https://api.fullworth.de")]
    [InlineData("https://api.fullworth.de/")]
    [InlineData("https://API.FullWorth.de/")]
    public void Configuring_the_official_endpoint_explicitly_changes_nothing(string configured)
    {
        Assert.False(
            FullWorthCloudClient.EndpointIsRefused(Configuration(configured), Environment(Environments.Production)));
    }

    [Theory]
    [InlineData("https://cloud.example.org")]
    [InlineData("http://cloud.example.org")]
    [InlineData("https://127.0.0.1:8097")]
    [InlineData("https://10.1.2.3")]
    [InlineData("not-a-url")]
    public void A_foreign_endpoint_is_refused_in_production(string configured)
    {
        Assert.True(
            FullWorthCloudClient.EndpointIsRefused(Configuration(configured), Environment(Environments.Production)));
    }

    /// <summary>
    /// And "refused" has to mean the call fails, not that it quietly goes to the official Cloud instead.
    /// That silent redirect is the original bug, and it is the only one of these outcomes that sends an
    /// operator's data to a host they did not pick.
    /// </summary>
    [Fact]
    public async Task A_refused_endpoint_fails_the_call_instead_of_redirecting_it()
    {
        var handler = new RecordingHandler();
        var client = new FullWorthCloudClient(
            new HttpClient(handler),
            Configuration("https://cloud.example.org"),
            Environment(Environments.Production));

        var error = await Assert.ThrowsAsync<FullWorthCloudException>(
            () => client.RegisterAsync(Guid.NewGuid(), "policy-1", "1.0.0", null, CancellationToken.None));

        Assert.Equal(FullWorthCloudClient.EndpointNotConfigurableErrorCode, error.ErrorCode);
        Assert.False(error.Transient);
        Assert.Contains("official FullWorth Cloud", error.Remediation);

        // Nothing left this instance. Not to the configured host, and not to the official one either.
        Assert.Null(handler.LastRequestUri);
    }

    /// <summary>
    /// A misconfigured Cloud endpoint must not take the instance down with it. Core finance features work
    /// with no Cloud at all, so constructing the client has to keep succeeding.
    /// </summary>
    [Fact]
    public void A_refused_endpoint_does_not_stop_the_instance_from_starting()
    {
        var client = new FullWorthCloudClient(
            new HttpClient(new RecordingHandler()),
            Configuration("not-a-url"),
            Environment(Environments.Production));

        Assert.Equal(FullWorthCloudClient.OfficialBaseUrl, client.BaseUri.ToString());
    }

    // In Development a local cloud is the whole point - and it is where this repository runs its tests.
    [Theory]
    [InlineData("http://localhost:8097")]
    [InlineData("http://fullworth-cloud-api:8080")]
    public void Development_may_point_anywhere(string configured)
    {
        Assert.False(
            FullWorthCloudClient.EndpointIsRefused(Configuration(configured), Environment(Environments.Development)));

        var uri = FullWorthCloudClient.ResolveBaseUri(
            Configuration(configured), Environment(Environments.Development));

        Assert.Equal(configured.TrimEnd('/') + "/", uri.ToString());
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public Uri? LastRequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequestUri = request.RequestUri;
            var body = JsonSerializer.Serialize(new { instanceId = Guid.Empty, credential = "x" });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    private static IConfiguration Configuration(string? baseUrl) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["FullWorthCloud:BaseUrl"] = baseUrl })
            .Build();

    private static IHostEnvironment Environment(string name) => new StubEnvironment { EnvironmentName = name };

    private sealed class StubEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "FullWorth.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
