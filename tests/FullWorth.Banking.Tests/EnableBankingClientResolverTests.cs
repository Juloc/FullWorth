using System.Net;
using System.Net.Http.Json;
using FullWorth.Banking.Backend;
using FullWorth.Banking.EnableBanking;
using Microsoft.Extensions.Options;

namespace FullWorth.Banking.Tests;

public sealed class EnableBankingClientResolverTests
{
    [Fact]
    public async Task ExistingConnectionRejectsProfileOwnedByDifferentAuthorizationUser()
    {
        var ownerId = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        var profileId = Guid.NewGuid();

        var backendHandler = new ResolverBackendHandler(new EnableBankingProfileDto(
            profileId,
            otherId,
            "application-id",
            "private-key",
            "fingerprint",
            "PRODUCTION",
            "Other user app",
            true,
            ["AIS"],
            ["https://finance.test/connect/enable-banking/callback"],
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow));
        using var backendHttp = new HttpClient(backendHandler) { BaseAddress = new Uri("https://backend.test/") };
        var backend = new FullWorthBackendClient(
            backendHttp,
            Options.Create(new BackendOptions
            {
                BaseUrl = "https://backend.test",
                IngestKey = "test-ingest-key"
            }));

        using var providerHttp = new HttpClient(new NeverProviderHandler())
        {
            BaseAddress = new Uri("https://provider.test/")
        };
        var options = Options.Create(new EnableBankingOptions
        {
            BaseUrl = "https://provider.test",
            RedirectUrl = "https://finance.test/connect/enable-banking/callback"
        });
        var resolver = new EnableBankingClientResolver(
            new SingleClientFactory(providerHttp),
            options,
            new EnableBankingRequestPolicy(),
            backend);

        var connection = new BankConnectionDto(
            Guid.NewGuid(),
            "enable-banking",
            "Test Bank",
            "DE",
            null,
            null,
            "session-1",
            "AUTHORIZED",
            DateTimeOffset.UtcNow.AddDays(30),
            null,
            null,
            null,
            0,
            null,
            EnableBankingProfileId: profileId,
            AuthorizationUserId: ownerId);

        await Assert.ThrowsAsync<EnableBankingProfileNotConfiguredException>(() =>
            resolver.ResolveForConnectionAsync(connection, CancellationToken.None));
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class ResolverBackendHandler(EnableBankingProfileDto profile) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get &&
                request.RequestUri!.AbsolutePath == $"/internal/banking/profiles/{profile.Id:D}")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(profile)
                });

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class NeverProviderHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new Xunit.Sdk.XunitException($"Provider must not be called: {request.RequestUri}");
    }
}
