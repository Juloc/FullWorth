using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Intelligence;

public static class KnowledgePackProtocol
{
    public const string SchemaVersion = "2";
    public const string LegacySchemaVersion = "1";
    public const string SignatureAlgorithm = "RSA-PSS-SHA256";
    public const string InstallationScopeKey = "instance";

    public static bool IsSupportedSchemaVersion(string? value) =>
        value is LegacySchemaVersion or SchemaVersion;
}

public sealed record KnowledgePackManifest(
    string PackId,
    string Version,
    string SchemaVersion,
    string Region,
    string ContentSha256,
    string SignatureAlgorithm,
    string SignatureBase64,
    string? MinimumClientVersion);

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

public sealed record KnowledgePackOntologyEntityPayload(
    string EntityType,
    string CanonicalKey,
    string DisplayName,
    string? ParentCanonicalKey,
    string Status,
    int Version);

public sealed record KnowledgePackOntologyAliasPayload(
    string EntityType,
    string CanonicalKey,
    string Alias,
    string NormalizedAlias,
    string Locale,
    string? Country,
    decimal Confidence,
    int DistinctInstances,
    int Version);

public sealed record KnowledgePackOntologyRedirectPayload(
    string EntityType,
    string FromCanonicalKey,
    string ToCanonicalKey,
    int Version);

public sealed record KnowledgePackBrandAssetPayload(
    string BrandKey,
    string CanonicalName,
    string LogoKey,
    string MediaType,
    string? ContentBase64,
    string ContentSha256,
    int ByteLength,
    string? SourceName,
    string? SourceUrl,
    string? LicenseNote);

public sealed record KnowledgePackBrandAliasPayload(
    string AliasKey,
    string BrandKey,
    string? Country);

public sealed record KnowledgePackPayload(
    string PackId,
    string Version,
    string SchemaVersion,
    string Region,
    IReadOnlyList<KnowledgePackMerchantPayload> Merchants,
    IReadOnlyList<KnowledgePackOntologyEntityPayload>? OntologyEntities = null,
    IReadOnlyList<KnowledgePackOntologyAliasPayload>? OntologyAliases = null,
    IReadOnlyList<KnowledgePackOntologyRedirectPayload>? OntologyRedirects = null,
    IReadOnlyList<KnowledgePackBrandAssetPayload>? BrandAssets = null,
    IReadOnlyList<KnowledgePackBrandAliasPayload>? BrandAliases = null,
    IReadOnlyList<KnowledgePackOntologyEntityPayload>? ProviderOntologyEntities = null,
    IReadOnlyList<KnowledgePackOntologyAliasPayload>? ProviderOntologyAliases = null,
    IReadOnlyList<KnowledgePackOntologyRedirectPayload>? ProviderOntologyRedirects = null,
    IReadOnlyList<KnowledgePackOntologyEntityPayload>? ProductOntologyEntities = null,
    IReadOnlyList<KnowledgePackOntologyAliasPayload>? ProductOntologyAliases = null,
    IReadOnlyList<KnowledgePackOntologyRedirectPayload>? ProductOntologyRedirects = null);

public sealed class KnowledgePackInstallation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ScopeKey { get; set; } = KnowledgePackProtocol.InstallationScopeKey;
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


public sealed class OfficialBrandAsset
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string BrandKey { get; set; } = string.Empty;
    public string CanonicalName { get; set; } = string.Empty;
    public string LogoKey { get; set; } = string.Empty;
    public string MediaType { get; set; } = "image/svg+xml";
    public string ContentSha256 { get; set; } = string.Empty;
    public int ByteLength { get; set; }
    public string? SourceName { get; set; }
    public string? SourceUrl { get; set; }
    public string? LicenseNote { get; set; }
}

public sealed class BrandAssetBlob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ContentSha256 { get; set; } = string.Empty;
    public string MediaType { get; set; } = "image/svg+xml";
    public int ByteLength { get; set; }
    public byte[] Content { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastUsedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class CustomBrandPack
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = "1";
    public int Priority { get; set; } = 1000;
    public bool Enabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class CustomBrandAsset
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PackId { get; set; }
    public string BrandKey { get; set; } = string.Empty;
    public string CanonicalName { get; set; } = string.Empty;
    public string LogoKey { get; set; } = string.Empty;
    public string MediaType { get; set; } = "image/svg+xml";
    public string ContentSha256 { get; set; } = string.Empty;
    public int ByteLength { get; set; }
    public string? SourceName { get; set; }
    public string? SourceUrl { get; set; }
    public string? LicenseNote { get; set; }
}

public sealed class CustomBrandAlias
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PackId { get; set; }
    public string AliasKey { get; set; } = string.Empty;
    public string BrandKey { get; set; } = string.Empty;
    public string Country { get; set; } = "GLOBAL";
}

public sealed class OfficialBrandAlias
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string AliasKey { get; set; } = string.Empty;
    public string BrandKey { get; set; } = string.Empty;
    public string Country { get; set; } = "GLOBAL";
}

public sealed class OfficialOntologyEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string EntityType { get; set; } = string.Empty;
    public string CanonicalKey { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? ParentCanonicalKey { get; set; }
    public string Status { get; set; } = string.Empty;
    public int Version { get; set; }
}

public sealed class OfficialOntologyAlias
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string EntityType { get; set; } = string.Empty;
    public string CanonicalKey { get; set; } = string.Empty;
    public string Alias { get; set; } = string.Empty;
    public string NormalizedAlias { get; set; } = string.Empty;
    public string Locale { get; set; } = "und";
    public string Country { get; set; } = "GLOBAL";
    public decimal Confidence { get; set; }
    public int DistinctInstances { get; set; }
    public int Version { get; set; }
}

public sealed class OfficialOntologyRedirect
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string EntityType { get; set; } = string.Empty;
    public string FromCanonicalKey { get; set; } = string.Empty;
    public string ToCanonicalKey { get; set; } = string.Empty;
    public int Version { get; set; }
}

/// <summary>
/// Read-only merchant-to-category mapping DTO consumed by the deterministic transaction rule engine.
/// Only rows from the currently verified knowledge-pack installation are projected to this shape.
/// </summary>
public sealed record OfficialMerchantCategoryMapping(
    string AliasKey,
    string Direction,
    string CategoryKey,
    decimal Confidence);

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
            entity.Property(x => x.Confidence).HasPrecision(6, 5);
            entity.Property(x => x.Domain).HasMaxLength(255);
            entity.Property(x => x.LogoKey).HasMaxLength(180);
        });

        modelBuilder.Entity<OfficialBrandAsset>(entity =>
        {
            entity.HasIndex(x => x.BrandKey).IsUnique();
            entity.HasIndex(x => x.LogoKey).IsUnique();
            entity.HasIndex(x => x.ContentSha256);
            entity.Property(x => x.BrandKey).HasMaxLength(120);
            entity.Property(x => x.CanonicalName).HasMaxLength(200);
            entity.Property(x => x.LogoKey).HasMaxLength(120);
            entity.Property(x => x.MediaType).HasMaxLength(80);
            entity.Property(x => x.ContentSha256).HasMaxLength(64);
            entity.Property(x => x.SourceName).HasMaxLength(200);
            entity.Property(x => x.SourceUrl).HasMaxLength(1000);
            entity.Property(x => x.LicenseNote).HasMaxLength(500);
        });

        modelBuilder.Entity<BrandAssetBlob>(entity =>
        {
            entity.HasIndex(x => x.ContentSha256).IsUnique();
            entity.Property(x => x.ContentSha256).HasMaxLength(64);
            entity.Property(x => x.MediaType).HasMaxLength(80);
        });

        modelBuilder.Entity<CustomBrandPack>(entity =>
        {
            entity.HasIndex(x => x.Name).IsUnique();
            entity.Property(x => x.Name).HasMaxLength(120);
            entity.Property(x => x.Version).HasMaxLength(80);
        });

        modelBuilder.Entity<CustomBrandAsset>(entity =>
        {
            entity.HasIndex(x => new { x.PackId, x.BrandKey }).IsUnique();
            entity.HasIndex(x => x.ContentSha256);
            entity.Property(x => x.BrandKey).HasMaxLength(120);
            entity.Property(x => x.CanonicalName).HasMaxLength(200);
            entity.Property(x => x.LogoKey).HasMaxLength(120);
            entity.Property(x => x.MediaType).HasMaxLength(80);
            entity.Property(x => x.ContentSha256).HasMaxLength(64);
            entity.Property(x => x.SourceName).HasMaxLength(200);
            entity.Property(x => x.SourceUrl).HasMaxLength(1000);
            entity.Property(x => x.LicenseNote).HasMaxLength(500);
        });

        modelBuilder.Entity<CustomBrandAlias>(entity =>
        {
            entity.HasIndex(x => new { x.PackId, x.AliasKey, x.Country }).IsUnique();
            entity.HasIndex(x => new { x.PackId, x.BrandKey });
            entity.Property(x => x.AliasKey).HasMaxLength(300);
            entity.Property(x => x.BrandKey).HasMaxLength(120);
            entity.Property(x => x.Country).HasMaxLength(8);
        });

        modelBuilder.Entity<OfficialBrandAlias>(entity =>
        {
            entity.HasIndex(x => new { x.AliasKey, x.Country }).IsUnique();
            entity.HasIndex(x => x.BrandKey);
            entity.Property(x => x.AliasKey).HasMaxLength(300);
            entity.Property(x => x.BrandKey).HasMaxLength(120);
            entity.Property(x => x.Country).HasMaxLength(8);
        });

        modelBuilder.Entity<OfficialOntologyEntity>(entity =>
        {
            entity.HasIndex(x => new { x.EntityType, x.CanonicalKey }).IsUnique();
            entity.Property(x => x.EntityType).HasMaxLength(32);
            entity.Property(x => x.CanonicalKey).HasMaxLength(180);
            entity.Property(x => x.DisplayName).HasMaxLength(200);
            entity.Property(x => x.ParentCanonicalKey).HasMaxLength(180);
            entity.Property(x => x.Status).HasMaxLength(24);
        });

        modelBuilder.Entity<OfficialOntologyAlias>(entity =>
        {
            entity.HasIndex(x => new { x.EntityType, x.NormalizedAlias, x.Locale, x.Country });
            entity.HasIndex(x => new { x.EntityType, x.CanonicalKey });
            entity.Property(x => x.EntityType).HasMaxLength(32);
            entity.Property(x => x.CanonicalKey).HasMaxLength(180);
            entity.Property(x => x.Alias).HasMaxLength(200);
            entity.Property(x => x.NormalizedAlias).HasMaxLength(200);
            entity.Property(x => x.Locale).HasMaxLength(20);
            entity.Property(x => x.Country).HasMaxLength(8);
            entity.Property(x => x.Confidence).HasPrecision(6, 5);
        });

        modelBuilder.Entity<OfficialOntologyRedirect>(entity =>
        {
            entity.HasIndex(x => new { x.EntityType, x.FromCanonicalKey }).IsUnique();
            entity.Property(x => x.EntityType).HasMaxLength(32);
            entity.Property(x => x.FromCanonicalKey).HasMaxLength(180);
            entity.Property(x => x.ToCanonicalKey).HasMaxLength(180);
        });
    }
}
