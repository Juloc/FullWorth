using FullWorth.Banking.Backend;
using FullWorth.Banking.Services;

namespace FullWorth.Banking.Tests;

/// <summary>
/// A FinTS sync that hits a TAN parks the challenge on the connection and sets its status to
/// <c>TAN_REQUIRED</c>. Everything after that pointed the user the wrong way: the manual sync answered
/// "reconnect needed" (because a TAN-pending connection is not authorized), and reconnecting starts a fresh
/// authorization that throws the pending challenge away. So the one thing the bank was waiting for could
/// never be delivered.
/// </summary>
public sealed class FinTsTanFlowTests
{
    private static readonly BankingCaller Caller = new(Guid.NewGuid(), Guid.NewGuid());

    [Fact]
    public async Task A_connection_waiting_for_a_tan_reports_that_instead_of_reconnect_needed()
    {
        using var environment = new TestBankingEnvironment();
        var backend = new FakeBackendHandler();
        var connection = TanPendingConnection();
        backend.Connections.Add(connection);
        var provider = NeverCalledProvider();
        var service = environment.CreateSyncService(provider, backend);

        var result = await service.RequestManualSyncAsync(connection.Id, Caller, force: true, CancellationToken.None);

        Assert.Equal(ManualSyncStatus.TanRequired, result.Status);
        // Nothing was contacted and nothing was rewritten: the parked challenge survives.
        Assert.Empty(provider.Requests);
        Assert.Empty(backend.Upserts);
    }

    [Fact]
    public async Task The_error_code_alone_is_enough_to_recognise_it()
    {
        using var environment = new TestBankingEnvironment();
        var backend = new FakeBackendHandler();
        var connection = TanPendingConnection(status: "AUTHORIZED", lastError: "FINTS_TAN_REQUIRED");
        backend.Connections.Add(connection);
        var service = environment.CreateSyncService(NeverCalledProvider(), backend);

        var result = await service.RequestManualSyncAsync(connection.Id, Caller, force: true, CancellationToken.None);

        Assert.Equal(ManualSyncStatus.TanRequired, result.Status);
    }

    // An ordinary expired consent must still be reported as reconnect-needed - the TAN check is narrow.
    [Fact]
    public async Task An_expired_consent_is_still_a_reconnect()
    {
        using var environment = new TestBankingEnvironment();
        var backend = new FakeBackendHandler();
        var connection = new BankConnectionDto(
            Guid.NewGuid(), "enable-banking", "Test Bank", "DE", null, null, "session-1",
            "AUTHORIZED", DateTimeOffset.UtcNow.AddDays(-1), null, null, null, 0, null);
        backend.Connections.Add(connection);
        var service = environment.CreateSyncService(NeverCalledProvider(), backend);

        var result = await service.RequestManualSyncAsync(connection.Id, Caller, force: true, CancellationToken.None);

        Assert.Equal(ManualSyncStatus.ReauthorizationRequired, result.Status);
    }

    private static BankConnectionDto TanPendingConnection(
        string status = "TAN_REQUIRED", string? lastError = "FINTS_TAN_REQUIRED") => new(
            Guid.NewGuid(),
            "fints",
            "ING",
            "DE",
            null,
            null,
            "fints-secret",
            status,
            DateTimeOffset.UtcNow.AddDays(30),
            DateTimeOffset.UtcNow.AddMinutes(-5),
            null,
            null,
            0,
            lastError);

    private static RecordingHttpMessageHandler NeverCalledProvider() =>
        new((request, _, _) => throw new Xunit.Sdk.XunitException($"Unexpected provider request: {request.RequestUri}"));
}
