using FullWorth.Banking.Backend;
using FullWorth.Banking.Services;

namespace FullWorth.Banking.Tests;

/// <summary>
/// The scheduler reported "0 synced, 4 skipped, 0 failed" and nothing else — no way to tell which four or
/// why. Worse, the loop's pre-filter dropped every connection that actually needed attention (not
/// authorized, session gone, consent expired) BEFORE counting, so those were reported as neither synced,
/// skipped nor failed. They simply did not appear, which is how a connection can sit unsynced for weeks
/// with nothing saying so.
/// </summary>
public sealed class SyncSkipReasonTests
{
    [Fact]
    public async Task A_connection_inside_its_cadence_is_skipped_as_not_due()
    {
        var result = await RunAsync(Connection(lastAttemptAt: DateTimeOffset.UtcNow.AddMinutes(-5)));

        var skip = Assert.Single(result.Skips!);
        Assert.Equal(BankSyncSkipReasons.NotDue, skip.Reason);
        Assert.Equal(1, result.Skipped);
    }

    [Fact]
    public async Task A_rate_limited_connection_says_so_and_reports_when_to_retry()
    {
        var retryAt = DateTimeOffset.UtcNow.AddHours(3);
        var result = await RunAsync(Connection(
            lastAttemptAt: DateTimeOffset.UtcNow.AddDays(-1),
            nextSyncAllowedAt: retryAt,
            lastError: "ASPSP_RATE_LIMIT_EXCEEDED"));

        var skip = Assert.Single(result.Skips!);
        Assert.Equal(BankSyncSkipReasons.RateLimited, skip.Reason);
        Assert.Equal(retryAt, skip.RetryAt);
    }

    // This is the one that used to disappear entirely.
    [Fact]
    public async Task A_connection_that_needs_reauthorization_is_reported_instead_of_vanishing()
    {
        var result = await RunAsync(Connection(
            lastAttemptAt: DateTimeOffset.UtcNow.AddDays(-1),
            status: "EXPIRED"));

        var skip = Assert.Single(result.Skips!);
        Assert.Equal(BankSyncSkipReasons.AuthorizationRequired, skip.Reason);
        Assert.Equal(1, result.Skipped);
    }

    [Fact]
    public async Task An_expired_consent_is_reported_as_expired()
    {
        var result = await RunAsync(Connection(
            lastAttemptAt: DateTimeOffset.UtcNow.AddDays(-1),
            validUntil: DateTimeOffset.UtcNow.AddDays(-1)));

        Assert.Equal(BankSyncSkipReasons.Expired, Assert.Single(result.Skips!).Reason);
    }

    // A FinTS connection parked on a TAN must not be reported as "reconnect needed": reconnecting throws
    // the pending challenge away.
    [Fact]
    public async Task A_connection_waiting_for_a_tan_is_reported_as_tan_required()
    {
        var result = await RunAsync(Connection(
            lastAttemptAt: DateTimeOffset.UtcNow.AddDays(-1),
            provider: "fints",
            status: "TAN_REQUIRED",
            lastError: "FINTS_TAN_REQUIRED"));

        Assert.Equal(BankSyncSkipReasons.TanRequired, Assert.Single(result.Skips!).Reason);
    }

    [Fact]
    public async Task Every_connection_appears_in_exactly_one_bucket()
    {
        var result = await RunAsync(
            Connection(lastAttemptAt: DateTimeOffset.UtcNow.AddMinutes(-5)),
            Connection(lastAttemptAt: DateTimeOffset.UtcNow.AddDays(-1), status: "EXPIRED"),
            Connection(lastAttemptAt: DateTimeOffset.UtcNow.AddDays(-1), validUntil: DateTimeOffset.UtcNow.AddDays(-2)));

        Assert.Equal(3, result.Synced + result.Skipped + result.Failed);
        Assert.Equal(
            [
                BankSyncSkipReasons.AuthorizationRequired,
                BankSyncSkipReasons.Expired,
                BankSyncSkipReasons.NotDue
            ],
            result.Skips!.Select(skip => skip.Reason).Order().ToArray());
    }

    private static async Task<BankSyncResult> RunAsync(params BankConnectionDto[] connections)
    {
        using var environment = new TestBankingEnvironment();
        var backend = new FakeBackendHandler();
        backend.Connections.AddRange(connections);
        var provider = new RecordingHttpMessageHandler((request, _, _) =>
            throw new Xunit.Sdk.XunitException($"No connection was due, so nothing may be requested: {request.RequestUri}"));
        var service = environment.CreateSyncService(provider, backend);

        return await service.SyncAllAsync(CancellationToken.None);
    }

    private static BankConnectionDto Connection(
        DateTimeOffset? lastAttemptAt = null,
        DateTimeOffset? nextSyncAllowedAt = null,
        DateTimeOffset? validUntil = null,
        string status = "AUTHORIZED",
        string provider = "enable-banking",
        string? lastError = null) => new(
            Guid.NewGuid(),
            provider,
            "Test Bank",
            "DE",
            null,
            null,
            "session-1",
            status,
            validUntil ?? DateTimeOffset.UtcNow.AddDays(30),
            lastAttemptAt,
            null,
            nextSyncAllowedAt,
            0,
            lastError);
}
