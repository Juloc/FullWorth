namespace FullWorth.Web.Modules.Admin;

public sealed class AdminAuditEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ActorAuthUserId { get; set; }
    public Guid? TargetAuthUserId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string Outcome { get; set; } = "success";
    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;
}
