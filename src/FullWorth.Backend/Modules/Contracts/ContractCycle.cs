namespace FullWorth.Backend.Modules.Contracts;

// Cadence math shared by contract detection and the detail view so both step identically.
public static class ContractCycle
{
    public static DateOnly Next(DateOnly date, string? cycle, int interval)
    {
        interval = Math.Max(1, interval);
        return (cycle ?? "monthly").Trim().ToLowerInvariant() switch
        {
            "weekly" => date.AddDays(7 * interval),
            "quarterly" => date.AddMonths(3 * interval),
            "yearly" => date.AddYears(interval),
            "daily" => date.AddDays(interval),
            _ => date.AddMonths(interval)
        };
    }

    public static decimal PeriodsPerYear(string? cycle, int interval)
    {
        interval = Math.Max(1, interval);
        return (cycle ?? "monthly").Trim().ToLowerInvariant() switch
        {
            "weekly" => 52m / interval,
            "quarterly" => 4m / interval,
            "yearly" => 1m / interval,
            "daily" => 365m / interval,
            _ => 12m / interval
        };
    }

    public static DateOnly NextOnOrAfter(DateOnly start, string? cycle, int interval, DateOnly onOrAfter)
    {
        if (start >= onOrAfter) return start;
        var c = (cycle ?? "monthly").Trim().ToLowerInvariant();
        interval = Math.Max(1, interval);
        if (c is "daily" or "weekly")
        {
            var stepDays = (c == "weekly" ? 7 : 1) * interval;
            var wholePeriodsBehind = (onOrAfter.DayNumber - start.DayNumber) / stepDays;
            var next = start.AddDays(wholePeriodsBehind * stepDays);
            if (next < onOrAfter) next = next.AddDays(stepDays);
            return next;
        }
        var guard = 0;
        var cursor = start;
        while (cursor < onOrAfter && guard++ < 2400) cursor = Next(cursor, c, interval);
        return cursor;
    }
}
