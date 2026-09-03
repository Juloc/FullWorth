namespace FullWorth.Backend.Modules.BankConnections;

public sealed record BankConnectionConsentHealth(string HealthStatus, int? DaysUntilExpiry);

public static class BankConnectionConsentHealthCalculator
{
    public static BankConnectionConsentHealth Calculate(
        string status,
        string? providerSessionId,
        DateTimeOffset? validUntil,
        int consecutiveFailures,
        string? lastError,
        DateTimeOffset? nextSyncAllowedAt,
        DateTimeOffset now)
    {
        var daysUntilExpiry = validUntil is { } expiry
            ? (int?)(int)(expiry.Date - now.Date).TotalDays
            : null;

        if (!string.Equals(status, "AUTHORIZED", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(providerSessionId))
            return new("reauthorization_required", daysUntilExpiry);
        if (validUntil is { } expiredAt && expiredAt < now)
            return new("expired", daysUntilExpiry);
        if (consecutiveFailures > 0 || lastError is not null)
            return new("error", daysUntilExpiry);
        if (nextSyncAllowedAt is { } cooldownEndsAt && cooldownEndsAt > now)
            return new("cooldown", daysUntilExpiry);
        if (validUntil is { } expiringAt && expiringAt <= now.AddDays(7))
            return new("expiring", daysUntilExpiry);

        return new("authorized", daysUntilExpiry);
    }
}
