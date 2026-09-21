using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.BankConnections;

public sealed record BankConnectionStatusView(
    Guid Id,
    Guid FullWorthSpaceId,
    string Provider,
    string InstitutionName,
    string Country,
    string PsuType,
    string Status,
    DateTimeOffset? ValidUntil,
    DateTimeOffset? LastSyncedAt,
    DateTimeOffset? NextSyncAllowedAt,
    DateTimeOffset UpdatedAt,
    string HealthStatus,
    int? DaysUntilExpiry);

public sealed record BankSyncHistoryWrite(
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    string Result,
    string? ErrorCode,
    // #167: optional, damit ein aelterer Banking-Prozess gegen ein neueres Backend weiterschreiben
    // kann - zwischen diesen Projekten gibt es keine gemeinsame Vertrags-Bibliothek und keinen Build,
    // der eine Abweichung faengt.
    string? Trigger = null,
    string? Connector = null);

public sealed record BankSyncHistoryItem(
    Guid Id,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    long DurationMs,
    string Result,
    string? ErrorCode,
    // Bei Laeufen, die vor #167 aufgezeichnet wurden, bleiben beide leer - die Oberflaeche laesst die
    // Zeile dann einfach weg, statt "unbekannt" zu behaupten.
    string? Trigger,
    string? Connector);

public sealed record BankConnectionWrite(
    Guid? Id,
    string Provider,
    string InstitutionName,
    string Country,
    string? AuthorizationState,
    string? AuthorizationId,
    string? ProviderSessionId,
    string Status,
    DateTimeOffset? ValidUntil,
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? LastSyncedAt,
    DateTimeOffset? NextSyncAllowedAt,
    int ConsecutiveFailures,
    string? LastError,
    // Required for a NEW connection (validated, owner-checked space). Absent/empty is rejected — there
    // is no LegacyId fallback on the live connect path any more.
    Guid? FullWorthSpaceId = null,
    Guid? AuthorizationUserId = null,
    DateTimeOffset? AuthorizationStateExpiresAt = null,
    Guid? EnableBankingProfileId = null,
    string PsuType = "personal",
    string? AuthMethod = null,
    string RequiredPsuHeadersJson = "[]");

public sealed record BankConnectionAuthorizeRequest(Guid FullWorthSpaceId, Guid? ConnectionId, Guid? EnableBankingProfileId = null);

public enum BankConnectionAuthorizeResult { Authorized, Forbidden, NotFound }

public sealed record ConsumeStateRequest(string State);

public sealed record DeleteBankConnectionInternalRequest(Guid FullWorthSpaceId);

public sealed record CloseBankConnectionInternalRequest(Guid FullWorthSpaceId);
