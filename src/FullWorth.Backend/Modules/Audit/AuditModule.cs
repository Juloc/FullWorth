using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Parity;
using FullWorth.Backend.Security;
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

/// <summary>
/// Small append-only audit API. Its deliberately narrow contract prevents callers from adding
/// request bodies, provider responses, credentials, or other sensitive material to the audit log.
/// The caller owns SaveChanges so an audit event is committed with the mutation it describes.
/// </summary>
public sealed class AuditService(DbContext db)
{
    public void Record(
        Guid? fullWorthSpaceId,
        Guid? actorUserId,
        string action,
        string entityType,
        Guid? entityId = null)
    {
        if (db.Model.FindEntityType(typeof(AuditEvent)) is null)
            return;

        db.Set<AuditEvent>().Add(new AuditEvent
        {
            FullWorthSpaceId = fullWorthSpaceId,
            ActorUserId = actorUserId,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            MetadataJson = null,
            OccurredAt = DateTimeOffset.UtcNow
        });
    }
}

public sealed record AuditEventDto(
    Guid Id,
    Guid? ActorUserId,
    string Action,
    string EntityType,
    Guid? EntityId,
    DateTimeOffset OccurredAt);

public sealed class AuditStore(FullWorthDbContext db)
{
    /// <summary>
    /// Returns the most recent audit events when the member has the explicit audit.read capability.
    /// Owners always have it; editor/viewer templates do not unless the owner grants an override.
    /// Returning null for denied/unknown spaces preserves the existing anti-enumeration behavior.
    /// </summary>
    public async Task<IReadOnlyList<AuditEventDto>?> ListForSpaceAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        string? action,
        string? entityType,
        DateTimeOffset? before,
        Guid? beforeId,
        int limit,
        CancellationToken ct)
    {
        if (!await PermissionsErgonomicsParityEndpoints.HasCapabilityAsync(
                db, userId, fullWorthSpaceId, "audit.read", ct)) return null;

        var take = limit is <= 0 or > 500 ? 100 : limit;
        var query = db.Set<AuditEvent>().AsNoTracking().Where(x => x.FullWorthSpaceId == fullWorthSpaceId);
        if (!string.IsNullOrEmpty(action)) query = query.Where(x => x.Action == action);
        if (!string.IsNullOrEmpty(entityType)) query = query.Where(x => x.EntityType == entityType);
        if (before is { } cutoff)
        {
            var cutoffId = beforeId ?? Guid.Empty;
            query = query.Where(x => x.OccurredAt < cutoff || (x.OccurredAt == cutoff && x.Id < cutoffId));
        }
        return await query
            .OrderByDescending(x => x.OccurredAt).ThenByDescending(x => x.Id)
            .Take(take)
            .Select(x => new AuditEventDto(x.Id, x.ActorUserId, x.Action, x.EntityType, x.EntityId, x.OccurredAt))
            .ToListAsync(ct);
    }
}

public static class AuditEndpoints
{
    public static IEndpointRouteBuilder MapAuditEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/audit", async (
            Guid fullWorthSpaceId,
            string? action,
            string? entityType,
            DateTimeOffset? before,
            Guid? beforeId,
            int? limit,
            CurrentUserContext currentUser,
            AuditStore store,
            CancellationToken ct) =>
        {
            var events = await store.ListForSpaceAsync(
                currentUser.RequireUserId(), fullWorthSpaceId, action, entityType, before, beforeId, limit ?? 100, ct);
            return events is null ? Results.NotFound() : Results.Ok(events);
        }).WithTags("Audit");

        return app;
    }
}
