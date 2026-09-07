using System.Text.Json;

namespace FullWorth.Backend.Modules.Intelligence.Signals;

public static class FinancialSignalJobTypes
{
    public const string RefreshUser = "signal-refresh-user";
    public const string RefreshSpace = "signal-refresh-space";
    public const string DailyFallback = "signal-daily-fallback";

    public static bool IsSupported(string type) =>
        type is RefreshUser or RefreshSpace or DailyFallback;
}

public sealed class FinancialSignalRefreshQueue(
    IntelligenceStore store,
    AutopilotRolloutSettings rollout)
{
    private static readonly TimeSpan DebounceWindow = TimeSpan.FromMinutes(5);

    public Task<IntelligenceJob?> EnqueueSpaceAsync(
        Guid fullWorthSpaceId,
        DateTimeOffset now,
        string reason,
        CancellationToken ct)
    {
        if (fullWorthSpaceId == Guid.Empty) throw new ArgumentException("FullWorth Space is required.", nameof(fullWorthSpaceId));
        if (!rollout.IsShadowOrOn(AutopilotFeatures.Signals)) return Task.FromResult<IntelligenceJob?>(null);

        var bucket = Bucket(now);
        var key = $"signals:space:{fullWorthSpaceId:N}:{bucket.ToUnixTimeSeconds()}";
        var payload = JsonSerializer.Serialize(new
        {
            fullWorthSpaceId,
            reason = NormalizeReason(reason)
        });
        return EnqueueAsync(FinancialSignalJobTypes.RefreshSpace, $"space:{fullWorthSpaceId:N}", now, key, payload, ct);
    }

    public Task<IntelligenceJob?> EnqueueUserAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        DateTimeOffset now,
        string reason,
        CancellationToken ct)
    {
        if (userId == Guid.Empty) throw new ArgumentException("User is required.", nameof(userId));
        if (fullWorthSpaceId == Guid.Empty) throw new ArgumentException("FullWorth Space is required.", nameof(fullWorthSpaceId));
        if (!rollout.IsShadowOrOn(AutopilotFeatures.Signals)) return Task.FromResult<IntelligenceJob?>(null);

        var bucket = Bucket(now);
        var key = $"signals:user:{userId:N}:{fullWorthSpaceId:N}:{bucket.ToUnixTimeSeconds()}";
        var payload = JsonSerializer.Serialize(new
        {
            userId,
            fullWorthSpaceId,
            reason = NormalizeReason(reason)
        });
        return EnqueueAsync(FinancialSignalJobTypes.RefreshUser, $"user:{userId:N}", now, key, payload, ct);
    }

    public Task<IntelligenceJob?> EnqueueDailyFallbackAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (!rollout.IsShadowOrOn(AutopilotFeatures.Signals)) return Task.FromResult<IntelligenceJob?>(null);
        var utc = now.ToUniversalTime();
        var key = $"signals:daily:{utc:yyyy-MM-dd}";
        return EnqueueAsync(
            FinancialSignalJobTypes.DailyFallback,
            "instance",
            now,
            key,
            "{}",
            ct);
    }

    private async Task<IntelligenceJob?> EnqueueAsync(
        string type,
        string scopeKey,
        DateTimeOffset scheduledFor,
        string key,
        string payload,
        CancellationToken ct) =>
        await store.EnqueueJobAsync(type, scopeKey, scheduledFor, key, payload, ct);

    private static DateTimeOffset Bucket(DateTimeOffset now)
    {
        var seconds = (long)DebounceWindow.TotalSeconds;
        var unix = now.ToUniversalTime().ToUnixTimeSeconds();
        return DateTimeOffset.FromUnixTimeSeconds(unix - unix % seconds);
    }

    private static string NormalizeReason(string reason)
    {
        var value = string.IsNullOrWhiteSpace(reason) ? "financial-change" : reason.Trim();
        return value.Length <= 80 ? value : value[..80];
    }
}
