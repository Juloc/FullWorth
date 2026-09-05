using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FullWorth.Banking.Backend;
using FullWorth.Banking.EnableBanking;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FullWorth.Banking.Tests;

public sealed class EnableBankingControlPanelRegistrationServiceTests
{
    private const string ApplicationId = "11111111-2222-3333-4444-555555555555";
    private const string Callback = "https://finance.test/connect/enable-banking/callback";

    [Fact]
    public async Task ProductionRegistrationUsesControlPanelApiAndPersistsVerifiedGeneratedKey()
    {
        var controlPanel = new RecordingHttpMessageHandler((request, _, _) =>
        {
            return request.RequestUri!.AbsolutePath switch
            {
                "/api/relyingparty/getOobConfirmationCode" =>
                    Task.FromResult(TestBankingEnvironment.JsonResponse("{}")),
                "/api/relyingparty/emailLinkSignin" =>
                    Task.FromResult(TestBankingEnvironment.JsonResponse(
                        """{"idToken":"one-time-id-token","refreshToken":"one-time-refresh-token","expiresIn":"3600"}""")),
                "/api/applications" =>
                    Task.FromResult(TestBankingEnvironment.JsonResponse(
                        $$"""{"app_id":"{{ApplicationId}}"}""")),
                _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))
            };
        });

        var provider = new RecordingHttpMessageHandler((request, _, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/application", request.RequestUri!.AbsolutePath);
            return Task.FromResult(TestBankingEnvironment.JsonResponse($$"""
                {
                  "name":"FullWorth",
                  "kid":"{{ApplicationId}}",
                  "environment":"PRODUCTION",
                  "redirect_urls":["{{Callback}}"],
                  "active":false,
                  "services":["AIS"]
                }
                """));
        });

        var backendHandler = new RegistrationBackendHandler();
        using var controlPanelHttp = new HttpClient(controlPanel) { BaseAddress = new Uri("https://enablebanking.test/") };
        using var providerHttp = new HttpClient(provider) { BaseAddress = new Uri("https://provider.test/") };
        using var backendHttp = new HttpClient(backendHandler) { BaseAddress = new Uri("https://backend.test/") };

        var options = Options.Create(new EnableBankingOptions
        {
            BaseUrl = "https://provider.test",
            ControlPanelBaseUrl = "https://enablebanking.test",
            RedirectUrl = Callback,
            PrivacyUrl = "https://fullworth.de/privacy/",
            TermsUrl = "https://fullworth.de/terms/",
            ApplicationName = "FullWorth",
            ApplicationDescription = "Private finance web app",
            MinimumRequestSpacingMilliseconds = 250
        });

        var factory = new NamedClientFactory(controlPanelHttp, providerHttp);
        var backend = new FullWorthBackendClient(
            backendHttp,
            Options.Create(new BackendOptions
            {
                BaseUrl = "https://backend.test",
                IngestKey = "test-ingest-key"
            }));

        var services = new ServiceCollection();
        services.AddSingleton<IHttpClientFactory>(factory);
        services.AddSingleton(options);
        services.AddSingleton(new EnableBankingRequestPolicy());
        services.AddSingleton(backend);
        services.AddScoped<EnableBankingClientResolver>();
        services.AddScoped<EnableBankingProfileService>();
        using var providerServices = services.BuildServiceProvider();

        var registration = new EnableBankingControlPanelRegistrationService(
            factory,
            providerServices.GetRequiredService<IServiceScopeFactory>(),
            options,
            NullLogger<EnableBankingControlPanelRegistrationService>.Instance);

        var userId = Guid.NewGuid();
        var started = await registration.StartAsync(
            userId,
            new EnableBankingAutoRegistrationRequest("owner@example.test", "PRODUCTION"),
            CancellationToken.None);

        Assert.Equal("waiting_for_email", started.Status);
        Assert.Contains("/connect/enable-banking/setup-callback?state=", started.SetupCallbackUrl, StringComparison.Ordinal);
        Assert.Equal("https://fullworth.de/privacy/", started.PrivacyUrl);
        Assert.Equal("https://fullworth.de/terms/", started.TermsUrl);

        var completed = await registration.CompleteAsync(started.Id, "email-oob-code", CancellationToken.None);

        Assert.True(completed.Success);
        Assert.Equal("completed", completed.Status);
        Assert.NotNull(backendHandler.LastWrite);
        Assert.Equal(userId, backendHandler.LastWrite!.UserId);
        Assert.Equal(ApplicationId, backendHandler.LastWrite.ApplicationId);
        Assert.Contains("BEGIN PRIVATE KEY", backendHandler.LastWrite.PrivateKeyPem, StringComparison.Ordinal);
        Assert.Equal("PRODUCTION", backendHandler.LastWrite.Environment);
        Assert.False(backendHandler.LastWrite.Active);
        Assert.Equal("one-time-refresh-token", backendHandler.LastWrite.ControlPanelRefreshToken);

        var requests = controlPanel.Requests.ToArray();
        Assert.Equal(3, requests.Length);

        using var loginStart = JsonDocument.Parse(requests[0].Body!);
        Assert.Equal("EMAIL_SIGNIN", loginStart.RootElement.GetProperty("requestType").GetString());
        Assert.Equal("owner@example.test", loginStart.RootElement.GetProperty("email").GetString());
        Assert.Equal(started.SetupCallbackUrl, loginStart.RootElement.GetProperty("continueUrl").GetString());
        Assert.True(loginStart.RootElement.GetProperty("canHandleCodeInApp").GetBoolean());

        using var application = JsonDocument.Parse(requests[2].Body!);
        Assert.Equal("FullWorth", application.RootElement.GetProperty("name").GetString());
        Assert.Equal("PRODUCTION", application.RootElement.GetProperty("environment").GetString());
        Assert.Equal(Callback, application.RootElement.GetProperty("redirect_urls")[0].GetString());
        Assert.Equal("Private finance web app", application.RootElement.GetProperty("description").GetString());
        Assert.Equal("owner@example.test", application.RootElement.GetProperty("gdpr_email").GetString());
        Assert.Equal("https://fullworth.de/privacy/", application.RootElement.GetProperty("privacy_url").GetString());
        Assert.Equal("https://fullworth.de/terms/", application.RootElement.GetProperty("terms_url").GetString());
        Assert.Contains("BEGIN PUBLIC KEY", application.RootElement.GetProperty("certificate").GetString(), StringComparison.Ordinal);

        Assert.True(requests[2].Headers!.TryGetValue("Authorization", out var authorization));
        Assert.Equal("Bearer one-time-id-token", authorization);
        Assert.Single(provider.Requests);
    }

    private sealed class NamedClientFactory(HttpClient controlPanel, HttpClient provider) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => name switch
        {
            "enable-banking-control-panel" => controlPanel,
            "enable-banking" => provider,
            _ => throw new InvalidOperationException($"Unexpected HTTP client '{name}'.")
        };
    }

    private sealed class RegistrationBackendHandler : HttpMessageHandler
    {
        public EnableBankingProfileWrite? LastWrite { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;

            if (request.Method == HttpMethod.Get &&
                path.StartsWith("/internal/banking/profiles/users/", StringComparison.Ordinal))
                return new(HttpStatusCode.NotFound);

            if (request.Method == HttpMethod.Post && path == "/internal/banking/profiles/")
            {
                LastWrite = await request.Content!.ReadFromJsonAsync<EnableBankingProfileWrite>(
                    cancellationToken: cancellationToken)
                    ?? throw new InvalidOperationException("Missing profile payload.");

                var dto = new EnableBankingProfileDto(
                    Guid.NewGuid(),
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
                    DateTimeOffset.UtcNow);

                return new(HttpStatusCode.OK) { Content = JsonContent.Create(dto) };
            }

            return new(HttpStatusCode.NotFound);
        }
    }
}
