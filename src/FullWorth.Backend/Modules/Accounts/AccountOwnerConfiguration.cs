using FullWorth.Backend.Modules.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FullWorth.Backend.Modules.Accounts;

public sealed class AccountOwnerConfiguration : IEntityTypeConfiguration<AccountOwner>
{
    public void Configure(EntityTypeBuilder<AccountOwner> entity)
    {
        entity.HasKey(x => new { x.AccountId, x.UserId });
        entity.HasIndex(x => new { x.UserId, x.AccountId });
        entity.Property(x => x.OwnershipType).HasMaxLength(16).IsRequired();
        entity.ToTable(table => table.HasCheckConstraint(
            "CK_AccountOwners_OwnershipType",
            "\"OwnershipType\" IN ('owner', 'viewer')"));

        entity.HasOne(x => x.Account)
            .WithMany(x => x.Owners)
            .HasForeignKey(x => x.AccountId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne<FullWorthUser>()
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
