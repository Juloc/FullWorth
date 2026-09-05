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

        var normalizedStatus = (status ?? string.Empty).ToUpperInvariant();
        if (normalizedStatus == "EXPIRED")
            return new("expired", daysUntilExpiry);
        if (normalizedStatus == "REVOKED")
            return new("revoked", daysUntilExpiry);
        if (normalizedStatus == "CLOSED")
            return new("closed", daysUntilExpiry);
        if (!string.Equals(normalizedStatus, "AUTHORIZED", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(providerSessionId))
            return new("reauthorization_required", daysUntilExpiry);
        if (validUntil is { } expiredAt && expiredAt < now)
            return new("expired", daysUntilExpiry);
        if (string.Equals(lastError, "HISTORY_PAGE_LIMIT_REACHED", StringComparison.Ordinal))
            return new("partial_history", daysUntilExpiry);
        if (consecutiveFailures > 0 || lastError is not null)
            return new("error", daysUntilExpiry);
        if (nextSyncAllowedAt is { } cooldownEndsAt && cooldownEndsAt > now)
            return new("cooldown", daysUntilExpiry);
        if (validUntil is { } expiringAt && expiringAt <= now.AddDays(7))
            return new("expiring", daysUntilExpiry);

        return new("authorized", daysUntilExpiry);
    }
}
