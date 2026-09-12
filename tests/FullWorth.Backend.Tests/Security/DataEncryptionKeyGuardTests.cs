using FullWorth.Backend.Data;
using FullWorth.Backend.Security;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace FullWorth.Backend.Tests.Security;

/// <summary>
/// The one failure that a backup cannot repair.
///
/// <c>data_encryption_key</c> lives in a Docker volume and the app creates it when it is missing. So a
/// container started against an existing database with an empty or wrong secrets volume generated a
/// brand-new key, started perfectly, reported itself healthy — and every encrypted column was
/// unreadable from that moment on. Nothing complained, because nothing was looking.
///
/// Making the volume external only stopped <c>docker compose down -v</c>, and only in one folder. It
/// did nothing about a wrong mount, a volume restored from the wrong backup, or a host migration that
/// forgot it — and it cost a manual <c>docker volume create</c> before every new host's first start.
/// </summary>
public sealed class DataEncryptionKeyGuardTests
{
    private static readonly string KeyA = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());
    private static readonly string KeyB = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)(i + 99)).ToArray());

    /// <summary>
    /// The proof case, and the reason it does not need the marker: a key that did not exist a second
    /// ago cannot have encrypted rows written last week. This is what protects the very first start
    /// after the guard ships, when no marker has been written yet.
    /// </summary>
    [Fact]
    public async Task A_key_generated_during_this_start_is_refused_on_a_database_that_has_users()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        await using var db = database.CreateContext();
        await GiveTheInstallationAUserAsync(db);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DataEncryptionKeyGuard.EnsureKeyBelongsToInstallationAsync(
                db, Cipher(KeyA), keyCreatedNow: true, CancellationToken.None));

        Assert.Contains("already contains data", failure.Message, StringComparison.Ordinal);
        Assert.Contains("fullworth-platform-secrets", failure.Message, StringComparison.Ordinal);
        // Nothing was recorded: a refused start must not leave the wrong key looking legitimate.
        Assert.Empty(await db.Set<InstallationEncryptionMarker>().ToListAsync());
    }

    /// <summary>
    /// A brand-new host generates its key and has no data. That is the normal first start, and it
    /// must not be mistaken for the catastrophe — refusing it would make a fresh install impossible.
    /// </summary>
    [Fact]
    public async Task A_fresh_installation_generating_its_key_starts_and_records_it()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        await using var db = database.CreateContext();

        await DataEncryptionKeyGuard.EnsureKeyBelongsToInstallationAsync(
            db, Cipher(KeyA), keyCreatedNow: true, CancellationToken.None);

        var marker = await db.Set<InstallationEncryptionMarker>().SingleAsync();
        Assert.Equal(Cipher(KeyA).Fingerprint, marker.KeyFingerprint);
    }

    /// <summary>The durable half: a different key later on, created now or not, is refused.</summary>
    [Fact]
    public async Task A_different_key_than_the_recorded_one_is_refused()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        await using var db = database.CreateContext();
        await DataEncryptionKeyGuard.EnsureKeyBelongsToInstallationAsync(
            db, Cipher(KeyA), keyCreatedNow: true, CancellationToken.None);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DataEncryptionKeyGuard.EnsureKeyBelongsToInstallationAsync(
                db, Cipher(KeyB), keyCreatedNow: false, CancellationToken.None));

        // Both fingerprints, because "which one do I have to restore" is the only useful question here.
        Assert.Contains(Cipher(KeyA).Fingerprint!, failure.Message, StringComparison.Ordinal);
        Assert.Contains(Cipher(KeyB).Fingerprint!, failure.Message, StringComparison.Ordinal);
        Assert.Equal(
            Cipher(KeyA).Fingerprint,
            (await db.Set<InstallationEncryptionMarker>().SingleAsync()).KeyFingerprint);
    }

    [Fact]
    public async Task The_same_key_starts_every_time_and_records_nothing_new()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        await using var db = database.CreateContext();

        for (var start = 0; start < 3; start++)
            await DataEncryptionKeyGuard.EnsureKeyBelongsToInstallationAsync(
                db, Cipher(KeyA), keyCreatedNow: false, CancellationToken.None);

        Assert.Single(await db.Set<InstallationEncryptionMarker>().ToListAsync());
    }

    /// <summary>
    /// Development and Testing run the identity cipher, where nothing is encrypted and there is no key
    /// to belong to anything. The guard has to stay out of the way there rather than inventing a
    /// marker that would then be compared against on every later start.
    /// </summary>
    [Fact]
    public async Task Without_a_configured_key_nothing_is_checked_and_nothing_is_recorded()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        await using var db = database.CreateContext();
        await GiveTheInstallationAUserAsync(db);

        await DataEncryptionKeyGuard.EnsureKeyBelongsToInstallationAsync(
            db, FieldCipher.Null, keyCreatedNow: true, CancellationToken.None);

        Assert.Empty(await db.Set<InstallationEncryptionMarker>().ToListAsync());
    }

    /// <summary>
    /// The fingerprint is derived, not the key. It goes into the same database as the ciphertext and
    /// into startup logs an operator reads out loud, so it must carry nothing back to the key.
    /// </summary>
    [Fact]
    public void The_fingerprint_is_stable_distinct_and_reveals_no_key_material()
    {
        var a = Cipher(KeyA).Fingerprint!;

        Assert.Equal(a, Cipher(KeyA).Fingerprint);
        Assert.NotEqual(a, Cipher(KeyB).Fingerprint);
        Assert.StartsWith("sha256:", a, StringComparison.Ordinal);
        Assert.DoesNotContain(KeyA, a, StringComparison.Ordinal);
        Assert.DoesNotContain(
            Convert.ToHexString(Convert.FromBase64String(KeyA)).ToLowerInvariant(),
            a,
            StringComparison.Ordinal);
    }

    private static async Task GiveTheInstallationAUserAsync(FullWorthDbContext db)
    {
        db.Users.Add(new FullWorth.Backend.Modules.Users.FullWorthUser
        {
            EmailNormalized = "someone@example.com",
            DisplayName = "Someone"
        });
        await db.SaveChangesAsync();
    }

    private static FieldCipher Cipher(string base64Key) =>
        FieldCipher.FromConfiguration(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Security:DataEncryptionKey"] = base64Key
                })
                .Build(),
            new FakeEnvironment());

    private sealed class FakeEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = ".";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
