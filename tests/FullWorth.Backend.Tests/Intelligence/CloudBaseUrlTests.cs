using FullWorth.Backend.Modules.Intelligence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace FullWorth.Backend.Tests.Intelligence;

/// <summary>
/// FullWorth must not depend on anybody else's infrastructure: a self-hoster has to be able to point
/// their instance at their own Cloud, over the public internet, from any machine. The configured URL
/// used to be read and then discarded outside Development, so an operator who entered their own Cloud
/// kept sending observations to api.fullworth.de with no error and no warning.
/// </summary>
public sealed class CloudBaseUrlTests
{
    [Fact]
    public void A_configured_https_endpoint_is_honoured_in_production()
    {
        var uri = FullWorthCloudClient.ResolveBaseUri(
            Configuration("https://cloud.example.org"), Environment(Environments.Production));

        Assert.Equal("https://cloud.example.org/", uri.ToString());
    }

    [Fact]
    public void The_official_endpoint_stays_the_default()
    {
        Assert.Equal(
            FullWorthCloudClient.OfficialBaseUrl,
            FullWorthCloudClient.ResolveBaseUri(Configuration(null), Environment(Environments.Production)).ToString());
        Assert.Equal(
            FullWorthCloudClient.OfficialBaseUrl,
            FullWorthCloudClient.ResolveBaseUri(Configuration("   "), Environment(Environments.Production)).ToString());
    }

    // Anonymised observations still describe someone's finances, so plaintext is not a configuration
    // choice - and a copied development setting must fail loudly instead of pointing at a dead host.
    [Theory]
    [InlineData("http://cloud.example.org")]
    [InlineData("https://127.0.0.1:8097")]
    [InlineData("https://localhost:8097")]
    [InlineData("https://10.1.2.3")]
    [InlineData("https://192.168.1.9")]
    [InlineData("https://172.20.0.5")]
    [InlineData("https://cloud.example.org/?x=1")]
    [InlineData("not-a-url")]
    public void A_dangerous_or_unreachable_endpoint_is_refused_in_production(string configured)
    {
        Assert.Throws<InvalidOperationException>(() =>
            FullWorthCloudClient.ResolveBaseUri(Configuration(configured), Environment(Environments.Production)));
    }

    // In Development a local cloud is the whole point.
    [Theory]
    [InlineData("http://localhost:8097")]
    [InlineData("http://fullworth-cloud-api:8080")]
    public void Development_may_point_anywhere(string configured)
    {
        var uri = FullWorthCloudClient.ResolveBaseUri(
            Configuration(configured), Environment(Environments.Development));

        Assert.Equal(configured.TrimEnd('/') + "/", uri.ToString());
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
