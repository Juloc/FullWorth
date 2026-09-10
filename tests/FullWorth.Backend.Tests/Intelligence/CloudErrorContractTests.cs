using System.Net;
using System.Text;
using FullWorth.Backend.Modules.Intelligence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace FullWorth.Backend.Tests.Intelligence;

/// <summary>
/// The Cloud states the real reason for a refusal and what to do about it in the response body. This
/// client read only the HTTP status, so every 403 became the same generic
/// <c>cloud_entitlement_denied</c>, the Cloud's own remediation sentence was thrown away, and the UI
/// could only show a bare snake_case token.
/// </summary>
public sealed class CloudErrorContractTests
{
    [Fact]
    public async Task The_clouds_own_error_code_and_remediation_survive_the_transport()
    {
        var client = Client(
            HttpStatusCode.Forbidden,
            "application/json",
            """{"errorCode":"benchmark_metric_invalid","message":"Unknown metric key.","remediation":"Use one of the metric keys from /v1/benchmarks/metrics."}""");

        var exception = await Assert.ThrowsAsync<FullWorthCloudException>(() =>
            client.GetBenchmarkAsync("secret", "nonsense", "EUR", "DE", null, null, null, null, null, CancellationToken.None));

        Assert.Equal("benchmark_metric_invalid", exception.ErrorCode);
        Assert.Equal("Unknown metric key.", exception.Message);
        Assert.Equal("Use one of the metric keys from /v1/benchmarks/metrics.", exception.Remediation);
    }

    // A reverse proxy answering instead of the Cloud sends HTML. The status-derived code is still the
    // right answer then - a failure to parse an error must never replace the error.
    [Fact]
    public async Task A_body_that_is_not_the_error_contract_falls_back_to_the_status()
    {
        var client = Client(HttpStatusCode.Forbidden, "text/html", "<html><body>403 Forbidden</body></html>");

        var exception = await Assert.ThrowsAsync<FullWorthCloudException>(() =>
            client.GetBenchmarkAsync("secret", "metric", "EUR", "DE", null, null, null, null, null, CancellationToken.None));

        Assert.Equal("cloud_entitlement_denied", exception.ErrorCode);
        Assert.Null(exception.Remediation);
    }

    [Fact]
    public async Task Malformed_json_falls_back_to_the_status_without_throwing_a_json_error()
    {
        var client = Client(HttpStatusCode.InternalServerError, "application/json", "{not json");

        var exception = await Assert.ThrowsAsync<FullWorthCloudException>(() =>
            client.GetBenchmarkAsync("secret", "metric", "EUR", "DE", null, null, null, null, null, CancellationToken.None));

        Assert.Equal("cloud_server_error", exception.ErrorCode);
        Assert.True(exception.Transient);
    }

    // A sentence in the errorCode field is not a machine code, and letting it through would put free
    // text where callers switch on a token.
    [Fact]
    public async Task An_error_code_that_is_not_a_code_is_ignored()
    {
        var longText = new string('x', 200);
        var client = Client(
            HttpStatusCode.Unauthorized,
            "application/json",
            $"{{\"errorCode\":\"{longText}\"}}");

        var exception = await Assert.ThrowsAsync<FullWorthCloudException>(() =>
            client.GetBenchmarkAsync("secret", "metric", "EUR", "DE", null, null, null, null, null, CancellationToken.None));

        Assert.Equal("cloud_unauthorized", exception.ErrorCode);
    }

    // Transience is a property of the status, not of the body: a 429 stays retryable whatever it says.
    [Fact]
    public async Task A_rate_limit_stays_transient_even_with_its_own_error_code()
    {
        var client = Client(
            HttpStatusCode.TooManyRequests,
            "application/json",
            """{"errorCode":"cloud_rate_limited","remediation":"Retry after the window."}""");

        var exception = await Assert.ThrowsAsync<FullWorthCloudException>(() =>
            client.GetBenchmarkAsync("secret", "metric", "EUR", "DE", null, null, null, null, null, CancellationToken.None));

        Assert.True(exception.Transient);
        Assert.Equal("Retry after the window.", exception.Remediation);
    }

    private static FullWorthCloudClient Client(HttpStatusCode status, string mediaType, string body)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["FullWorthCloud:BaseUrl"] = "http://cloud.test" })
            .Build();
        return new FullWorthCloudClient(
            new HttpClient(new StubHandler(status, mediaType, body)),
            configuration,
            new TestEnvironment());
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class StubHandler(HttpStatusCode status, string mediaType, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, mediaType)
            });
    }
}
