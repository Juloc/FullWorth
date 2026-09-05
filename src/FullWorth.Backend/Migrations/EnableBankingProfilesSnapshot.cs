using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Migrations;

/// <summary>Snapshot delta for per-user Enable Banking profiles introduced 2026-09-05.</summary>
internal static class EnableBankingProfilesSnapshot
{
    internal static void Apply(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity("FullWorth.Backend.Modules.BankConnections.EnableBankingProfile", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("uuid");
            b.Property<bool>("Active").HasColumnType("boolean");
            b.Property<string>("ApplicationId").IsRequired().HasMaxLength(128).HasColumnType("character varying(128)");
            b.Property<string>("ApplicationName").IsRequired().HasMaxLength(200).HasColumnType("character varying(200)");
            b.Property<DateTimeOffset>("CreatedAt").HasColumnType("timestamp with time zone");
            b.Property<string>("Environment").IsRequired().HasMaxLength(24).HasColumnType("character varying(24)");
            b.Property<string>("KeyFingerprint").IsRequired().HasMaxLength(128).HasColumnType("character varying(128)");
            b.Property<string>("PrivateKeyPem").IsRequired().HasColumnType("text");
            b.Property<string>("RedirectUrlsJson").IsRequired().HasColumnType("jsonb");
            b.Property<string>("ServicesJson").IsRequired().HasColumnType("jsonb");
            b.Property<DateTimeOffset?>("VerifiedAt").HasColumnType("timestamp with time zone");
            b.Property<DateTimeOffset>("UpdatedAt").HasColumnType("timestamp with time zone");
            b.Property<Guid>("UserId").HasColumnType("uuid");

            b.HasKey("Id");
            b.HasIndex("ApplicationId");
            b.HasIndex("UserId").IsUnique();
            b.ToTable("EnableBankingProfiles");
        });

        modelBuilder.Entity("FullWorth.Backend.Modules.BankConnections.BankConnection", b =>
        {
            b.Property<string>("AuthMethod").HasMaxLength(120).HasColumnType("character varying(120)");
            b.Property<Guid?>("EnableBankingProfileId").HasColumnType("uuid");
            b.Property<string>("PsuType").IsRequired().HasMaxLength(16).HasColumnType("character varying(16)");
            b.Property<string>("RequiredPsuHeadersJson").IsRequired().HasColumnType("jsonb");
            b.HasIndex("EnableBankingProfileId");
        });

        modelBuilder.Entity("FullWorth.Backend.Modules.BankConnections.EnableBankingProfile", b =>
        {
            b.HasOne("FullWorth.Backend.Modules.Users.FullWorthUser", null)
                .WithMany()
                .HasForeignKey("UserId")
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired();
        });

        modelBuilder.Entity("FullWorth.Backend.Modules.BankConnections.BankConnection", b =>
        {
            b.HasOne("FullWorth.Backend.Modules.BankConnections.EnableBankingProfile", null)
                .WithMany()
                .HasForeignKey("EnableBankingProfileId")
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
