using System.Net;
using System.Text;
using System.Text.Json;
using FullWorth.Backend.Modules.Intelligence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace FullWorth.Backend.Tests.Intelligence;

/// <summary>
/// This instance tells the Cloud which wire protocol it speaks, and stops asking when the answer is
/// "older than we serve".
///
/// The client sent no protocol version at all, so the Cloud recorded the default for every instance and
/// the field was useless for the one thing it exists for: a Cloud that stops serving an old protocol
/// could not tell an outdated instance from a current one, and could therefore only refuse everybody or
/// nobody. Meanwhile an outdated instance would register successfully and then fail on every endpoint
/// whose shape had moved — retrying, because a request that looks malformed is what a client retries.
/// </summary>
public sealed class CloudProtocolCompatibilityTests
{
    [Fact]
    public async Task Registration_declares_the_wire_protocol_this_build_speaks()
    {
        var instanceId = Guid.NewGuid();
        var handler = new CapturingHandler(instanceId);
        var client = CreateClient(handler);

        await client.RegisterAsync(instanceId, "policy-1", "1.0.0", null, CancellationToken.None);

        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal(
            CloudIntelligencePolicy.WireProtocolVersion,
            body.RootElement.GetProperty("protocolVersion").GetString());
    }

    /// <summary>
    /// The Cloud answers 426 with a machine-readable code and a sentence for the operator. Both have to
    /// survive: the code is what the rest of this instance branches on, the sentence is the only thing
    /// that tells a person what to actually do.
    /// </summary>
    [Fact]
    public async Task A_cloud_that_refuses_this_build_is_reported_as_such_not_as_a_bad_request()
    {
        var handler = new RefusingHandler();
        var client = CreateClient(handler);

        var error = await Assert.ThrowsAsync<FullWorthCloudException>(
            () => client.RegisterAsync(Guid.NewGuid(), "policy-1", "1.0.0", null, CancellationToken.None));

        Assert.Equal(FullWorthCloudClient.ClientTooOldErrorCode, error.ErrorCode);
        Assert.Equal(HttpStatusCode.UpgradeRequired, error.StatusCode);
        Assert.Contains("Update FullWorth", error.Remediation);

        // Not transient: waiting changes nothing about an outdated build, and a transient error is
        // exactly the thing every retry loop in this module is built to keep trying.
        Assert.False(error.Transient);
    }

    /// <summary>
    /// And the back-off matches. A minute is right for a Cloud that is down, because it comes back; for
    /// "your build is too old" it means every instance of an outdated fleet asks again every minute,
    /// forever, and can only ever be refused again.
    /// </summary>
    [Fact]
    public void A_failure_that_waiting_cannot_fix_backs_off_far_longer_than_an_outage()
    {
        Assert.True(
            CloudCredentialAcquisition.TerminalCooldown > CloudCredentialAcquisition.Cooldown * 60,
            "An outdated build must not retry on the same cadence as a temporary outage.");
    }

    private static FullWorthCloudClient CreateClient(HttpMessageHandler handler)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FullWorthCloud:BaseUrl"] = "http://cloud.test"
            })
            .Build();
        return new FullWorthCloudClient(new HttpClient(handler), configuration, new TestEnvironment());
    }

    private sealed class CapturingHandler(Guid instanceId) : HttpMessageHandler
    {
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Body = await request.Content!.ReadAsStringAsync(ct);
            var body = JsonSerializer.Serialize(new
            {
                instanceId,
                credential = "instance-secret",
                credentialExpiresAt = (DateTimeOffset?)null,
                entitlementStatus = "active"
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }
    }

    /// <summary>Mirrors the Cloud's error contract for <c>client_too_old</c>.</summary>
    private sealed class RefusingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = JsonSerializer.Serialize(new
            {
                errorCode = "client_too_old",
                message = (string?)null,
                remediation = "This FullWorth instance is older than this Cloud supports. Update FullWorth, " +
                              "then register again. Local finance features are unaffected - the Cloud is optional."
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.UpgradeRequired)
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
