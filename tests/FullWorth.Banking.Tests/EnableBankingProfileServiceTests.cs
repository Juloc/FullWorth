using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using FullWorth.Banking.Backend;
using FullWorth.Banking.EnableBanking;
using Microsoft.Extensions.Options;

namespace FullWorth.Banking.Tests;

public sealed class EnableBankingProfileServiceTests
{
    private const string ApplicationId = "11111111-2222-3333-4444-555555555555";
    private const string Callback = "https://finance.test/connect/enable-banking/callback";

    [Fact]
    public async Task ValidSandboxApplicationIsVerifiedAndSaved()
    {
        using var fixture = new Fixture($$"""
            {
              "name":"FullWorth Test",
              "kid":"{{ApplicationId}}",
              "environment":"SANDBOX",
              "redirect_urls":["{{Callback}}"],
              "active":true,
              "services":["AIS"]
            }
            """);

        var result = await fixture.Service.VerifyAndSaveAsync(
            Guid.NewGuid(),
            new EnableBankingProfileVerifyRequest(ApplicationId, fixture.PrivateKeyPem),
            CancellationToken.None);

        Assert.Equal(ApplicationId, result.ApplicationId);
        Assert.Equal("SANDBOX", result.Environment);
        Assert.True(result.Active);
        Assert.Contains("AIS", result.Services);
        Assert.Equal(1, fixture.BackendProfileWrites);
    }

    [Fact]
    public async Task PublicKeyOnlyPemIsRejectedBeforeProviderCall()
    {
        using var rsa = RSA.Create(2048);
        var publicOnlyPem = rsa.ExportSubjectPublicKeyInfoPem();
        using var fixture = new Fixture("{}");

        await Assert.ThrowsAsync<ArgumentException>(() =>
            fixture.Service.VerifyAndSaveAsync(
                Guid.NewGuid(),
                new EnableBankingProfileVerifyRequest(ApplicationId, publicOnlyPem),
                CancellationToken.None));

        Assert.Equal(0, fixture.BackendProfileWrites);
        Assert.Equal(0, fixture.ProviderRequests);
    }

    [Fact]
    public async Task KidMismatchIsRejected()
    {
        using var fixture = new Fixture($$"""
            {
              "name":"Wrong",
              "kid":"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
              "environment":"SANDBOX",
              "redirect_urls":["{{Callback}}"],
              "active":true,
              "services":["AIS"]
            }
            """);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.VerifyAndSaveAsync(
                Guid.NewGuid(),
                new EnableBankingProfileVerifyRequest(ApplicationId, fixture.PrivateKeyPem),
                CancellationToken.None));

        Assert.Equal(0, fixture.BackendProfileWrites);
    }

    [Fact]
    public async Task MissingAisServiceIsRejected()
    {
        using var fixture = new Fixture($$"""
            {
              "name":"PIS only",
              "kid":"{{ApplicationId}}",
              "environment":"SANDBOX",
              "redirect_urls":["{{Callback}}"],
              "active":true,
              "services":["PIS"]
            }
            """);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.VerifyAndSaveAsync(
                Guid.NewGuid(),
                new EnableBankingProfileVerifyRequest(ApplicationId, fixture.PrivateKeyPem),
                CancellationToken.None));

        Assert.Equal(0, fixture.BackendProfileWrites);
    }

    [Fact]
    public async Task RedirectMismatchIsRejected()
    {
        using var fixture = new Fixture($$"""
            {
              "name":"Wrong redirect",
              "kid":"{{ApplicationId}}",
              "environment":"SANDBOX",
              "redirect_urls":["https://other.example/callback"],
              "active":true,
              "services":["AIS"]
            }
            """);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.VerifyAndSaveAsync(
                Guid.NewGuid(),
                new EnableBankingProfileVerifyRequest(ApplicationId, fixture.PrivateKeyPem),
                CancellationToken.None));

        Assert.Equal(0, fixture.BackendProfileWrites);
    }

    [Fact]
    public async Task InactiveProductionApplicationIsStoredAsNotReadyForActivationUi()
    {
        using var fixture = new Fixture($$"""
            {
              "name":"Private Production",
              "kid":"{{ApplicationId}}",
              "environment":"PRODUCTION",
              "redirect_urls":["{{Callback}}"],
              "active":false,
              "services":["AIS"]
            }
            """);

        var result = await fixture.Service.VerifyAndSaveAsync(
            Guid.NewGuid(),
            new EnableBankingProfileVerifyRequest(ApplicationId, fixture.PrivateKeyPem),
            CancellationToken.None);

        Assert.Equal("PRODUCTION", result.Environment);
        Assert.False(result.Active);
        Assert.Equal(1, fixture.BackendProfileWrites);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly HttpClient _providerHttp;
        private readonly HttpClient _backendHttp;
        private int _backendProfileWrites;
        private RecordingHttpMessageHandler? _providerHandler;

        public Fixture(string applicationJson)
        {
            using var rsa = RSA.Create(2048);
            PrivateKeyPem = rsa.ExportPkcs8PrivateKeyPem();

            var providerHandler = new RecordingHttpMessageHandler((request, _, _) =>
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Equal("/application", request.RequestUri!.AbsolutePath);
                return Task.FromResult(TestBankingEnvironment.JsonResponse(applicationJson));
            });
            _providerHandler = providerHandler;
            _providerHttp = new HttpClient(providerHandler) { BaseAddress = new Uri("https://provider.test/") };

            var backendHandler = new ProfileBackendHandler(() => Interlocked.Increment(ref _backendProfileWrites));
            _backendHttp = new HttpClient(backendHandler) { BaseAddress = new Uri("https://backend.test/") };
            var backend = new FullWorthBackendClient(
                _backendHttp,
                Options.Create(new BackendOptions
                {
                    BaseUrl = "https://backend.test",
                    IngestKey = "test-ingest-key"
                }));

            var options = Options.Create(new EnableBankingOptions
            {
                BaseUrl = "https://provider.test",
                RedirectUrl = Callback,
                MinimumRequestSpacingMilliseconds = 250
            });
            var resolver = new EnableBankingClientResolver(
                new SingleClientFactory(_providerHttp),
                options,
                new EnableBankingRequestPolicy(),
                backend);

            Service = new EnableBankingProfileService(resolver, backend, options);
        }

        public string PrivateKeyPem { get; }
        public EnableBankingProfileService Service { get; }
        public int BackendProfileWrites => _backendProfileWrites;
        public int ProviderRequests => _providerHandler?.Requests.Count ?? 0;

        public void Dispose()
        {
            _providerHttp.Dispose();
            _backendHttp.Dispose();
        }
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class ProfileBackendHandler(Action onWrite) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method != HttpMethod.Post ||
                request.RequestUri!.AbsolutePath != "/internal/banking/profiles/")
                return new(HttpStatusCode.NotFound);

            var body = await request.Content!.ReadFromJsonAsync<EnableBankingProfileWrite>(
                cancellationToken: cancellationToken)
                ?? throw new InvalidOperationException("Missing profile body.");
            onWrite();

            var dto = new EnableBankingProfileDto(
                Guid.NewGuid(),
                body.UserId,
                body.ApplicationId,
                body.PrivateKeyPem,
                body.KeyFingerprint,
                body.Environment,
                body.ApplicationName,
                body.Active,
                body.Services,
                body.RedirectUrls,
                body.VerifiedAt,
                DateTimeOffset.UtcNow);

            return new(HttpStatusCode.OK) { Content = JsonContent.Create(dto) };
        }
    }
}
