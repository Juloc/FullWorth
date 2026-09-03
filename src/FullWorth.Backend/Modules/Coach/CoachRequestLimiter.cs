using System.Collections.Concurrent;

namespace FullWorth.Backend.Modules.Coach;

/// <summary>
/// Small in-process guard for expensive Coach answer generation. The browser BFF already has its
/// general API limiter; this tighter backend guard protects optional provider spend as well as direct
/// trusted-service calls. It deliberately keys by Finance user rather than IP.
/// </summary>
public static class CoachRequestLimiter
{
    public const int PermitLimit = 30;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private static readonly ConcurrentDictionary<Guid, Counter> Counters = new();

    public static bool TryAcquire(Guid userId, out TimeSpan retryAfter)
    {
        var now = DateTimeOffset.UtcNow;
        var counter = Counters.GetOrAdd(userId, _ => new Counter(now));
        lock (counter)
        {
            if (now - counter.Start >= Window)
            {
                counter.Start = now;
                counter.Count = 0;
            }

            if (counter.Count >= PermitLimit)
            {
                retryAfter = Window - (now - counter.Start);
                if (retryAfter < TimeSpan.FromSeconds(1)) retryAfter = TimeSpan.FromSeconds(1);
                return false;
            }

            counter.Count++;
            retryAfter = TimeSpan.Zero;
            return true;
        }
    }

    internal static void ResetForTests(Guid userId) => Counters.TryRemove(userId, out _);

    private sealed class Counter(DateTimeOffset start)
    {
        public DateTimeOffset Start { get; set; } = start;
        public int Count { get; set; }
    }
}
