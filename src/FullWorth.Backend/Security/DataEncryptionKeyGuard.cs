using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Security;

/// <summary>
/// One row, naming the data encryption key this installation's rows were written with.
///
/// It holds a derived fingerprint, never the key — the whole point is that it can sit in the same
/// database as the ciphertext without weakening it.
/// </summary>
public sealed class InstallationEncryptionMarker
{
    public const string InstanceScopeKey = "instance";

    public Guid Id { get; set; } = Guid.NewGuid();
    public string ScopeKey { get; set; } = InstanceScopeKey;
    public string KeyFingerprint { get; set; } = string.Empty;
    public DateTimeOffset FirstSeenAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Refuses to start when the data encryption key cannot be the one this database was encrypted with.
///
/// The failure this exists for is silent, total and irreversible. <c>data_encryption_key</c> lives in
/// a Docker volume; the app creates it when it is missing. So a container started against an existing
/// database with an empty or wrong secrets volume generates a brand-new key, starts perfectly, reports
/// itself healthy — and every encrypted column is now unreadable. No Postgres backup restores it,
/// because the data in Postgres was never the missing part.
///
/// Making the volume external protected only against <c>docker compose down -v</c>, and only in that
/// one folder. It did nothing about a wrong mount, a restored-from-the-wrong-backup volume, or a host
/// migration that forgot it — and the price was a manual <c>docker volume create</c> before the first
/// start of every new host, which is exactly the kind of step this deployment is trying to be rid of.
/// Detecting the damage is strictly stronger than guarding one command that can cause it.
///
/// Two signals, because one of them has to work on the very first start after this guard ships:
///
///   1. The key was created moments ago, in this process, and the database already holds data. That
///      is proof, not suspicion: a key that did not exist a second ago cannot have encrypted rows
///      written last week. Needs no marker, so it protects the first upgraded start too.
///   2. The stored fingerprint differs from the key in use. This is the durable one, and it also
///      catches a swap to a different key that already existed.
/// </summary>
public static class DataEncryptionKeyGuard
{
    public static async Task EnsureKeyBelongsToInstallationAsync(
        FullWorthDbContext db,
        FieldCipher cipher,
        bool keyCreatedNow,
        CancellationToken ct)
    {
        // No key configured at all is Development and Testing, where FieldCipher is the identity
        // cipher and nothing is encrypted. Production cannot reach here without one: FieldCipher
        // itself refuses to be constructed.
        if (!cipher.Enabled || cipher.Fingerprint is not { } fingerprint) return;

        var marker = await db.Set<InstallationEncryptionMarker>()
            .SingleOrDefaultAsync(x => x.ScopeKey == InstallationEncryptionMarker.InstanceScopeKey, ct);

        if (marker is not null)
        {
            if (string.Equals(marker.KeyFingerprint, fingerprint, StringComparison.Ordinal)) return;

            throw new InvalidOperationException(
                $"This database was encrypted with data encryption key {marker.KeyFingerprint}, but " +
                $"the key in use is {fingerprint}. Starting would leave every encrypted column " +
                "unreadable, so FullWorth is refusing to. The fullworth-platform-secrets volume is " +
                "most likely missing, empty, or a different one than this database belongs to — " +
                "restore the volume holding data_encryption_key. A Postgres backup alone cannot " +
                "repair this, and the app will not re-encrypt anything by itself.");
        }

        if (keyCreatedNow && await HasDataAsync(db, ct))
            throw new InvalidOperationException(
                $"A new data encryption key ({fingerprint}) was generated during this start, but this " +
                "database already contains data. A key created seconds ago cannot be the one that " +
                "encrypted it, so FullWorth is refusing to start rather than rendering every " +
                "encrypted column unreadable. Mount the fullworth-platform-secrets volume that holds " +
                "this installation's data_encryption_key.");

        db.Add(new InstallationEncryptionMarker
        {
            ScopeKey = InstallationEncryptionMarker.InstanceScopeKey,
            KeyFingerprint = fingerprint,
            FirstSeenAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Whether this installation has been used. Users are the right question: the seeder creates
    /// reference rows on an empty database, so counting "any table is non-empty" would call a fresh
    /// installation used and refuse its very first start.
    /// </summary>
    private static Task<bool> HasDataAsync(FullWorthDbContext db, CancellationToken ct) =>
        db.Users.AnyAsync(ct);
}
