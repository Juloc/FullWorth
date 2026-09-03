using System;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Migrations;

/// <summary>
/// Frozen snapshot delta for the Coach conversations + Spending Review model introduced by the
/// 20260901230000_CoachAndSpendingReviews migration. That migration creates the tables via raw SQL
/// (idempotent CREATE TABLE IF NOT EXISTS) and therefore did not update the EF model snapshot, so the
/// entities were missing from the baseline and tripped PendingModelChangesWarning at startup migration.
/// Keep this string-based so later CLR model changes remain visible as pending model changes.
/// </summary>
internal static class CoachSnapshot
{
    private const string Conversation = "FullWorth.Backend.Modules.Coach.CoachConversation";
    private const string Message = "FullWorth.Backend.Modules.Coach.CoachMessage";
    private const string Review = "FullWorth.Backend.Modules.Coach.SpendingReview";

    internal static void Apply(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity(Conversation, b =>
        {
            b.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("uuid");
            b.Property<DateTimeOffset?>("ArchivedAt").HasColumnType("timestamp with time zone");
            b.Property<DateTimeOffset>("CreatedAt").HasColumnType("timestamp with time zone");
            b.Property<Guid>("FullWorthSpaceId").HasColumnType("uuid");
            b.Property<string>("MascotId").HasMaxLength(50).HasColumnType("character varying(50)");
            b.Property<string>("Title").IsRequired().HasMaxLength(120).HasColumnType("character varying(120)");
            b.Property<DateTimeOffset>("UpdatedAt").HasColumnType("timestamp with time zone");
            b.Property<Guid>("UserId").HasColumnType("uuid");
            b.HasKey("Id");
            b.HasIndex("UserId");
            b.HasIndex("FullWorthSpaceId", "UserId", "UpdatedAt").IsDescending(false, false, true);
            b.ToTable("CoachConversations", (string)null);
        });

        modelBuilder.Entity(Message, b =>
        {
            b.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("uuid");
            b.Property<Guid>("ConversationId").HasColumnType("uuid");
            b.Property<DateTimeOffset>("CreatedAt").HasColumnType("timestamp with time zone");
            b.Property<string>("FactsJson").HasColumnType("jsonb");
            b.Property<string>("Mode").IsRequired().HasMaxLength(20).HasColumnType("character varying(20)");
            b.Property<string>("Model").HasMaxLength(120).HasColumnType("character varying(120)");
            b.Property<string>("Provider").HasMaxLength(64).HasColumnType("character varying(64)");
            b.Property<string>("Role").IsRequired().HasMaxLength(16).HasColumnType("character varying(16)");
            b.Property<string>("Text").IsRequired().HasMaxLength(12000).HasColumnType("character varying(12000)");
            b.HasKey("Id");
            b.HasIndex("ConversationId", "CreatedAt");
            b.ToTable("CoachMessages", (string)null);
        });

        modelBuilder.Entity(Review, b =>
        {
            b.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("uuid");
            b.Property<DateTimeOffset>("CreatedAt").HasColumnType("timestamp with time zone");
            b.Property<Guid>("FullWorthSpaceId").HasColumnType("uuid");
            b.Property<string>("Note").HasMaxLength(500).HasColumnType("character varying(500)");
            b.Property<Guid?>("PurchaseId").HasColumnType("uuid");
            b.Property<string>("ReasonsJson").IsRequired().HasColumnType("jsonb");
            b.Property<string>("Sentiment").IsRequired().HasMaxLength(16).HasColumnType("character varying(16)");
            b.Property<Guid>("TransactionId").HasColumnType("uuid");
            b.Property<DateTimeOffset>("UpdatedAt").HasColumnType("timestamp with time zone");
            b.Property<Guid>("UserId").HasColumnType("uuid");
            b.HasKey("Id");
            b.HasIndex("PurchaseId");
            b.HasIndex("TransactionId");
            b.HasIndex("UserId");
            b.HasIndex("FullWorthSpaceId", "UserId", "TransactionId").IsUnique();
            b.HasIndex("FullWorthSpaceId", "UserId", "UpdatedAt").IsDescending(false, false, true);
            b.ToTable("SpendingReviews", (string)null);
        });

        modelBuilder.Entity(Conversation, b =>
        {
            b.HasOne("FullWorth.Backend.Modules.FullWorthSpaces.FullWorthSpace", null)
                .WithMany().HasForeignKey("FullWorthSpaceId").OnDelete(DeleteBehavior.Restrict).IsRequired();
            b.HasOne("FullWorth.Backend.Modules.Users.FullWorthUser", null)
                .WithMany().HasForeignKey("UserId").OnDelete(DeleteBehavior.Restrict).IsRequired();
        });

        modelBuilder.Entity(Message, b =>
        {
            b.HasOne("FullWorth.Backend.Modules.Coach.CoachConversation", null)
                .WithMany().HasForeignKey("ConversationId").OnDelete(DeleteBehavior.Cascade).IsRequired();
        });

        modelBuilder.Entity(Review, b =>
        {
            b.HasOne("FullWorth.Backend.Modules.FullWorthSpaces.FullWorthSpace", null)
                .WithMany().HasForeignKey("FullWorthSpaceId").OnDelete(DeleteBehavior.Restrict).IsRequired();
            b.HasOne("FullWorth.Backend.Modules.Purchases.Purchase", null)
                .WithMany().HasForeignKey("PurchaseId").OnDelete(DeleteBehavior.SetNull);
            b.HasOne("FullWorth.Backend.Modules.Transactions.FinanceTransaction", null)
                .WithMany().HasForeignKey("TransactionId").OnDelete(DeleteBehavior.Cascade).IsRequired();
            b.HasOne("FullWorth.Backend.Modules.Users.FullWorthUser", null)
                .WithMany().HasForeignKey("UserId").OnDelete(DeleteBehavior.Restrict).IsRequired();
        });
    }
}
