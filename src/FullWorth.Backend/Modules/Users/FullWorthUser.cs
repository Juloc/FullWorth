using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FullWorth.Backend.Modules.Users;

public sealed class FullWorthUser
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string EmailNormalized { get; set; } = string.Empty;
    [NotMapped]
    public string Email => EmailNormalized;
    public string DisplayName { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class FullWorthUserConfiguration : IEntityTypeConfiguration<FullWorthUser>
{
    public void Configure(EntityTypeBuilder<FullWorthUser> entity)
    {
        entity.HasKey(x => x.Id);
        entity.HasIndex(x => x.EmailNormalized).IsUnique();
        entity.Property(x => x.EmailNormalized).HasMaxLength(320).IsRequired();
        entity.Property(x => x.DisplayName).HasMaxLength(200).IsRequired();
    }
}