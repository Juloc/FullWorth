namespace FullWorth.Backend.Modules.Notifications;

/// <summary>
/// A once-only marker so threshold/time-based notifications (budget crossings, contract-due) are sent at
/// most once per user per occurrence. State-transition types (bank_reauth, bank_sync_error) need no row —
/// they are deduped by the edge itself. The DedupKey encodes the occurrence (budget cycle + threshold, or
/// contract + due date), so a new cycle/occurrence re-arms naturally.
/// </summary>
public sealed class NotificationDedup
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FinanceUserId { get; set; }
    public Guid FullWorthSpaceId { get; set; }
    public string Type { get; set; } = string.Empty;
    public string DedupKey { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
