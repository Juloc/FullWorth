using System.Net;
using System.Net.Http.Json;
using FullWorth.Banking.Backend;
using FullWorth.Banking.EnableBanking;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FullWorth.Banking.Tests;

public sealed class EnableBankingControlPanelStatusFallbackTests
{
    private static readonly Guid UserId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    [Fact]
    public async Task RejectedHostedCallbackFallsBackToOfficialCliLoopback()
    {
        var backendHandler = new BackendHandler(Profile());
        var starts = 0;
        var controlPanel = new RecordingHttpMessageHandler((request, _, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/relyingparty/getOobConfirmationCode")
            {
                starts++;
                if (starts == 1)
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
                    {
                        Content = JsonContent.Create(new
                        {
                            error = new { message = "UNAUTHORIZED_CONTINUE_URI" }
                        })
                    });
                }

                return Task.FromResult(TestBankingEnvironment.JsonResponse("{}"));
            }

            return request.RequestUri.AbsolutePath switch
            {
                "/api/relyingparty/emailLinkSignin" =>
                    Task.FromResult(TestBankingEnvironment.JsonResponse(
                        """{"idToken":"temporary-id","refreshToken":"loopback-refresh","expiresIn":"3600"}""")),
                _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))
            };
        });

        using var backendHttp = new HttpClient(backendHandler)
        {
            BaseAddress = new Uri("https://backend.test/")
        };
        using var controlPanelHttp = new HttpClient(controlPanel)
        {
            BaseAddress = new Uri("https://enablebanking.test/")
        };

        var service = new EnableBankingControlPanelStatusService(
            new NamedFactory(controlPanelHttp),
            Backend(backendHttp),
            Options.Create(new EnableBankingOptions
            {
                ControlPanelBaseUrl = "https://enablebanking.test",
                RedirectUrl = "https://finance.test/connect/enable-banking/callback"
            }),
            NullLogger<EnableBankingControlPanelStatusService>.Instance);

        var started = await service.StartConnectionAsync(
            UserId,
            new EnableBankingProviderStatusConnectRequest("owner@example.test"),
            CancellationToken.None);

        Assert.True(started.ManualCompletionRequired);
        Assert.Equal("http://localhost:8888/", started.SetupCallbackUrl);

        var completed = await service.CompleteConnectionManuallyAsync(
            UserId,
            new EnableBankingProviderStatusConnectCompleteRequest(
                started.Id,
                "http://localhost:8888/?mode=signIn&oobCode=email-oob-code"),
            CancellationToken.None);

        Assert.True(completed.Success);
        Assert.Equal("loopback-refresh", backendHandler.LastWrite!.ControlPanelRefreshToken);

        var requests = controlPanel.Requests.ToArray();
        Assert.Equal(3, requests.Length);

        using var first = System.Text.Json.JsonDocument.Parse(requests[0].Body!);
        Assert.Contains(
            "/connect/enable-banking/status-callback?state=",
            first.RootElement.GetProperty("continueUrl").GetString(),
            StringComparison.Ordinal);

        using var second = System.Text.Json.JsonDocument.Parse(requests[1].Body!);
        Assert.Equal(
            "http://localhost:8888/",
            second.RootElement.GetProperty("continueUrl").GetString());
    }

    private static EnableBankingProfileDto Profile() => new(
        Guid.NewGuid(),
        UserId,
        "app-12345678",
        "test-key",
        "fingerprint",
        "PRODUCTION",
        "FullWorth",
        true,
        ["AIS"],
        ["https://finance.test/connect/enable-banking/callback"],
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow,
        null);

    private static FullWorthBackendClient Backend(HttpClient http) => new(
        http,
        Options.Create(new BackendOptions
        {
            BaseUrl = "https://backend.test",
            IngestKey = "test-ingest-key"
        }));

    private sealed class NamedFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            Assert.Equal("enable-banking-control-panel", name);
            return client;
        }
    }

    private sealed class BackendHandler(EnableBankingProfileDto profile) : HttpMessageHandler
    {
        private EnableBankingProfileDto _profile = profile;
        public EnableBankingProfileWrite? LastWrite { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get &&
                path == $"/internal/banking/profiles/users/{UserId:D}")
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(_profile) };

            if (request.Method == HttpMethod.Post && path == "/internal/banking/profiles/")
            {
                LastWrite = await request.Content!.ReadFromJsonAsync<EnableBankingProfileWrite>(
                    cancellationToken: cancellationToken)
                    ?? throw new InvalidOperationException("Missing profile write.");

                _profile = _profile with
                {
                    ControlPanelRefreshToken = LastWrite.ControlPanelRefreshToken,
                    UpdatedAt = DateTimeOffset.UtcNow
                };
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(_profile) };
            }

            return new(HttpStatusCode.NotFound);
        }
    }
}
