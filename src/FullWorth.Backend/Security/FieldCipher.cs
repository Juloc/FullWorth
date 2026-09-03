using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Hosting;

namespace FullWorth.Backend.Security;

/// <summary>
/// At-rest encryption for individual sensitive DB fields (P0.4). Uses AES-256-GCM with a random nonce
/// per value (so ciphertext is non-deterministic) and a keyed HMAC "blind index" for the few fields
/// that must still be looked up / uniquely constrained by value. The key comes from
/// <c>Security:DataEncryptionKey</c> (base64, 32 bytes); in Production it is mandatory, and outside
/// Production a missing key yields an identity cipher so dev/test run without configuring a key.
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

    public static FieldCipher FromConfiguration(IConfiguration configuration, IHostEnvironment environment)
    {
        var configured = configuration["Security:DataEncryptionKey"];
        if (string.IsNullOrWhiteSpace(configured))
        {
            if (environment.IsProduction())
                throw new InvalidOperationException("Security:DataEncryptionKey must be configured (32 bytes, base64) before exposing the service.");
            return Null;
        }

        byte[] key;
        try { key = Convert.FromBase64String(configured.Trim()); }
        catch (FormatException) { throw new InvalidOperationException("Security:DataEncryptionKey must be valid base64."); }
        if (key.Length != 32)
            throw new InvalidOperationException("Security:DataEncryptionKey must decode to exactly 32 bytes (AES-256).");
        return new FieldCipher(key);
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
