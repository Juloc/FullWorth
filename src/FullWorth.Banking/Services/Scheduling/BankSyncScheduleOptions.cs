namespace FullWorth.Banking.Services.Scheduling;

public sealed class BankSyncScheduleOptions
{
    public const string SectionName = "SyncSchedule";

    public TimeOnly MorningSlot { get; set; } = new(8, 0);
    public TimeOnly MiddaySlot { get; set; } = new(13, 0);
    public TimeOnly EveningSlot { get; set; } = new(18, 0);
    public string TimeZoneId { get; set; } = "Europe/Berlin";
}
