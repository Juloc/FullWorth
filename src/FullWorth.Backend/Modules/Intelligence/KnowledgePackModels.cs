using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Intelligence;

public static class KnowledgePackPolicy
{
    public const string CurrentSchemaVersion = "1";
    public const string InstallationScopeKey = "official";

    /// <summary>
    /// Region requested when neither an installed pack nor operator config specifies one. "GLOBAL"
    /// means region-agnostic packs and is the shared default across the client sync, the signature
    /// verifier and the cloud's pack builder/query — an unconfigured instance must request the same
    /// region the cloud publishes by default, or it would receive 204 forever and never install a pack.
    /// </summary>
    public const string DefaultRegion = "GLOBAL";
    public const int MaximumPackBytes = 5 * 1024 * 1024;
    public const int MaximumMerchantMappings = 50_000;
    public const int ArchiveRetentionCount = 3;
}

public sealed class KnowledgePackInstallation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ScopeKey { get; set; } = KnowledgePackPolicy.InstallationScopeKey;
    public string PackId { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string SchemaVersion { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public string ContentSha256 { get; set; } = string.Empty;
    public string SignatureAlgorithm { get; set; } = string.Empty;
    public int MerchantMappingCount { get; set; }
    public DateTimeOffset InstalledAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastCheckedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? LastErrorCode { get; set; }
}

/// <summary>
/// Verified raw packs are retained byte-exactly (Base64 over the downloaded bytes) so a future rollback
/// can verify the original hash/signature again without JSON/text normalization changing signed bytes.
/// </summary>
public sealed class KnowledgePackArchive
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string PackId { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string SchemaVersion { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public string ContentSha256 { get; set; } = string.Empty;
    public string SignatureAlgorithm { get; set; } = string.Empty;
    public string SignatureBase64 { get; set; } = string.Empty;
    public string PayloadBase64 { get; set; } = string.Empty;
    public DateTimeOffset VerifiedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Official read-only mapping installed from a verified FullWorth Knowledge Pack. This is global to the
/// instance and deliberately carries category keys instead of local category IDs so every FullWorth Space
/// can resolve the same official mapping against its own category tree.
/// </summary>
public sealed class OfficialMerchantMapping
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string PackId { get; set; } = string.Empty;
    public string PackVersion { get; set; } = string.Empty;
    public string AliasKey { get; set; } = string.Empty;
    public string Direction { get; set; } = "any";
    public string CanonicalMerchantKey { get; set; } = string.Empty;
    public string CanonicalName { get; set; } = string.Empty;
    public string? CategoryKey { get; set; }
    public string? Country { get; set; }
    public decimal Confidence { get; set; }
    public string? Domain { get; set; }
    public string? LogoKey { get; set; }
}

public sealed record OfficialMerchantCategoryMapping(
    string AliasKey,
    string Direction,
    string CategoryKey,
    decimal Confidence);

public sealed record KnowledgePackMerchantPayload(
    string AliasKey,
    string Direction,
    string CanonicalMerchantKey,
    string CanonicalName,
    string? CategoryKey,
    string? Country,
    decimal Confidence,
    string? Domain,
    string? LogoKey);

public sealed record KnowledgePackPayload(
    string PackId,
    string Version,
    string SchemaVersion,
    string Region,
    IReadOnlyList<KnowledgePackMerchantPayload> Merchants);

public sealed record KnowledgePackManifest(
    string PackId,
    string Version,
    string SchemaVersion,
    string Region,
    string ContentSha256,
    string SignatureAlgorithm,
    string SignatureBase64,
    string? MinimumClientVersion);

public static class KnowledgePackModelConfiguration
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<KnowledgePackInstallation>(entity =>
        {
            entity.HasIndex(x => x.ScopeKey).IsUnique();
            entity.Property(x => x.ScopeKey).HasMaxLength(32);
            entity.Property(x => x.PackId).HasMaxLength(120);
            entity.Property(x => x.Version).HasMaxLength(80);
            entity.Property(x => x.SchemaVersion).HasMaxLength(40);
            entity.Property(x => x.Region).HasMaxLength(32);
            entity.Property(x => x.ContentSha256).HasMaxLength(80);
            entity.Property(x => x.SignatureAlgorithm).HasMaxLength(40);
            entity.Property(x => x.LastErrorCode).HasMaxLength(120);
        });

        modelBuilder.Entity<KnowledgePackArchive>(entity =>
        {
            entity.HasIndex(x => new { x.PackId, x.Version }).IsUnique();
            entity.Property(x => x.PackId).HasMaxLength(120);
            entity.Property(x => x.Version).HasMaxLength(80);
            entity.Property(x => x.SchemaVersion).HasMaxLength(40);
            entity.Property(x => x.Region).HasMaxLength(32);
            entity.Property(x => x.ContentSha256).HasMaxLength(80);
            entity.Property(x => x.SignatureAlgorithm).HasMaxLength(40);
            entity.Property(x => x.SignatureBase64).HasColumnType("text");
            entity.Property(x => x.PayloadBase64).HasColumnType("text");
        });

        modelBuilder.Entity<OfficialMerchantMapping>(entity =>
        {
            entity.HasIndex(x => new { x.AliasKey, x.Direction, x.Country }).IsUnique();
            entity.HasIndex(x => x.CanonicalMerchantKey);
            entity.Property(x => x.PackId).HasMaxLength(120);
            entity.Property(x => x.PackVersion).HasMaxLength(80);
            entity.Property(x => x.AliasKey).HasMaxLength(300);
            entity.Property(x => x.Direction).HasMaxLength(16);
            entity.Property(x => x.CanonicalMerchantKey).HasMaxLength(180);
            entity.Property(x => x.CanonicalName).HasMaxLength(240);
            entity.Property(x => x.CategoryKey).HasMaxLength(180);
            entity.Property(x => x.Country).HasMaxLength(8);
            entity.Property(x => x.Domain).HasMaxLength(255);
            entity.Property(x => x.LogoKey).HasMaxLength(180);
            entity.Property(x => x.Confidence).HasPrecision(6, 5);
        });
    }
}
