using FullWorth.Backend.Modules.BankConnections;

namespace FullWorth.Backend.Tests.BankConnections;

public sealed class BankConnectionConsentHealthTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("PENDING_AUTHORIZATION", "session", 30, 0, null, null, "reauthorization_required", 30)]
    [InlineData("AUTHORIZED", null, 30, 0, null, null, "reauthorization_required", 30)]
    [InlineData("AUTHORIZED", "session", -1, 2, "provider failure", 2, "expired", -1)]
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

    [Fact]
    public void CalculateReturnsNoExpiryDaysWhenConsentHasNoExpiry()
    {
        var result = BankConnectionConsentHealthCalculator.Calculate(
            "AUTHORIZED", "session", null, 0, null, null, Now);

        Assert.Equal("authorized", result.HealthStatus);
        Assert.Null(result.DaysUntilExpiry);
    }
}
