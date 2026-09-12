using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Hosting;

namespace FullWorth.Backend.Security;

/// <summary>
/// At-rest encryption for individual sensitive DB fields (P0.4). Uses AES-256-GCM with a random nonce
/// per value (so ciphertext is non-deterministic) and a keyed HMAC "blind index" for the few fields
/// that must still be looked up / uniquely constrained by value. The key comes from
/// <c>Security:DataEncryptionKey</c> (base64, 32 bytes) or is derived from
/// <c>Security:MasterKey</c>. In Production one of them is mandatory; outside Production a missing
/// key yields an identity cipher so dev/test run without configuring a key.
/// </summary>
public sealed class FieldCipher
{
    private const string Version = "v1:";
    private readonly byte[]? _key;
    private readonly byte[]? _macKey;

    /// <summary>Identity cipher (no encryption) for dev/test when no key is configured.</summary>
    public static readonly FieldCipher Null = new(null);

    private FieldCipher(byte[]? key)
    {
        _key = key;
        _macKey = key is null ? null : HKDF.DeriveKey(HashAlgorithmName.SHA256, key, 32, info: "fullworth-blind-index"u8.ToArray());
    }

    public bool Enabled => _key is not null;

    /// <summary>
    /// A short, stable name for the key in use — safe to store next to the data it encrypts and safe
    /// to print in a startup error, which is the whole point: an installation can recognise whether
    /// the key it was handed is the one its rows were written with.
    ///
    /// Derived through HKDF with its own info label rather than hashing the key directly, so this
    /// value is domain-separated from both the key and the blind-index MAC key above. Nothing about
    /// the key can be recovered from it.
    /// </summary>
    public string? Fingerprint => _key is null
        ? null
        : "sha256:" + Convert.ToHexString(HKDF.DeriveKey(
            HashAlgorithmName.SHA256, _key, 8, info: "fullworth-data-key-fingerprint"u8.ToArray()))
            .ToLowerInvariant();

    public static FieldCipher FromConfiguration(IConfiguration configuration, IHostEnvironment environment)
    {
        var configured = configuration["Security:DataEncryptionKey"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            byte[] key;
            try { key = Convert.FromBase64String(configured.Trim()); }
            catch (FormatException) { throw new InvalidOperationException("Security:DataEncryptionKey must be valid base64."); }
            if (key.Length != 32)
                throw new InvalidOperationException("Security:DataEncryptionKey must decode to exactly 32 bytes (AES-256).");
            return new FieldCipher(key);
        }

        var masterKey = configuration["Security:MasterKey"]?.Trim();
        if (!string.IsNullOrWhiteSpace(masterKey))
        {
            if (masterKey.Length < 32
                || masterKey.StartsWith("replace-", StringComparison.OrdinalIgnoreCase)
                || masterKey.Contains("change-me", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Security:MasterKey must be a stable random secret of at least 32 characters.");

            var key = SHA256.HashData(Encoding.UTF8.GetBytes("fullworth:data-encryption:v1:" + masterKey));
            return new FieldCipher(key);
        }

        if (environment.IsProduction())
            throw new InvalidOperationException("Security:DataEncryptionKey or Security:MasterKey must be configured before exposing the service.");
        return Null;
    }

    /// <summary>Encrypt a value for storage. Null passes through; identity cipher returns the input.</summary>
    public string? Protect(string? plaintext)
    {
        if (plaintext is null || _key is null) return plaintext;
        var nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var cipherBytes = new byte[plainBytes.Length];
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];
        using (var aes = new AesGcm(_key, tag.Length))
            aes.Encrypt(nonce, plainBytes, cipherBytes, tag);
        var combined = new byte[nonce.Length + tag.Length + cipherBytes.Length];
        Buffer.BlockCopy(nonce, 0, combined, 0, nonce.Length);
        Buffer.BlockCopy(tag, 0, combined, nonce.Length, tag.Length);
        Buffer.BlockCopy(cipherBytes, 0, combined, nonce.Length + tag.Length, cipherBytes.Length);
        return Version + Convert.ToBase64String(combined);
    }

    /// <summary>Decrypt a stored value. Null and legacy/plaintext (no version prefix) pass through.</summary>
    public string? Unprotect(string? stored)
    {
        if (stored is null || _key is null || !stored.StartsWith(Version, StringComparison.Ordinal)) return stored;
        var combined = Convert.FromBase64String(stored[Version.Length..]);
        var nonceLength = AesGcm.NonceByteSizes.MaxSize;
        var tagLength = AesGcm.TagByteSizes.MaxSize;
        if (combined.Length < nonceLength + tagLength) throw new CryptographicException("Encrypted value is malformed.");
        var nonce = combined.AsSpan(0, nonceLength);
        var tag = combined.AsSpan(nonceLength, tagLength);
        var cipherBytes = combined.AsSpan(nonceLength + tagLength);
        var plainBytes = new byte[cipherBytes.Length];
        using (var aes = new AesGcm(_key, tagLength))
            aes.Decrypt(nonce, cipherBytes, tag, plainBytes);
        return Encoding.UTF8.GetString(plainBytes);
    }

    /// <summary>
    /// A subkey of the data key for a second at-rest consumer, derived with HKDF under its own
    /// <paramref name="info"/> label — the same construction the blind-index key already uses. It exists
    /// so the pension document blob store can encrypt files with a key of its own instead of the data
    /// key: the blobs live in a bind-mounted directory a backup job can copy wholesale, and HKDF being
    /// one-way means a leaked blob key yields neither the data key nor an encrypted field. Null when no
    /// key is configured, mirroring <see cref="Null"/>, so a keyless dev/test host keeps working.
    /// </summary>
    public byte[]? DeriveSubKey(string info, int length = 32)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(info);
        return _key is null ? null : HKDF.DeriveKey(HashAlgorithmName.SHA256, _key, length, info: Encoding.UTF8.GetBytes(info));
    }

    /// <summary>
    /// Deterministic keyed hash for values that must stay uniquely constrained / looked up by value
    /// after the value itself is encrypted. Identity cipher returns the input so dev/test keep working.
    /// </summary>
    public string? BlindIndex(string? value)
    {
        if (value is null || _macKey is null) return value;
        using var hmac = new HMACSHA256(_macKey);
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(value.Trim())));
    }
}
