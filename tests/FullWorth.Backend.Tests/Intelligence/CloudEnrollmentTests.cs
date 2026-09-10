using System.Net;
using System.Text;
using System.Text.Json;
using FullWorth.Backend.Modules.Intelligence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace FullWorth.Backend.Tests.Intelligence;

/// <summary>
/// An external self-hosted FullWorth must be able to activate Cloud against the official endpoint without any
/// private Cloud-server secret (no shared Docker secrets volume, no global enrollment token). The shared
/// enrollment token stays supported for gated same-host/private Cloud deployments.
/// </summary>
public sealed class CloudEnrollmentTests
{
    [Fact]
    public async Task Registration_without_an_enrollment_token_succeeds_and_sends_no_enrollment_header()
    {
        var instanceId = Guid.NewGuid();
        var handler = new CapturingHandler(instanceId);
        var client = CreateClient(handler, enrollmentToken: null);

        var result = await client.RegisterAsync(instanceId, "policy-1", "1.0.0", null, CancellationToken.None);

        Assert.Equal(instanceId, result.InstanceId);
        Assert.Equal("instance-secret", result.Credential);
        Assert.False(handler.Request!.Headers.Contains("X-FullWorth-Enrollment-Token"));
        // A first enrollment has nothing to prove and must not send an Authorization header.
        Assert.Null(handler.Request.Headers.Authorization);
    }

    [Fact]
    public async Task Registration_still_presents_the_enrollment_token_when_an_operator_configured_one()
    {
        var instanceId = Guid.NewGuid();
        var handler = new CapturingHandler(instanceId);
        var client = CreateClient(handler, enrollmentToken: "shared-token");

        await client.RegisterAsync(instanceId, "policy-1", "1.0.0", null, CancellationToken.None);

        Assert.True(handler.Request!.Headers.TryGetValues("X-FullWorth-Enrollment-Token", out var values));
        Assert.Equal("shared-token", Assert.Single(values!));
    }

    // Re-registering a known instance id revokes that instance's live credential, so the Cloud only
    // allows it for a caller that proves it holds one. The client has to present it.
    [Fact]
    public async Task A_re_registration_presents_the_current_credential_as_proof()
    {
        var instanceId = Guid.NewGuid();
        var handler = new CapturingHandler(instanceId);
        var client = CreateClient(handler, enrollmentToken: null);

        await client.RegisterAsync(instanceId, "policy-1", "1.0.0", "held-credential", CancellationToken.None);

        Assert.Equal("Bearer", handler.Request!.Headers.Authorization?.Scheme);
        Assert.Equal("held-credential", handler.Request.Headers.Authorization?.Parameter);
    }

    private static FullWorthCloudClient CreateClient(HttpMessageHandler handler, string? enrollmentToken)
    {
        var settings = new Dictionary<string, string?>
        {
            // Development/Testing is the only place a non-HTTPS base URL override is honored.
            ["FullWorthCloud:BaseUrl"] = "http://cloud.test"
        };
        if (enrollmentToken is not null)
            settings["FullWorthCloud:EnrollmentToken"] = enrollmentToken;

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return new FullWorthCloudClient(new HttpClient(handler), configuration, new TestEnvironment());
    }

    private sealed class CapturingHandler(Guid instanceId) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            var body = JsonSerializer.Serialize(new
            {
                instanceId,
                credential = "instance-secret",
                credentialExpiresAt = (DateTimeOffset?)null,
                entitlementStatus = "active"
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
