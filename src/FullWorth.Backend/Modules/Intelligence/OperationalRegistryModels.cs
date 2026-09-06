using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Intelligence;

public sealed record KnowledgePackContractProviderPayload(
    string ProviderKey,
    string CanonicalName,
    string? Domain,
    string? ProviderCategory,
    string? Country,
    string? BrandKey,
    int Version);

public sealed record KnowledgePackContractSignaturePayload(
    string ProviderKey,
    string MerchantFingerprint,
    string? ExpectedRecurrence,
    decimal Confidence);

public sealed record KnowledgePackProductPayload(
    string ProductKey,
    string CanonicalName,
    string? BrandKey,
    string? CategoryKey,
    string? PackageQuantity,
    string? PackageUnit,
    string? Country,
    int Version);

public sealed record KnowledgePackProductGtinPayload(
    string ProductKey,
    string Gtin);

public sealed record KnowledgePackProductAliasPayload(
    string ProductKey,
    string AliasKey,
    string? MerchantContext,
    decimal Confidence);

public sealed class OfficialContractProvider
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ProviderKey { get; set; } = string.Empty;
    public string CanonicalName { get; set; } = string.Empty;
    public string? Domain { get; set; }
    public string? ProviderCategory { get; set; }
    public string Country { get; set; } = "GLOBAL";
    public string? BrandKey { get; set; }
    public int Version { get; set; }
}

public sealed class OfficialContractSignature
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ProviderKey { get; set; } = string.Empty;
    public string MerchantFingerprint { get; set; } = string.Empty;
    public string? ExpectedRecurrence { get; set; }
    public decimal Confidence { get; set; }
}

public sealed class OfficialProduct
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ProductKey { get; set; } = string.Empty;
    public string CanonicalName { get; set; } = string.Empty;
    public string? BrandKey { get; set; }
    public string? CategoryKey { get; set; }
    public string? PackageQuantity { get; set; }
    public string? PackageUnit { get; set; }
    public string Country { get; set; } = "GLOBAL";
    public int Version { get; set; }
}

public sealed class OfficialProductGtin
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ProductKey { get; set; } = string.Empty;
    public string Gtin { get; set; } = string.Empty;
}

public sealed class OfficialProductAlias
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ProductKey { get; set; } = string.Empty;
    public string AliasKey { get; set; } = string.Empty;
    public string? MerchantContext { get; set; }
    public decimal Confidence { get; set; }
}

public static class OperationalRegistryModelConfiguration
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OfficialContractProvider>(entity =>
        {
            entity.HasIndex(x => x.ProviderKey).IsUnique();
            entity.Property(x => x.ProviderKey).HasMaxLength(180);
            entity.Property(x => x.CanonicalName).HasMaxLength(240);
            entity.Property(x => x.Domain).HasMaxLength(255);
            entity.Property(x => x.ProviderCategory).HasMaxLength(120);
            entity.Property(x => x.Country).HasMaxLength(8);
            entity.Property(x => x.BrandKey).HasMaxLength(120);
        });

        modelBuilder.Entity<OfficialContractSignature>(entity =>
        {
            entity.HasIndex(x => new { x.ProviderKey, x.MerchantFingerprint }).IsUnique();
            entity.HasIndex(x => x.MerchantFingerprint);
            entity.Property(x => x.ProviderKey).HasMaxLength(180);
            entity.Property(x => x.MerchantFingerprint).HasMaxLength(320);
            entity.Property(x => x.ExpectedRecurrence).HasMaxLength(80);
            entity.Property(x => x.Confidence).HasPrecision(6, 5);
        });

        modelBuilder.Entity<OfficialProduct>(entity =>
        {
            entity.HasIndex(x => x.ProductKey).IsUnique();
            entity.Property(x => x.ProductKey).HasMaxLength(180);
            entity.Property(x => x.CanonicalName).HasMaxLength(240);
            entity.Property(x => x.BrandKey).HasMaxLength(120);
            entity.Property(x => x.CategoryKey).HasMaxLength(180);
            entity.Property(x => x.PackageQuantity).HasMaxLength(80);
            entity.Property(x => x.PackageUnit).HasMaxLength(40);
            entity.Property(x => x.Country).HasMaxLength(8);
        });

        modelBuilder.Entity<OfficialProductGtin>(entity =>
        {
            entity.HasIndex(x => x.Gtin).IsUnique();
            entity.HasIndex(x => x.ProductKey);
            entity.Property(x => x.ProductKey).HasMaxLength(180);
            entity.Property(x => x.Gtin).HasMaxLength(20);
        });

        modelBuilder.Entity<OfficialProductAlias>(entity =>
        {
            entity.HasIndex(x => new { x.AliasKey, x.MerchantContext });
            entity.HasIndex(x => x.ProductKey);
            entity.Property(x => x.ProductKey).HasMaxLength(180);
            entity.Property(x => x.AliasKey).HasMaxLength(320);
            entity.Property(x => x.MerchantContext).HasMaxLength(320);
            entity.Property(x => x.Confidence).HasPrecision(6, 5);
        });
    }
}
