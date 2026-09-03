using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Purchases;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Modules.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace FullWorth.Backend.Modules.Coach;

public sealed class CoachModelCustomizer(ModelCustomizerDependencies dependencies) : ModelCustomizer(dependencies)
{
    public override void Customize(ModelBuilder modelBuilder, DbContext context)
    {
        base.Customize(modelBuilder, context);

        modelBuilder.Entity<SpendingReview>(entity =>
        {
            entity.ToTable("SpendingReviews");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.FullWorthSpaceId, x.UserId, x.TransactionId }).IsUnique();
            entity.HasIndex(x => new { x.FullWorthSpaceId, x.UserId, x.UpdatedAt }).IsDescending(false, false, true);
            entity.Property(x => x.Sentiment).HasConversion<string>().HasMaxLength(16);
            entity.Property(x => x.ReasonsJson).HasColumnType("jsonb");
            entity.Property(x => x.Note).HasMaxLength(500);
            entity.HasOne<FullWorthSpace>().WithMany().HasForeignKey(x => x.FullWorthSpaceId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<FullWorthUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<FinanceTransaction>().WithMany().HasForeignKey(x => x.TransactionId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<Purchase>().WithMany().HasForeignKey(x => x.PurchaseId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<CoachConversation>(entity =>
        {
            entity.ToTable("CoachConversations");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.FullWorthSpaceId, x.UserId, x.UpdatedAt }).IsDescending(false, false, true);
            entity.Property(x => x.Title).HasMaxLength(120);
            entity.Property(x => x.MascotId).HasMaxLength(50);
            entity.HasOne<FullWorthSpace>().WithMany().HasForeignKey(x => x.FullWorthSpaceId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<FullWorthUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<CoachMessage>(entity =>
        {
            entity.ToTable("CoachMessages");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.ConversationId, x.CreatedAt });
            entity.Property(x => x.Role).HasConversion<string>().HasMaxLength(16);
            entity.Property(x => x.Mode).HasConversion<string>().HasMaxLength(20);
            entity.Property(x => x.Text).HasMaxLength(12000);
            entity.Property(x => x.FactsJson).HasColumnType("jsonb");
            entity.Property(x => x.Provider).HasMaxLength(64);
            entity.Property(x => x.Model).HasMaxLength(120);
            entity.HasOne<CoachConversation>().WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
