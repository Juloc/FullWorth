using FullWorth.Web.Modules.Auth;
using FullWorth.Web.Modules.Admin;
using FullWorth.Web.Modules.Passkeys;
using FullWorth.Web.Modules.Recovery;
using FullWorth.Web.Modules.Sessions;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Web.Data;

public sealed class AuthDbContext(DbContextOptions<AuthDbContext> options)
    : IdentityUserContext<AuthUser, Guid>(options)
{
    public DbSet<UserSession> UserSessions => Set<UserSession>();
    public DbSet<AdminAuditEvent> AdminAuditEvents => Set<AdminAuditEvent>();
    public DbSet<RecoveryCode> RecoveryCodes => Set<RecoveryCode>();
    public DbSet<PasskeyCredential> PasskeyCredentials => Set<PasskeyCredential>();
    public DbSet<PasskeyChallenge> PasskeyChallenges => Set<PasskeyChallenge>();
    public DbSet<FullWorth.Web.Modules.Admin.ExternalAuthSettings> ExternalAuthSettings => Set<FullWorth.Web.Modules.Admin.ExternalAuthSettings>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.HasDefaultSchema("auth");

        builder.Entity<FullWorth.Web.Modules.Admin.ExternalAuthSettings>(entity =>
        {
            // Exactly one row, enforced rather than assumed: a second "instance" row would make which
            // sign-in providers this installation offers depend on insertion order.
            entity.HasIndex(x => x.ScopeKey).IsUnique();
            entity.Property(x => x.ScopeKey).IsRequired().HasMaxLength(40);
            entity.Property(x => x.GoogleClientId).IsRequired().HasMaxLength(200);
            entity.Property(x => x.AppleServiceId).IsRequired().HasMaxLength(200);
            entity.Property(x => x.AppleTeamId).IsRequired().HasMaxLength(64);
            entity.Property(x => x.ApplePrivateKeyId).IsRequired().HasMaxLength(64);
            // text, not a bounded column: these hold data-protection ciphertext, whose length depends
            // on the key ring rather than on what was typed. An Apple .p8 key is a whole PEM block.
            entity.Property(x => x.GoogleClientSecretProtected).IsRequired().HasColumnType("text");
            entity.Property(x => x.ApplePrivateKeyProtected).IsRequired().HasColumnType("text");
        });

        builder.Entity<AuthUser>(entity =>
        {
            entity.Property(x => x.Email).IsRequired().HasMaxLength(256);
            entity.Property(x => x.NormalizedEmail).IsRequired().HasMaxLength(256);
            entity.Property(x => x.UserName).IsRequired().HasMaxLength(256);
            entity.Property(x => x.NormalizedUserName).IsRequired().HasMaxLength(256);
            entity.Property(x => x.FinanceUserId).IsRequired();
            entity.Property(x => x.IsDisabled).IsRequired();
            entity.Property(x => x.IsAdmin).IsRequired();
            entity.Property(x => x.DeletionRequestedAt);
            entity.Property(x => x.DeletionScheduledFor);
            entity.Property(x => x.DeletionLeaseUntil);
            entity.Property(x => x.DeletionLastError).HasMaxLength(120);
            entity.Property(x => x.CreatedAt).IsRequired();
            entity.Property(x => x.UpdatedAt).IsRequired();

            entity.HasIndex(x => x.NormalizedEmail)
                .IsUnique()
                .HasDatabaseName("EmailIndex");

            entity.HasIndex(x => x.FinanceUserId)
                .IsUnique()
                .HasDatabaseName("FinanceUserIdIndex");

            entity.HasIndex(x => x.DeletionScheduledFor)
                .HasDatabaseName("DeletionScheduledForIndex");
        });

        builder.Entity<AdminAuditEvent>(entity =>
        {
            entity.ToTable("AdminAuditEvents");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.ActorAuthUserId).IsRequired();
            entity.Property(x => x.Action).IsRequired().HasMaxLength(80);
            entity.Property(x => x.Outcome).IsRequired().HasMaxLength(40);
            entity.Property(x => x.OccurredAt).IsRequired();
            entity.HasIndex(x => x.OccurredAt);
            entity.HasIndex(x => x.TargetAuthUserId);
        });

        builder.Entity<UserSession>(entity =>
        {
            entity.ToTable("UserSessions");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.AuthUserId).IsRequired();
            entity.Property(x => x.CreatedAt).IsRequired();
            entity.Property(x => x.LastSeenAt).IsRequired();
            entity.Property(x => x.ExpiresAt).IsRequired();
            entity.Property(x => x.AbsoluteExpiresAt).IsRequired();
            entity.Property(x => x.DeviceName).IsRequired().HasMaxLength(UserSession.MaxDeviceNameLength);
            entity.Property(x => x.UserAgent).HasMaxLength(UserSession.MaxUserAgentLength);
            entity.Property(x => x.IpAddress).HasMaxLength(UserSession.MaxIpAddressLength);
            entity.Property(x => x.SecurityStampAtIssue).HasMaxLength(UserSession.MaxSecurityStampLength);

            entity.HasIndex(x => x.AuthUserId);
            entity.HasIndex(x => x.RevokedAt);
            entity.HasIndex(x => x.AbsoluteExpiresAt);

            entity.HasOne<AuthUser>()
                .WithMany()
                .HasForeignKey(x => x.AuthUserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<RecoveryCode>(entity =>
        {
            entity.ToTable("RecoveryCodes", table =>
                table.HasCheckConstraint("CK_RecoveryCodes_CodeHash_Length", "octet_length(\"CodeHash\") = 32"));
            entity.HasKey(x => x.Id);
            entity.Property(x => x.AuthUserId).IsRequired();
            entity.Property(x => x.CodeHash).IsRequired().HasMaxLength(32);
            entity.Property(x => x.CreatedAt).IsRequired();
            entity.Property(x => x.UsedAt);

            entity.HasIndex(x => new { x.AuthUserId, x.CodeHash }).IsUnique();

            entity.HasOne<AuthUser>()
                .WithMany()
                .HasForeignKey(x => x.AuthUserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.ConfigurePasskeys();
    }

    public override int SaveChanges()
    {
        UpdateTimestamps();
        return base.SaveChanges();
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        UpdateTimestamps();
        return base.SaveChangesAsync(cancellationToken);
    }

    private void UpdateTimestamps()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var entry in ChangeTracker.Entries<AuthUser>())
        {
            if (entry.State == EntityState.Added)
            {
                if (entry.Entity.CreatedAt == default)
                    entry.Entity.CreatedAt = now;
                entry.Entity.UpdatedAt = now;
            }
            else if (entry.State == EntityState.Modified)
            {
                entry.Entity.UpdatedAt = now;
            }
        }
    }
}
