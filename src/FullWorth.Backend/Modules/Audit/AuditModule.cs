using System.Text.Json;
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

    /// <summary>
    /// Records a sanitized bank-sync attempt. Only timing, outcome and a short machine error code are
    /// accepted here; provider payloads, credentials and human error messages never enter the audit log.
    /// </summary>
    public void RecordBankSyncAttempt(
        Guid fullWorthSpaceId,
        Guid connectionId,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt,
        string result,
        string? errorCode)
    {
        if (db.Model.FindEntityType(typeof(AuditEvent)) is null)
            return;

        var safeResult = result switch
        {
            "success" => "success",
            "partial" => "partial",
            _ => "error"
        };
        var safeErrorCode = SanitizeMachineCode(errorCode);
        if (completedAt < startedAt) completedAt = startedAt;

        db.Set<AuditEvent>().Add(new AuditEvent
        {
            FullWorthSpaceId = fullWorthSpaceId,
            ActorUserId = null,
            Action = "bank_sync.attempt",
            EntityType = "BankConnection",
            EntityId = connectionId,
            MetadataJson = JsonSerializer.Serialize(new BankSyncAuditMetadata(
                startedAt,
                completedAt,
                Math.Max(0L, (long)(completedAt - startedAt).TotalMilliseconds),
                safeResult,
                safeErrorCode)),
            OccurredAt = completedAt
        });
    }

    /// <summary>Records the first observed pending state for an imported transaction.</summary>
    public void RecordTransactionPendingObserved(
        Guid fullWorthSpaceId,
        Guid transactionId,
        DateTimeOffset occurredAt)
    {
        if (db.Model.FindEntityType(typeof(AuditEvent)) is null)
            return;

        db.Set<AuditEvent>().Add(new AuditEvent
        {
            FullWorthSpaceId = fullWorthSpaceId,
            ActorUserId = null,
            Action = "transaction.pending_observed",
            EntityType = "Transaction",
            EntityId = transactionId,
            MetadataJson = null,
            OccurredAt = occurredAt
        });
    }

    /// <summary>
    /// Records only the old/new provider status when a persisted transaction changes state.
    /// The event timestamp is the exact time FullWorth observed the new status.
    /// </summary>
    public void RecordTransactionStatusTransition(
        Guid fullWorthSpaceId,
        Guid transactionId,
        string? fromStatus,
        string? toStatus,
        DateTimeOffset occurredAt)
    {
        if (db.Model.FindEntityType(typeof(AuditEvent)) is null)
            return;

        db.Set<AuditEvent>().Add(new AuditEvent
        {
            FullWorthSpaceId = fullWorthSpaceId,
            ActorUserId = null,
            Action = "transaction.status_changed",
            EntityType = "Transaction",
            EntityId = transactionId,
            MetadataJson = JsonSerializer.Serialize(new TransactionStatusAuditMetadata(
                SanitizeMachineCode(fromStatus) ?? "UNKNOWN",
                SanitizeMachineCode(toStatus) ?? "UNKNOWN")),
            OccurredAt = occurredAt
        });
    }

    private static string? SanitizeMachineCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var safe = new string(value.Trim().ToUpperInvariant()
            .Where(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.')
            .Take(128)
            .ToArray());
        return safe.Length == 0 ? null : safe;
    }
}

public sealed record BankSyncAuditMetadata(
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    long DurationMs,
    string Result,
    string? ErrorCode);

public sealed record TransactionStatusAuditMetadata(string FromStatus, string ToStatus);

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
