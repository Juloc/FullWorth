using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Intelligence;

public static class CloudIntelligenceModes
{
    public const string Disabled = "disabled";
    public const string Enabled = "enabled";
}

public static class CloudIntelligencePolicy
{
    // Bump when materially changing what may be contributed. Existing consent must not silently cover
    // new data categories after a material policy change.
    public const string CurrentVersion = "2026-09-06.4";
    public const string SubmissionSchemaVersion = "1";
}

public static class CloudSubmissionStatuses
{
    public const string Queued = "queued";
    public const string Sending = "sending";
    public const string Sent = "sent";
    public const string Failed = "failed";
    public const string DeadLetter = "dead_letter";
}

public sealed class CloudSubmissionOutbox
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid InstanceId { get; set; }
    public Guid? FeedbackEventId { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public string SchemaVersion { get; set; } = CloudIntelligencePolicy.SubmissionSchemaVersion;
    public string EventType { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = "{}";
    public string Status { get; set; } = CloudSubmissionStatuses.Queued;
    public int AttemptCount { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    public string? ErrorCode { get; set; }
    public string? LeaseOwner { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class CloudConnectionState
{
    public const string InstanceScopeKey = "instance";

    public Guid Id { get; set; } = Guid.NewGuid();
    public string ScopeKey { get; set; } = InstanceScopeKey;
    public Guid InstanceId { get; set; } = Guid.NewGuid();
    public string Mode { get; set; } = CloudIntelligenceModes.Disabled;
    /// <summary>
    /// Null means the Cloud Intelligence setup choice has not been completed yet. New setup presents
    /// Cloud Intelligence enabled by default but allows explicit opt-out before completion.
    /// </summary>
    public DateTimeOffset? SetupDecisionAt { get; set; }
    public Guid? SetupDecisionByUserId { get; set; }
    public DateTimeOffset? EnabledAt { get; set; }
    public DateTimeOffset? DisabledAt { get; set; }
    public DateTimeOffset? LastRegistrationAt { get; set; }
    public DateTimeOffset? LastSubmissionAt { get; set; }
    public DateTimeOffset? LastKnowledgePackCheckAt { get; set; }
    public string? EntitlementStatus { get; set; }
    public string? LastErrorCode { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class CloudIntelligenceConsent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid InstanceId { get; set; }
    public Guid AcceptedByUserId { get; set; }
    public string PolicyVersion { get; set; } = string.Empty;
    public DateTimeOffset AcceptedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? RevokedAt { get; set; }
    public string Locale { get; set; } = "en";
    public string ClientVersion { get; set; } = string.Empty;
}

/// <summary>
/// Rotatable credential issued by the official FullWorth Platform Cloud after enrollment/license proof.
/// The bootstrap/enrollment proof is never stored here and is never used as the normal API bearer token.
/// </summary>
public sealed class CloudInstanceCredential
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid InstanceId { get; set; }
    public string ProtectedSecret { get; set; } = string.Empty;
    public string SecretFingerprint { get; set; } = string.Empty;
    public DateTimeOffset IssuedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public static class CloudIntelligenceModelConfiguration
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CloudConnectionState>(entity =>
        {
            entity.HasIndex(x => x.ScopeKey).IsUnique();
            entity.HasIndex(x => x.InstanceId).IsUnique();
            entity.Property(x => x.ScopeKey).HasMaxLength(32);
            entity.Property(x => x.Mode).HasMaxLength(16);
            entity.Property(x => x.EntitlementStatus).HasMaxLength(80);
            entity.Property(x => x.LastErrorCode).HasMaxLength(120);
        });

        modelBuilder.Entity<CloudIntelligenceConsent>(entity =>
        {
            entity.HasIndex(x => new { x.InstanceId, x.AcceptedAt });
            entity.HasIndex(x => new { x.InstanceId, x.PolicyVersion, x.RevokedAt });
            entity.Property(x => x.PolicyVersion).HasMaxLength(80);
            entity.Property(x => x.Locale).HasMaxLength(20);
            entity.Property(x => x.ClientVersion).HasMaxLength(80);
        });

        modelBuilder.Entity<CloudInstanceCredential>(entity =>
        {
            entity.HasIndex(x => x.InstanceId).IsUnique();
            entity.Property(x => x.ProtectedSecret).HasColumnType("text");
            entity.Property(x => x.SecretFingerprint).HasMaxLength(80);
        });


        modelBuilder.Entity<CloudSubmissionOutbox>(entity =>
        {
            entity.HasIndex(x => x.IdempotencyKey).IsUnique();
            entity.HasIndex(x => new { x.Status, x.NextAttemptAt, x.CreatedAt });
            entity.HasIndex(x => new { x.LeaseOwner, x.LeaseExpiresAt });
            entity.HasIndex(x => x.FeedbackEventId);
            entity.Property(x => x.IdempotencyKey).HasMaxLength(240);
            entity.Property(x => x.SchemaVersion).HasMaxLength(40);
            entity.Property(x => x.EventType).HasMaxLength(80);
            entity.Property(x => x.Status).HasMaxLength(32);
            entity.Property(x => x.ErrorCode).HasMaxLength(120);
            entity.Property(x => x.LeaseOwner).HasMaxLength(160);
            entity.Property(x => x.PayloadJson).HasColumnType("jsonb");
        });
    }
}
