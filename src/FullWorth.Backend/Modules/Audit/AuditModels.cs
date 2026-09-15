using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FullWorth.Backend.Modules.Audit;

/// <summary>Append-only record of a security-relevant action.</summary>

public sealed class AuditEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? FullWorthSpaceId { get; set; }
    public Guid? ActorUserId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public Guid? EntityId { get; set; }
    public string? MetadataJson { get; set; }
    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    public void Configure(EntityTypeBuilder<AuditEvent> e)
    {
        e.ToTable("AuditEvents");
        e.HasKey(x => x.Id);
        e.Property(x => x.Action).HasMaxLength(64);
        e.Property(x => x.EntityType).HasMaxLength(64);
        e.HasIndex(x => new { x.FullWorthSpaceId, x.OccurredAt });
        e.HasIndex(x => x.OccurredAt);
    }
}
