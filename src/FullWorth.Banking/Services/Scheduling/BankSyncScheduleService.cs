namespace FullWorth.Banking.Services.Scheduling;

public sealed class BankSyncScheduleService(BankSyncScheduleOptions options)
{
    private static readonly TimeSpan BackgroundHardFloor = TimeSpan.FromMinutes(360);
    private readonly TimeZoneInfo _timeZone = TimeZoneInfo.FindSystemTimeZoneById(options.TimeZoneId);
    private readonly TimeOnly[] _slots = [options.MorningSlot, options.MiddaySlot, options.EveningSlot];

    public DateTimeOffset GetNextScheduledRun(
        DateTimeOffset now,
        DateTimeOffset? lastAttempt,
        DateTimeOffset? nextAllowed)
    {
        var earliest = Max(now, lastAttempt?.Add(BackgroundHardFloor), nextAllowed);
        var firstLocalDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(earliest, _timeZone).Date);

        for (var dayOffset = 0; ; dayOffset++)
        {
            var localDate = firstLocalDate.AddDays(dayOffset);
            foreach (var slot in _slots.Order())
            {
                var candidate = ResolveLocalSlot(localDate, slot);
                if (candidate >= earliest) return candidate;
            }
        }
    }

    private DateTimeOffset ResolveLocalSlot(DateOnly localDate, TimeOnly slot)
    {
        var localTime = localDate.ToDateTime(slot, DateTimeKind.Unspecified);
        while (_timeZone.IsInvalidTime(localTime)) localTime = localTime.AddMinutes(1);

        if (_timeZone.IsAmbiguousTime(localTime))
            return _timeZone.GetAmbiguousTimeOffsets(localTime)
                .Select(offset => new DateTimeOffset(localTime, offset))
                .OrderBy(candidate => candidate.UtcTicks)
                .First();

        return new(localTime, _timeZone.GetUtcOffset(localTime));
    }

    private static DateTimeOffset Max(DateTimeOffset first, DateTimeOffset? second, DateTimeOffset? third)
    {
        var result = first;
        if (second is { } secondValue && secondValue > result) result = secondValue;
        if (third is { } thirdValue && thirdValue > result) result = thirdValue;
        return result;
    }
}
