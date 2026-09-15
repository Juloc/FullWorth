using System.Text.Json;
using FullWorth.Backend.Data;
using FullWorth.Backend.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FullWorth.Backend.Modules.Audit;

/// <summary>Append-only record of a security-relevant action.</summary>

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
        if (!await SpaceCapabilities.HasCapabilityAsync(
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
