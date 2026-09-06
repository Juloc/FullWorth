namespace FullWorth.Web.Modules.Auth;

public sealed class AccountDeletionOptions
{
    public const string SectionName = "AccountDeletion";

    public TimeSpan RecoveryWindow { get; set; } = TimeSpan.FromDays(7);
    public TimeSpan PurgeInterval { get; set; } = TimeSpan.FromHours(1);
    public TimeSpan PurgeLease { get; set; } = TimeSpan.FromMinutes(15);

    public void Validate()
    {
        if (RecoveryWindow < TimeSpan.FromDays(1))
            throw new InvalidOperationException("AccountDeletion:RecoveryWindow must be at least one day.");
        if (PurgeInterval < TimeSpan.FromMinutes(5))
            throw new InvalidOperationException("AccountDeletion:PurgeInterval must be at least five minutes.");
        if (PurgeLease < TimeSpan.FromMinutes(1))
            throw new InvalidOperationException("AccountDeletion:PurgeLease must be at least one minute.");
    }
}

public sealed record AccountDeletionRequest(string CurrentPassword);
public sealed record AccountDeletionStatusDto(
    bool Pending,
    DateTimeOffset? RequestedAt,
    DateTimeOffset? ScheduledFor,
    bool CanReactivate,
    string? LastError);
