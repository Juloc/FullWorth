using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Intelligence;

// Die Nachschlagewerke dieser Instanz: Markenlogos und Haendlerzuordnungen.
//
// Sie standen bis zur Abschaffung der Cloud in KnowledgePackModels.cs, zusammen mit dem
// Uebertragungsformat eines signierten Wissenspakets - Manifest, Delta, Nutzlast, Installation,
// Archiv. Das Format ist weg, die Nachschlagewerke sind geblieben: der mitgelieferte Katalog
// fuellt sie beim Start, eigene Pakete und die eigene Recherche schreiben daneben.

public sealed class OfficialMerchantMapping
{
    public Guid Id { get; set; } = Guid.NewGuid();
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

/// <summary>
/// Ein selbst recherchiertes Logo (#176). Dieselben Felder wie ein Logo aus einem Paket, damit der
/// Katalog es nicht anders behandeln muss - nur die Herkunft ist eine andere, und sie steht in
/// <see cref="SourceUrl"/>.
/// </summary>
public sealed class ResearchedBrandAsset
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string BrandKey { get; set; } = string.Empty;
    public string CanonicalName { get; set; } = string.Empty;
    public string LogoKey { get; set; } = string.Empty;
    public string MediaType { get; set; } = "image/svg+xml";
    public string ContentSha256 { get; set; } = string.Empty;
    public int ByteLength { get; set; }
    public string? SourceUrl { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ResearchedBrandAlias
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string AliasKey { get; set; } = string.Empty;
    public string BrandKey { get; set; } = string.Empty;
    public string Country { get; set; } = "GLOBAL";
}

/// <summary>
/// Ein Versuch, zu einem Haendlernamen ein Logo zu finden - mit seinem Ergebnis (#176).
///
/// Das ist die Antwort auf "wie oft": EIN Versuch je Name. Ohne dieses Gedaechtnis wuerde ein frischer
/// Import mit hunderten unbekannten Haendlern hunderte Aufrufe ausloesen, und bei jedem weiteren
/// Import wieder. Auch ein Misserfolg wird gemerkt; nach <see cref="RetryAfterDays"/> Tagen darf es
/// erneut versucht werden, weil eine Marke inzwischen ein SVG bereitstellen kann.
///
/// Gemerkt wird der HASH des Namens, nicht der Name. Diese Tabelle gehoert der Instanz und ueberlebt
/// das Loeschen eines Kontos - und eine Gegenpartei ist nicht immer eine Firma: eine Ueberweisung von
/// "Max Mustermann" haette hier sonst dauerhaft einen Personennamen hinterlassen, den niemand mehr
/// loeschen kann. Fuer die einzige Frage, die diese Tabelle beantwortet ("schon versucht?"), ist der
/// Hash genau so gut. Ein <see cref="ResearchedBrandAlias"/> traegt den Namen im Klartext, aber den
/// gibt es nur, wenn wirklich ein Logo gefunden wurde - also nur fuer eine Marke mit eigener Website.
/// </summary>
public sealed class BrandLogoResearchAttempt
{
    public const int RetryAfterDays = 30;

    public Guid Id { get; set; } = Guid.NewGuid();
    public string AliasHash { get; set; } = string.Empty;
    public string Outcome { get; set; } = string.Empty;
    public string? Domain { get; set; }
    public DateTimeOffset AttemptedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class OfficialBrandAlias
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string AliasKey { get; set; } = string.Empty;
    public string BrandKey { get; set; } = string.Empty;
    public string Country { get; set; } = "GLOBAL";
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


public static class IntelligenceCatalogModelConfiguration
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OfficialMerchantMapping>(entity =>
        {
            entity.HasIndex(x => new { x.AliasKey, x.Direction, x.Country }).IsUnique();
            entity.HasIndex(x => x.CanonicalMerchantKey);
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

        modelBuilder.Entity<ResearchedBrandAsset>(entity =>
        {
            entity.HasIndex(x => x.BrandKey).IsUnique();
            entity.HasIndex(x => x.ContentSha256);
            entity.Property(x => x.BrandKey).HasMaxLength(120);
            entity.Property(x => x.CanonicalName).HasMaxLength(200);
            entity.Property(x => x.LogoKey).HasMaxLength(120);
            entity.Property(x => x.MediaType).HasMaxLength(80);
            entity.Property(x => x.ContentSha256).HasMaxLength(64);
            entity.Property(x => x.SourceUrl).HasMaxLength(1000);
        });

        modelBuilder.Entity<ResearchedBrandAlias>(entity =>
        {
            entity.HasIndex(x => new { x.AliasKey, x.Country }).IsUnique();
            entity.HasIndex(x => x.BrandKey);
            entity.Property(x => x.AliasKey).HasMaxLength(300);
            entity.Property(x => x.BrandKey).HasMaxLength(120);
            entity.Property(x => x.Country).HasMaxLength(8);
        });

        modelBuilder.Entity<BrandLogoResearchAttempt>(entity =>
        {
            entity.HasIndex(x => x.AliasHash).IsUnique();
            entity.Property(x => x.AliasHash).HasMaxLength(64);
            entity.Property(x => x.Outcome).HasMaxLength(40);
            entity.Property(x => x.Domain).HasMaxLength(253);
        });

        modelBuilder.Entity<OfficialBrandAlias>(entity =>
        {
            entity.HasIndex(x => new { x.AliasKey, x.Country }).IsUnique();
            entity.HasIndex(x => x.BrandKey);
            entity.Property(x => x.AliasKey).HasMaxLength(300);
            entity.Property(x => x.BrandKey).HasMaxLength(120);
            entity.Property(x => x.Country).HasMaxLength(8);
        });

    }
}
