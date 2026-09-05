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
            null,
            false,
            CancellationToken.None);

        Assert.Equal(DisconnectStatus.ClosedDataRetained, result);
        Assert.Single(provider.Requests);
        var closed = backend.Connections.Single();
        Assert.Equal("CLOSED", closed.Status);
        Assert.Null(closed.ProviderSessionId);
        Assert.Null(closed.LastError);
        Assert.Equal(0, closed.ConsecutiveFailures);
    }


    [Fact]
    public async Task ReauthorizationPersistsNewSessionBeforeClosingSupersededSession()
    {
        using var environment = new TestBankingEnvironment();
        var backend = new FakeBackendHandler();
        var connection = TestBankingEnvironment.AuthorizedConnection(sessionId: "session-old") with
        {
            AuthorizationState = "state-1",
            Status = "PENDING_AUTHORIZATION"
        };
        backend.Connections.Add(connection);

        var provider = new RecordingHttpMessageHandler((request, _, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post && path == "/sessions")
                return Task.FromResult(TestBankingEnvironment.JsonResponse(
                    "{\"session_id\":\"session-new\",\"access\":{\"valid_until\":\"2026-12-31T23:59:59Z\"}}"));
            if (request.Method == HttpMethod.Delete && path == "/sessions/session-old")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            if (request.Method == HttpMethod.Get && path == "/sessions/session-new")
                return Task.FromResult(TestBankingEnvironment.JsonResponse(
                    "{\"status\":\"AUTHORIZED\",\"accounts\":[]}"));
            throw new Xunit.Sdk.XunitException($"Unexpected provider request: {request.Method} {request.RequestUri}");
        });
        var service = environment.CreateSyncService(provider, backend);

        var result = await service.CompleteConnectionAsync(
            "state-1",
            "authorization-code",
            null,
            CancellationToken.None);

        Assert.Equal("AUTHORIZED", result.Status);
        Assert.Equal("session-new", result.ProviderSessionId);
        var requests = provider.Requests.ToArray();
        Assert.Equal(
            new[] { "/sessions", "/sessions/session-old", "/sessions/session-new" },
            requests.Select(x => x.Uri.AbsolutePath).ToArray());
        Assert.Equal("session-new", backend.Connections.Single().ProviderSessionId);
    }
}
