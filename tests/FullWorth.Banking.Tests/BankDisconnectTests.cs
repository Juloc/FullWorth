using System.Net;
using FullWorth.Banking.Services;

namespace FullWorth.Banking.Tests;

public sealed class BankDisconnectTests
{
    [Fact]
    public async Task RetainDataClosesProviderSessionAndKeepsLocalConnectionClosed()
    {
        using var environment = new TestBankingEnvironment();
        var backend = new FakeBackendHandler();
        var connection = TestBankingEnvironment.AuthorizedConnection(
            lastAttemptAt: DateTimeOffset.UtcNow.AddHours(-1),
            sessionId: "session-retain");
        backend.Connections.Add(connection);

        var provider = new RecordingHttpMessageHandler((request, _, _) =>
        {
            Assert.Equal(HttpMethod.Delete, request.Method);
            Assert.Equal("/sessions/session-retain", request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        });
        var service = environment.CreateSyncService(provider, backend);

        var result = await service.DisconnectAsync(
            connection.Id,
            new BankingCaller(Guid.NewGuid(), Guid.NewGuid()),
            psuContext: null,
            deleteLocalData: false,
            CancellationToken.None);

        Assert.Equal(DisconnectStatus.ClosedDataRetained, result);
        Assert.Single(provider.Requests);
        var closed = backend.Connections.Single();
        Assert.Equal("CLOSED", closed.Status);
        Assert.Null(closed.ProviderSessionId);
        Assert.Null(closed.LastError);
        Assert.Equal(0, closed.ConsecutiveFailures);
    }
}
