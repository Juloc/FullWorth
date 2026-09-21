using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FullWorth.Backend.Modules.Audit;

/// <summary>Append-only record of a security-relevant action.</summary>

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
        string? errorCode,
        string? trigger = null,
        string? connector = null)
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

        // #167: nur die Werte, die diese Anwendung selbst kennt. Ein Auslöser oder Verbindungsweg
        // kommt ueber die interne Schnittstelle herein, und was dort steht, landet sonst ungeprueft
        // in der Historie und von dort auf den Bildschirm.
        var safeTrigger = trigger switch
        {
            "manual" => "manual",
            "authorization" => "authorization",
            "automatic" => "automatic",
            _ => null
        };
        var safeConnector = SanitizeProviderKey(connector);

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
                safeErrorCode,
                safeTrigger,
                safeConnector)),
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

    /// <summary>
    /// Wie <see cref="SanitizeMachineCode"/>, aber ohne Grossschreibung (#167).
    ///
    /// Fehlercodes sind gross (<c>FINTS_TAN_REQUIRED</c>), ein Verbindungsweg ist es nicht: das ist
    /// derselbe Schluessel, den <c>BankConnection.Provider</c> traegt (<c>fints</c>,
    /// <c>enable-banking</c>), und den liest der Rest des Systems klein. Ihn hier grosszuschreiben
    /// hiesse, dass die Historie einen anderen Namen fuer denselben Weg verwendet als jede andere
    /// Stelle - und die Uebersetzung in der Oberflaeche haengt an diesem Schluessel.
    /// </summary>
    private static string? SanitizeProviderKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var safe = new string(value.Trim().ToLowerInvariant()
            .Where(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.')
            .Take(64)
            .ToArray());
        return safe.Length == 0 ? null : safe;
    }
}
