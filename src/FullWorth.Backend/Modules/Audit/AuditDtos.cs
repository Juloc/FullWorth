using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FullWorth.Backend.Modules.Audit;

/// <summary>Append-only record of a security-relevant action.</summary>

/// <param name="Trigger">Wodurch der Lauf ausgeloest wurde (#167): <c>automatic</c> aus dem
/// Hintergrunddienst, <c>manual</c> auf Knopfdruck, <c>authorization</c> bei der Rueckkehr von der
/// Bank. Der Unterschied ist bei einer Stoerung die erste Frage - ein fehlgeschlagener
/// Hintergrundlauf heisst etwas anderes als einer, den jemand gerade angestossen hat.</param>
/// <param name="Connector">Womit synchronisiert wurde, z. B. <c>fints</c> oder <c>enable-banking</c>.
/// Dieselbe Verbindung kann ueber verschiedene Wege laufen, und ein Fehler gehoert dem Weg.</param>
/// <remarks>
/// Beide Felder sind optional, weil sie es fuer bereits aufgezeichnete Laeufe sein muessen: die
/// Historie liegt als JSON in den Audit-Ereignissen, und alte Zeilen haben sie nicht. Sie fehlen dort
/// einfach, statt dass die Zeile unlesbar wird.
/// </remarks>
public sealed record BankSyncAuditMetadata(
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    long DurationMs,
    string Result,
    string? ErrorCode,
    string? Trigger = null,
    string? Connector = null);

public sealed record TransactionStatusAuditMetadata(string FromStatus, string ToStatus);

public sealed record AuditEventDto(
    Guid Id,
    Guid? ActorUserId,
    string Action,
    string EntityType,
    Guid? EntityId,
    DateTimeOffset OccurredAt);
