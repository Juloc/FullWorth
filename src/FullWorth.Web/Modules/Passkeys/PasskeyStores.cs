using FullWorth.Web.Modules.Auth;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Web.Modules.Passkeys;

public interface IPasskeyStore
{
    Task<PasskeyCredential?> GetByCredentialIdAsync(byte[] credentialId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PasskeyCredential>> ListAsync(Guid authUserId, CancellationToken cancellationToken = default);
    Task<bool> CredentialIdExistsAsync(byte[] credentialId, CancellationToken cancellationToken = default);
    Task CreateAsync(PasskeyCredential credential, CancellationToken cancellationToken = default);
    Task<bool> UpdateAfterAssertionAsync(Guid authUserId, byte[] credentialId, uint expectedSignatureCounter, uint newSignatureCounter, bool isBackedUp, DateTimeOffset lastUsedAt, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(Guid authUserId, Guid credentialRecordId, CancellationToken cancellationToken = default);
}

public sealed class PasskeyStore(DbContext db) : IPasskeyStore
{
    public Task<PasskeyCredential?> GetByCredentialIdAsync(byte[] credentialId, CancellationToken cancellationToken = default) =>
        db.Set<PasskeyCredential>().AsNoTracking().SingleOrDefaultAsync(x => x.CredentialId.SequenceEqual(credentialId), cancellationToken);

    public async Task<IReadOnlyList<PasskeyCredential>> ListAsync(Guid authUserId, CancellationToken cancellationToken = default) =>
        await db.Set<PasskeyCredential>().AsNoTracking().Where(x => x.AuthUserId == authUserId).OrderByDescending(x => x.CreatedAt).ToListAsync(cancellationToken);

    public Task<bool> CredentialIdExistsAsync(byte[] credentialId, CancellationToken cancellationToken = default) =>
        db.Set<PasskeyCredential>().AsNoTracking().AnyAsync(x => x.CredentialId.SequenceEqual(credentialId), cancellationToken);

    public async Task CreateAsync(PasskeyCredential credential, CancellationToken cancellationToken = default)
    {
        db.Set<PasskeyCredential>().Add(credential);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> UpdateAfterAssertionAsync(Guid authUserId, byte[] credentialId, uint expectedSignatureCounter, uint newSignatureCounter, bool isBackedUp, DateTimeOffset lastUsedAt, CancellationToken cancellationToken = default)
    {
        var affected = await db.Set<PasskeyCredential>()
            .Where(x => x.AuthUserId == authUserId && x.CredentialId.SequenceEqual(credentialId) && x.SignatureCounter == expectedSignatureCounter)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.SignatureCounter, newSignatureCounter)
                .SetProperty(x => x.IsBackedUp, isBackedUp)
                .SetProperty(x => x.LastUsedAt, lastUsedAt), cancellationToken);
        return affected == 1;
    }

    public async Task<bool> DeleteAsync(Guid authUserId, Guid credentialRecordId, CancellationToken cancellationToken = default)
    {
        var affected = await db.Set<PasskeyCredential>()
            .Where(x => x.AuthUserId == authUserId && x.Id == credentialRecordId)
            .ExecuteDeleteAsync(cancellationToken);
        return affected == 1;
    }
}

public interface IPasskeyChallengeStore
{
    Task CreateAsync(PasskeyChallenge challenge, CancellationToken cancellationToken = default);
    Task<PasskeyChallenge?> ConsumeAsync(Guid challengeId, PasskeyChallengeType type, Guid? authUserId, DateTimeOffset now, CancellationToken cancellationToken = default);
}

public sealed class PasskeyChallengeStore(DbContext db) : IPasskeyChallengeStore
{
    public async Task CreateAsync(PasskeyChallenge challenge, CancellationToken cancellationToken = default)
    {
        db.Set<PasskeyChallenge>().Add(challenge);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<PasskeyChallenge?> ConsumeAsync(Guid challengeId, PasskeyChallengeType type, Guid? authUserId, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var query = db.Set<PasskeyChallenge>()
            .Where(x => x.Id == challengeId && x.Type == type && x.ConsumedAt == null && x.ExpiresAt > now);

        query = authUserId.HasValue
            ? query.Where(x => x.AuthUserId == authUserId.Value)
            : query.Where(x => x.AuthUserId == null);

        var affected = await query.ExecuteUpdateAsync(setters => setters.SetProperty(x => x.ConsumedAt, now), cancellationToken);
        if (affected != 1) return null;

        return await db.Set<PasskeyChallenge>().AsNoTracking().SingleAsync(x => x.Id == challengeId, cancellationToken);
    }
}

public static class PasskeyModelConfiguration
{
    public static void ConfigurePasskeys(this ModelBuilder builder)
    {
        builder.Entity<PasskeyCredential>(entity =>
        {
            entity.ToTable("PasskeyCredentials", "auth");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.CredentialId).IsRequired().HasMaxLength(1024);
            entity.Property(x => x.PublicKey).IsRequired().HasMaxLength(4096);
            entity.Property(x => x.UserHandle).IsRequired().HasMaxLength(64);
            entity.Property(x => x.SignatureCounter).HasConversion<long>().HasColumnType("bigint").IsRequired();
            entity.Property(x => x.DisplayName).IsRequired().HasMaxLength(PasskeyOptions.CredentialDisplayNameMaxLength);
            entity.Property(x => x.CreatedAt).IsRequired();
            entity.HasIndex(x => x.CredentialId).IsUnique();
            entity.HasIndex(x => x.AuthUserId);
            entity.HasOne<AuthUser>().WithMany().HasForeignKey(x => x.AuthUserId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<PasskeyChallenge>(entity =>
        {
            entity.ToTable("PasskeyChallenges", "auth", table =>
            {
                table.HasCheckConstraint("CK_PasskeyChallenges_Type", "\"Type\" IN (1, 2)");
                table.HasCheckConstraint("CK_PasskeyChallenges_Expiry", "\"ExpiresAt\" > \"CreatedAt\"");
                table.HasCheckConstraint("CK_PasskeyChallenges_ConsumedAt", "\"ConsumedAt\" IS NULL OR \"ConsumedAt\" >= \"CreatedAt\"");
            });
            entity.HasKey(x => x.Id);
            entity.Property(x => x.OptionsJson).IsRequired().HasMaxLength(32768);
            entity.Property(x => x.CreatedAt).IsRequired();
            entity.Property(x => x.ExpiresAt).IsRequired();
            entity.HasIndex(x => x.AuthUserId);
            entity.HasIndex(x => new { x.Type, x.ExpiresAt, x.ConsumedAt });
            entity.HasOne<AuthUser>().WithMany().HasForeignKey(x => x.AuthUserId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
