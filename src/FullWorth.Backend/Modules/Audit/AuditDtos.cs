using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FullWorth.Backend.Modules.Audit;

/// <summary>Append-only record of a security-relevant action.</summary>

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
