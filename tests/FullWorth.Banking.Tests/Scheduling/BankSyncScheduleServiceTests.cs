using FullWorth.Banking.Services.Scheduling;
using Microsoft.Extensions.Configuration;

namespace FullWorth.Banking.Tests.Scheduling;

public sealed class BankSyncScheduleServiceTests
{
    private static readonly BankSyncScheduleOptions BerlinSlots = new()
    {
        MorningSlot = new(8, 0),
        MiddaySlot = new(13, 0),
        EveningSlot = new(18, 0),
        TimeZoneId = "Europe/Berlin"
    };

    [Theory]
    [InlineData(2026, 2, 3, 6, 30, 2026, 2, 3, 8, 0)]
    [InlineData(2026, 2, 3, 8, 30, 2026, 2, 3, 13, 0)]
    [InlineData(2026, 2, 3, 13, 30, 2026, 2, 3, 18, 0)]
    public void GetNextScheduledRunReturnsTheNextLocalSlot(
        int nowYear,
        int nowMonth,
        int nowDay,
        int nowHour,
        int nowMinute,
        int expectedYear,
        int expectedMonth,
        int expectedDay,
        int expectedHour,
        int expectedMinute)
    {
        var service = new BankSyncScheduleService(BerlinSlots);
        var result = service.GetNextScheduledRun(
            Berlin(nowYear, nowMonth, nowDay, nowHour, nowMinute),
            lastAttempt: null,
            nextAllowed: null);

        Assert.Equal(Berlin(expectedYear, expectedMonth, expectedDay, expectedHour, expectedMinute), result);
    }

    [Fact]
    public void OptionsBindThreeTimeOnlySlotsAndTimeZoneFromConfiguration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{BankSyncScheduleOptions.SectionName}:MorningSlot"] = "07:30:00",
                [$"{BankSyncScheduleOptions.SectionName}:MiddaySlot"] = "12:15:00",
                [$"{BankSyncScheduleOptions.SectionName}:EveningSlot"] = "19:45:00",
                [$"{BankSyncScheduleOptions.SectionName}:TimeZoneId"] = "Europe/Berlin"
            })
            .Build();

        var options = configuration.GetSection(BankSyncScheduleOptions.SectionName).Get<BankSyncScheduleOptions>();

        Assert.NotNull(options);
        Assert.Equal(new TimeOnly(7, 30), options.MorningSlot);
        Assert.Equal(new TimeOnly(12, 15), options.MiddaySlot);
        Assert.Equal(new TimeOnly(19, 45), options.EveningSlot);
        Assert.Equal("Europe/Berlin", options.TimeZoneId);
    }

    [Fact]
    public void GetNextScheduledRunHonorsHardFloorAndPersistedNextAllowed()
    {
        var service = new BankSyncScheduleService(BerlinSlots);
        var now = Berlin(2026, 2, 3, 7, 0);

        var result = service.GetNextScheduledRun(
            now,
            lastAttempt: now.AddMinutes(-30),
            nextAllowed: Berlin(2026, 2, 3, 15, 0));

        Assert.Equal(Berlin(2026, 2, 3, 18, 0), result);
        Assert.True(result >= now.AddMinutes(-30).AddMinutes(360));
        Assert.True(result >= Berlin(2026, 2, 3, 15, 0));
    }

    [Fact]
    public void GetNextScheduledRunSkipsSlotsInsideTheManualSyncHardFloor()
    {
        var service = new BankSyncScheduleService(BerlinSlots);
        var manualSyncAt = Berlin(2026, 2, 3, 7, 30);

        var result = service.GetNextScheduledRun(manualSyncAt, manualSyncAt, nextAllowed: null);

        Assert.Equal(Berlin(2026, 2, 3, 18, 0), result);
    }

    [Fact]
    public void GetNextScheduledRunMovesSpringForwardSlotToTheFirstValidLocalTime()
    {
        var service = new BankSyncScheduleService(new BankSyncScheduleOptions
        {
            MorningSlot = new(2, 30),
            MiddaySlot = new(12, 0),
            EveningSlot = new(18, 0),
            TimeZoneId = "Europe/Berlin"
        });

        var result = service.GetNextScheduledRun(
            new DateTimeOffset(2026, 3, 29, 0, 0, 0, TimeSpan.Zero),
            lastAttempt: null,
            nextAllowed: null);

        Assert.Equal(new DateTimeOffset(2026, 3, 29, 3, 0, 0, TimeSpan.FromHours(2)), result);
        Assert.Equal(TimeSpan.FromHours(2), result.Offset);
    }

    [Fact]
    public void GetNextScheduledRunUsesTheEarlierOccurrenceOfAnAmbiguousFallBackSlot()
    {
        var service = new BankSyncScheduleService(new BankSyncScheduleOptions
        {
            MorningSlot = new(2, 30),
            MiddaySlot = new(12, 0),
            EveningSlot = new(18, 0),
            TimeZoneId = "Europe/Berlin"
        });

        var result = service.GetNextScheduledRun(
            new DateTimeOffset(2026, 10, 24, 22, 0, 0, TimeSpan.Zero),
            lastAttempt: null,
            nextAllowed: null);

        Assert.Equal(new DateTimeOffset(2026, 10, 25, 2, 30, 0, TimeSpan.FromHours(2)), result);
        Assert.Equal(TimeSpan.FromHours(2), result.Offset);
    }

    private static DateTimeOffset Berlin(int year, int month, int day, int hour, int minute)
    {
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        var local = new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified);
        return new(local, timeZone.GetUtcOffset(local));
    }
}
