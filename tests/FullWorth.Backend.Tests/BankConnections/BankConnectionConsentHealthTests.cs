using FullWorth.Backend.Modules.BankConnections;

namespace FullWorth.Backend.Tests.BankConnections;

public sealed class BankConnectionConsentHealthTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("PENDING_AUTHORIZATION", "session", 30, 0, null, null, "reauthorization_required", 30)]
    [InlineData("AUTHORIZED", null, 30, 0, null, null, "reauthorization_required", 30)]
    [InlineData("EXPIRED", "session", 30, 0, "SESSION_EXPIRED", null, "expired", 30)]
    [InlineData("REVOKED", "session", 30, 1, "SESSION_REVOKED", null, "revoked", 30)]
    [InlineData("CLOSED", null, 30, 0, null, null, "closed", 30)]
    [InlineData("AUTHORIZED", "session", -1, 2, "provider failure", 2, "expired", -1)]
    [InlineData("AUTHORIZED", "session", 30, 1, "HISTORY_PAGE_LIMIT_REACHED", 2, "partial_history", 30)]
    [InlineData("AUTHORIZED", "session", 30, 1, null, 2, "error", 30)]
    [InlineData("AUTHORIZED", "session", 30, 0, null, 2, "cooldown", 30)]
    [InlineData("AUTHORIZED", "session", 7, 0, null, null, "expiring", 7)]
    [InlineData("AUTHORIZED", "session", 8, 0, null, null, "authorized", 8)]
    public void CalculateReturnsExpectedHealthStatusInPriorityOrder(
        string status,
        string? providerSessionId,
        int validUntilDays,
        int consecutiveFailures,
        string? lastError,
        int? cooldownDays,
        string expectedHealthStatus,
        int expectedDaysUntilExpiry)
    {
        var result = BankConnectionConsentHealthCalculator.Calculate(
            status,
            providerSessionId,
            Now.AddDays(validUntilDays),
            consecutiveFailures,
            lastError,
            cooldownDays is { } days ? Now.AddDays(days) : null,
            Now);

        Assert.Equal(expectedHealthStatus, result.HealthStatus);
        Assert.Equal(expectedDaysUntilExpiry, result.DaysUntilExpiry);
    }

    // A parked TAN challenge is its own state. It used to fall through to reauthorization_required,
    // whose only action is Reconnect - which starts a fresh authorization and discards the very challenge
    // the bank is waiting for, leaving the connection permanently unrepairable.
    [Theory]
    [InlineData("TAN_REQUIRED", null)]
    [InlineData("TAN_REQUIRED", "FINTS_TAN_REQUIRED")]
    [InlineData("AUTHORIZED", "FINTS_TAN_REQUIRED")]
    public void APendingTanIsItsOwnHealthState(string status, string? lastError)
    {
        var result = BankConnectionConsentHealthCalculator.Calculate(
            status, "session", Now.AddDays(30), 0, lastError, null, Now);

        Assert.Equal("tan_required", result.HealthStatus);
    }

    [Fact]
    public void CalculateReturnsNoExpiryDaysWhenConsentHasNoExpiry()
    {
        var result = BankConnectionConsentHealthCalculator.Calculate(
            "AUTHORIZED", "session", null, 0, null, null, Now);

        Assert.Equal("authorized", result.HealthStatus);
        Assert.Null(result.DaysUntilExpiry);
    }
}
