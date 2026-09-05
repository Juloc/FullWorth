using System.Net;
using System.Net.Http.Json;
using FullWorth.Banking.Backend;
using FullWorth.Banking.EnableBanking;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FullWorth.Banking.Tests;

public sealed class EnableBankingControlPanelStatusServiceTests
{
    private static readonly Guid UserId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    private const string Callback = "https://finance.test/connect/enable-banking/callback";

    [Fact]
    public async Task TodayStatsUsesOfficialControlPanelFeedAndPersistsRotatedRefreshToken()
    {
        var backendHandler = new StatusBackendHandler(Profile("stored-refresh"));
        var controlPanel = new RecordingHttpMessageHandler((request, _, _) =>
        {
            return request.RequestUri!.AbsolutePath switch
            {
                "/api/token" => Task.FromResult(TestBankingEnvironment.JsonResponse(
                    """{"id_token":"status-id-token","refresh_token":"rotated-refresh","expires_in":3600}""")),
                "/api/get_today_stats" => Task.FromResult(TestBankingEnvironment.JsonResponse(
                    """
                    [
                      {"country":"DE","brand":"C24","psu_type":"personal","status":"major disruption"},
                      {"country":"DE","brand":"DKB","psu_type":"personal","status":"no problems detected"},
                      {"country":"FI","brand":"Other","psu_type":"personal","status":"possible problems"}
                    ]
                    """)),
                _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))
            };
        });

        using var backendHttp = new HttpClient(backendHandler) { BaseAddress = new Uri("https://backend.test/") };
        using var controlPanelHttp = new HttpClient(controlPanel) { BaseAddress = new Uri("https://enablebanking.test/") };
        var backend = Backend(backendHttp);
        var service = new EnableBankingControlPanelStatusService(
            new SingleNamedClientFactory(controlPanelHttp),
            backend,
            BankingOptions(),
            NullLogger<EnableBankingControlPanelStatusService>.Instance);

        var result = await service.GetTodayAsync(UserId, "DE", CancellationToken.None);

        Assert.True(result.Available);
        Assert.Equal(2, result.Statuses.Count);
        Assert.Contains(result.Statuses, x => x.Brand == "C24" && x.Status == "major disruption");
        Assert.Contains(result.Statuses, x => x.Brand == "DKB" && x.Status == "no problems detected");
        Assert.NotNull(backendHandler.LastWrite);
        Assert.Equal("rotated-refresh", backendHandler.LastWrite!.ControlPanelRefreshToken);

        var requests = controlPanel.Requests.ToArray();
        Assert.Equal(2, requests.Length);
        Assert.Equal("/api/token", requests[0].Uri!.AbsolutePath);
        Assert.Equal("/api/get_today_stats", requests[1].Uri!.AbsolutePath);
        Assert.True(requests[1].Headers!.TryGetValue("Authorization", out var authorization));
        Assert.Equal("Bearer status-id-token", authorization);
    }

    [Fact]
    public async Task ExistingManualProfileCanConnectStatusFeedWithoutRecreatingApplication()
    {
        var backendHandler = new StatusBackendHandler(Profile(null));
        var controlPanel = new RecordingHttpMessageHandler((request, _, _) =>
        {
            return request.RequestUri!.AbsolutePath switch
            {
                "/api/relyingparty/getOobConfirmationCode" =>
                    Task.FromResult(TestBankingEnvironment.JsonResponse("{}")),
                "/api/relyingparty/emailLinkSignin" =>
                    Task.FromResult(TestBankingEnvironment.JsonResponse(
                        """{"idToken":"temporary-id","refreshToken":"status-refresh","expiresIn":"3600"}""")),
                _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))
            };
        });

        using var backendHttp = new HttpClient(backendHandler) { BaseAddress = new Uri("https://backend.test/") };
        using var controlPanelHttp = new HttpClient(controlPanel) { BaseAddress = new Uri("https://enablebanking.test/") };
        var service = new EnableBankingControlPanelStatusService(
            new SingleNamedClientFactory(controlPanelHttp),
            Backend(backendHttp),
            BankingOptions(),
            NullLogger<EnableBankingControlPanelStatusService>.Instance);

        var started = await service.StartConnectionAsync(
            UserId,
            new EnableBankingProviderStatusConnectRequest("owner@example.test"),
            CancellationToken.None);

        Assert.Equal("waiting_for_email", started.Status);
        Assert.Contains("/connect/enable-banking/status-callback?state=", started.SetupCallbackUrl, StringComparison.Ordinal);

        var completed = await service.CompleteConnectionAsync(
            started.Id,
            "email-oob-code",
            CancellationToken.None);

        Assert.True(completed.Success);
        Assert.Equal("completed", completed.Status);
        Assert.NotNull(backendHandler.LastWrite);
        Assert.Equal("status-refresh", backendHandler.LastWrite!.ControlPanelRefreshToken);
        Assert.Equal("app-12345678", backendHandler.LastWrite.ApplicationId);

        var requests = controlPanel.Requests.ToArray();
        Assert.Equal(2, requests.Length);
        using var startJson = System.Text.Json.JsonDocument.Parse(requests[0].Body!);
        Assert.Equal("owner@example.test", startJson.RootElement.GetProperty("email").GetString());
        Assert.Equal(started.SetupCallbackUrl, startJson.RootElement.GetProperty("continueUrl").GetString());
    }

    private static EnableBankingProfileDto Profile(string? refreshToken) => new(
        Guid.Parse("11111111-2222-3333-4444-555555555555"),
        UserId,
        "app-12345678",
        "-----BEGIN PRIVATE KEY-----\ntest\n-----END PRIVATE KEY-----",
        "fingerprint",
        "PRODUCTION",
        "FullWorth",
        true,
        ["AIS"],
        [Callback],
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow,
        refreshToken);

    private static FullWorthBackendClient Backend(HttpClient http) => new(
        http,
        Options.Create(new BackendOptions
        {
            BaseUrl = "https://backend.test",
            IngestKey = "test-ingest-key"
        }));

    private static IOptions<EnableBankingOptions> BankingOptions() =>
        Options.Create(new EnableBankingOptions
        {
            ControlPanelBaseUrl = "https://enablebanking.test",
            RedirectUrl = Callback
        });

    private sealed class SingleNamedClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            Assert.Equal("enable-banking-control-panel", name);
            return client;
        }
    }

    private sealed class StatusBackendHandler(EnableBankingProfileDto profile) : HttpMessageHandler
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

                _profile = new EnableBankingProfileDto(
                    _profile.Id,
                    LastWrite.UserId,
                    LastWrite.ApplicationId,
                    LastWrite.PrivateKeyPem,
                    LastWrite.KeyFingerprint,
                    LastWrite.Environment,
                    LastWrite.ApplicationName,
                    LastWrite.Active,
                    LastWrite.Services,
                    LastWrite.RedirectUrls,
                    LastWrite.VerifiedAt,
                    DateTimeOffset.UtcNow,
                    LastWrite.ControlPanelRefreshToken);
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(_profile) };
            }

            return new(HttpStatusCode.NotFound);
        }
    }
}
