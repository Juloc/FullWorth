using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Intelligence;

public sealed class LearnedMerchantMapping
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FullWorthSpaceId { get; set; }
    public Guid CreatedByUserId { get; set; }
    public string NormalizedCounterparty { get; set; } = string.Empty;
    public string Direction { get; set; } = "expense";
    public Guid CategoryId { get; set; }
    public string Source { get; set; } = "user-confirmed";
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public static class LearnedMerchantMappingModelConfiguration
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<LearnedMerchantMapping>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.FullWorthSpaceId, x.NormalizedCounterparty, x.Direction }).IsUnique();
            entity.HasIndex(x => new { x.FullWorthSpaceId, x.IsActive });
            entity.Property(x => x.NormalizedCounterparty).HasMaxLength(320);
            entity.Property(x => x.Direction).HasMaxLength(16);
            entity.Property(x => x.Source).HasMaxLength(40);
        });
    }
}

public readonly record struct LearnedMerchantCategoryMapping(
    string NormalizedCounterparty,
    string Direction,
    Guid CategoryId);
