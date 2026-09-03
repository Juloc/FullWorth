using FullWorth.Backend.Modules.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FullWorth.Backend.Modules.FullWorthSpaces;

public static class FullWorthSpaceDefaults
{
    public static readonly Guid LegacyId = Guid.Parse("7b21b1a4-0b7b-4ae1-93d0-b8d1f859e8a1");
    public static readonly DateTimeOffset LegacyCreatedAt = new(2026, 8, 11, 0, 0, 0, TimeSpan.Zero);
    public const string LegacyName = "Default";
    public const string LegacyBaseCurrency = "EUR";
}

public sealed class FullWorthSpaceConfiguration : IEntityTypeConfiguration<FullWorthSpace>
{
    public void Configure(EntityTypeBuilder<FullWorthSpace> entity)
    {
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Name).HasMaxLength(200).IsRequired();
        entity.Property(x => x.BaseCurrency).HasMaxLength(3).IsRequired();
        entity.HasData(new FullWorthSpace
        {
            Id = FullWorthSpaceDefaults.LegacyId,
            Name = FullWorthSpaceDefaults.LegacyName,
            BaseCurrency = FullWorthSpaceDefaults.LegacyBaseCurrency,
            CreatedAt = FullWorthSpaceDefaults.LegacyCreatedAt,
            UpdatedAt = FullWorthSpaceDefaults.LegacyCreatedAt
        });
    }
}

public sealed class FullWorthSpaceInviteConfiguration : IEntityTypeConfiguration<FullWorthSpaceInvite>
{
    public void Configure(EntityTypeBuilder<FullWorthSpaceInvite> entity)
    {
        entity.HasKey(x => x.Id);
        entity.HasIndex(x => new { x.FullWorthSpaceId, x.Status });
        entity.HasIndex(x => x.TokenHash).IsUnique();
        entity.Property(x => x.EmailNormalized).HasMaxLength(320).IsRequired();
        entity.Property(x => x.SpaceRole).HasMaxLength(16).IsRequired();
        entity.Property(x => x.TokenHash).HasMaxLength(128).IsRequired();
        // Requested grants are read only at claim time, in memory — plain text, never queried in LINQ.
        entity.Property(x => x.AccountGrantsJson).HasColumnType("text").IsRequired();
        entity.Property(x => x.Status).HasMaxLength(16).IsRequired();
        entity.ToTable(table =>
        {
            table.HasCheckConstraint("CK_FullWorthSpaceInvites_Role", "\"SpaceRole\" IN ('owner', 'member')");
            table.HasCheckConstraint("CK_FullWorthSpaceInvites_Status", "\"Status\" IN ('pending', 'claimed', 'revoked')");
        });
        // Restrict: an invite belongs to a space and must not silently vanish if the space row is touched;
        // matches the fullworth-history retention rule used across the schema.
        entity.HasOne<FullWorthSpace>()
            .WithMany()
            .HasForeignKey(x => x.FullWorthSpaceId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class FullWorthSpaceMemberConfiguration : IEntityTypeConfiguration<FullWorthSpaceMember>
{
    public void Configure(EntityTypeBuilder<FullWorthSpaceMember> entity)
    {
        entity.HasKey(x => new { x.FullWorthSpaceId, x.UserId });
        entity.HasIndex(x => new { x.UserId, x.FullWorthSpaceId });
        entity.HasIndex(x => new { x.FullWorthSpaceId, x.Role });
        entity.Property(x => x.Role).HasMaxLength(16).IsRequired();
        entity.ToTable(table => table.HasCheckConstraint(
            "CK_FullWorthSpaceMembers_Role",
            "\"Role\" IN ('owner', 'member')"));

        entity.HasOne(x => x.FullWorthSpace)
            .WithMany(x => x.Members)
            .HasForeignKey(x => x.FullWorthSpaceId)
            .OnDelete(DeleteBehavior.Restrict);

        entity.HasOne<FullWorthUser>()
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
