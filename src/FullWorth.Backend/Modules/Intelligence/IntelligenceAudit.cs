using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Intelligence;

/// <summary>
/// Append-only audit for instance-level intelligence administration. Deliberately stores no request,
/// response, credential, prompt, or arbitrary metadata payloads.
/// </summary>
public sealed class IntelligenceAuditEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ActorUserId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public Guid? EntityId { get; set; }
    public string Outcome { get; set; } = "success";
    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;
}

public static class IntelligenceAuditModelConfiguration
{
    public static void ConfigureAudit(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<IntelligenceAuditEvent>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.OccurredAt);
            entity.HasIndex(x => new { x.ActorUserId, x.OccurredAt });
            entity.Property(x => x.Action).HasMaxLength(80);
            entity.Property(x => x.EntityType).HasMaxLength(80);
            entity.Property(x => x.Outcome).HasMaxLength(32);
        });
    }
}

public static class IntelligenceAuditWriter
{
    public static void Record(
        IntelligenceDbContext db,
        Guid actorUserId,
        string action,
        string entityType,
        Guid? entityId = null,
        string outcome = "success")
    {
        db.IntelligenceAuditEvents.Add(new IntelligenceAuditEvent
        {
            ActorUserId = actorUserId,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            Outcome = outcome,
            OccurredAt = DateTimeOffset.UtcNow
        });
    }
}
