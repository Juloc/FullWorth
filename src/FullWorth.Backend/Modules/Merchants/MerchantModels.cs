using FullWorth.Backend.Modules.FullWorthSpaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FullWorth.Backend.Modules.Merchants;

public sealed class Merchant
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FullWorthSpaceId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string NormalizedName { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class MerchantAlias
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid MerchantId { get; set; }
    public Guid FullWorthSpaceId { get; set; }
    public string NormalizedAlias { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class MerchantConfiguration : IEntityTypeConfiguration<Merchant>
{
    public void Configure(EntityTypeBuilder<Merchant> e)
    {
        e.ToTable("Merchants");
        e.HasKey(x => x.Id);
        e.Property(x => x.Name).HasMaxLength(200);
        e.Property(x => x.NormalizedName).HasMaxLength(200);
        e.HasIndex(x => new { x.FullWorthSpaceId, x.NormalizedName }).IsUnique();
        e.HasOne<FullWorthSpace>().WithMany().HasForeignKey(x => x.FullWorthSpaceId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class MerchantAliasConfiguration : IEntityTypeConfiguration<MerchantAlias>
{
    public void Configure(EntityTypeBuilder<MerchantAlias> e)
    {
        e.ToTable("MerchantAliases");
        e.HasKey(x => x.Id);
        e.Property(x => x.NormalizedAlias).HasMaxLength(200);
        e.HasIndex(x => new { x.FullWorthSpaceId, x.NormalizedAlias }).IsUnique();
        e.HasIndex(x => x.MerchantId);
        e.HasOne<Merchant>().WithMany().HasForeignKey(x => x.MerchantId).OnDelete(DeleteBehavior.Cascade);
        e.HasOne<FullWorthSpace>().WithMany().HasForeignKey(x => x.FullWorthSpaceId).OnDelete(DeleteBehavior.Restrict);
    }
}
