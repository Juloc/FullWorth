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
    public const string CurrentVersion = "2026-09-01.1";
}

public sealed class CloudConnectionState
{
    public const string InstanceScopeKey = "instance";

    public Guid Id { get; set; } = Guid.NewGuid();
    public string ScopeKey { get; set; } = InstanceScopeKey;
    public Guid InstanceId { get; set; } = Guid.NewGuid();
    public string Mode { get; set; } = CloudIntelligenceModes.Disabled;
    /// <summary>
    /// Null means the mandatory reciprocal-cloud choice has not been made yet. Once populated, Mode
    /// contains the explicit choice: enabled for reciprocal FullWorth Cloud, disabled for local-only.
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
    }
}
