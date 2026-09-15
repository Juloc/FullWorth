using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.BankConnections;

public sealed class BankConnection
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FullWorthSpaceId { get; set; }
    public Guid? EnableBankingProfileId { get; set; }
    public string Provider { get; set; } = "enable-banking";
    public string InstitutionName { get; set; } = string.Empty;
    public string Country { get; set; } = "DE";
    public string PsuType { get; set; } = "personal";
    public string? AuthMethod { get; set; }
    public string RequiredPsuHeadersJson { get; set; } = "[]";
    public string? AuthorizationState { get; set; }
    // The OAuth state is bound to the user who initiated the connect and expires; it is consumed
    // exactly once at callback so a replayed callback cannot re-drive the flow.
    public Guid? AuthorizationUserId { get; set; }
    public DateTimeOffset? AuthorizationStateExpiresAt { get; set; }
    public string? AuthorizationId { get; set; }
    // Encrypted at rest (P0.4). ProviderSessionIdLookup is a keyed blind index that keeps the value
    // uniquely constrained and findable (ingest batch -> connection) without storing it in the clear.
    public string? ProviderSessionId { get; set; }
    public string? ProviderSessionIdLookup { get; set; }
    public string Status { get; set; } = "PENDING_AUTHORIZATION";
    public DateTimeOffset? ValidUntil { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }
    public DateTimeOffset? LastSyncedAt { get; set; }
    public DateTimeOffset? NextSyncAllowedAt { get; set; }
    public int ConsecutiveFailures { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
