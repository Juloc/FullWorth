using System.Net;
using FullWorth.Banking.Backend;
using FullWorth.Banking.Services;

namespace FullWorth.Banking.Tests;

public sealed class TransactionDetailsTests
{
    private static readonly BankingCaller Caller = new(Guid.NewGuid(), Guid.NewGuid());

    [Fact]
    public async Task ProviderConsentRevokedMarksConnectionAndRequiresReauthorization()
    {
        using var environment = new TestBankingEnvironment();
        var backend = new FakeBackendHandler();
        var connection = TestBankingEnvironment.AuthorizedConnection();
        backend.Connections.Add(connection);
        backend.ProviderPointer = new TransactionProviderPointer(
            connection.Id,
            "account-1",
            "provider-tx");

        var provider = new RecordingHttpMessageHandler((request, _, _) =>
        {
            Assert.Equal(
                "/accounts/account-1/transactions/provider-tx",
                request.RequestUri!.AbsolutePath);
            return Task.FromResult(TestBankingEnvironment.JsonResponse(
                "{\"error_code\":\"SESSION_REVOKED\"}",
                HttpStatusCode.Unauthorized));
        });
        var service = environment.CreateSyncService(provider, backend);

        await Assert.ThrowsAsync<BankReauthorizationRequiredException>(() =>
            service.GetTransactionDetailsAsync(
                Guid.NewGuid(),
                Caller,
                psuContext: null,
                CancellationToken.None));

        var final = backend.Connections.Single();
        Assert.Equal("REVOKED", final.Status);
        Assert.Equal("SESSION_REVOKED", final.LastError);
        Assert.Null(final.NextSyncAllowedAt);
        Assert.Single(provider.Requests);
    }

    [Fact]
    public async Task MissingSingleTransactionDetailDoesNotMarkConnectionFailed()
    {
        using var environment = new TestBankingEnvironment();
        var backend = new FakeBackendHandler();
        var connection = TestBankingEnvironment.AuthorizedConnection();
        backend.Connections.Add(connection);
        backend.ProviderPointer = new TransactionProviderPointer(
            connection.Id,
            "account-1",
            "missing-tx");

        var provider = new RecordingHttpMessageHandler((request, _, _) =>
            Task.FromResult(TestBankingEnvironment.JsonResponse(
                "{\"error_code\":\"TRANSACTION_NOT_FOUND\"}",
                HttpStatusCode.NotFound)));
        var service = environment.CreateSyncService(provider, backend);

        await Assert.ThrowsAsync<FullWorth.Banking.EnableBanking.EnableBankingApiException>(() =>
            service.GetTransactionDetailsAsync(
                Guid.NewGuid(),
                Caller,
                null,
                CancellationToken.None));

        var final = backend.Connections.Single();
        Assert.Equal("AUTHORIZED", final.Status);
        Assert.Null(final.LastError);
        Assert.Equal(0, final.ConsecutiveFailures);
        Assert.Null(final.NextSyncAllowedAt);
        Assert.Single(provider.Requests);
    }

    [Fact]
    public async Task ExpiredLocalConsentRequiresReauthorizationWithoutProviderCall()
    {
        using var environment = new TestBankingEnvironment();
        var backend = new FakeBackendHandler();
        var connection = TestBankingEnvironment.AuthorizedConnection() with
        {
            ValidUntil = DateTimeOffset.UtcNow.AddMinutes(-1)
        };
        backend.Connections.Add(connection);
        backend.ProviderPointer = new TransactionProviderPointer(
            connection.Id,
            "account-1",
            "provider-tx");

        var provider = new RecordingHttpMessageHandler((request, _, _) =>
            throw new Xunit.Sdk.XunitException(
                $"Provider must not be called: {request.RequestUri}"));
        var service = environment.CreateSyncService(provider, backend);

        await Assert.ThrowsAsync<BankReauthorizationRequiredException>(() =>
            service.GetTransactionDetailsAsync(
                Guid.NewGuid(),
                Caller,
                psuContext: null,
                CancellationToken.None));

        Assert.Empty(provider.Requests);
    }
}
