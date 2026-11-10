using System.Security.Cryptography;

namespace FullWorth.Backend.Documents;

/// <summary>Where a blob went and what protects it. The scheme is stored so a future re-key knows what it has to re-wrap.</summary>
public sealed record EncryptedBlob(string StoragePath, string EncryptionScheme);

public static class BlobEncryptionSchemes
{
    public const string AesGcmV1 = "aesgcm-v1";

    /// <summary>Dev/test without a configured key, mirroring <c>FieldCipher.Null</c>. Never in Production.</summary>
    public const string None = "none";
}

/// <summary>
/// Where an uploaded personal document lives on disk: encrypted, under one root, and never leaving the
/// installation. The one implementation behind pension documents and payslips - a document kind brings
/// its own root and its own key label, nothing else.
///
/// AES-256-GCM, a fresh random nonce per blob, layout <c>nonce | tag | ciphertext</c> — deliberately the
/// same layout <c>FieldCipher</c> writes for a field, so one re-key job can eventually re-wrap
/// both. The stored scheme is what tells that job what it is looking at, and it is what
/// <see cref="ReadAsync"/> obeys: a blob written on a dev box before a key existed stays readable after
/// one is configured, because the scheme is read, never guessed.
///
/// The key is <b>not</b> the data key: the caller derives it from that under its own HKDF label,
/// because these two secrets live behind different walls - the blobs sit in a bind-mounted directory
/// an operator or a backup job can copy wholesale, while the field key protects database columns.
/// HKDF is one-way, so a leaked blob key reveals neither the data key nor an encrypted field.
///
/// No file name, no path and no byte of content reaches a log line from here — nothing in this class
/// logs at all, and the exceptions it throws name only the scheme, never the document.
/// </summary>
public sealed class EncryptedBlobStore(string rootPath, long maxBytes, byte[]? key)
{
    /// <summary>The scheme a blob written now would carry. <c>none</c> only on a keyless dev/test host.</summary>
    public string Scheme => key is null ? BlobEncryptionSchemes.None : BlobEncryptionSchemes.AesGcmV1;

    /// <summary>
    /// Writes the blob and returns its relative path plus the scheme that protects it. The layout is
    /// <c>yyyy/MM/{id:N}.bin</c>, so a directory listing alone reveals neither which space a document
    /// belongs to nor what kind of file it was; the original extension is the DB column's job, and an
    /// encrypted blob has no meaningful extension anyway.
    /// </summary>
    public async Task<EncryptedBlob> WriteAsync(Guid id, byte[] content, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.Length == 0) throw new ArgumentException("A document must not be empty.", nameof(content));
        if (content.Length > maxBytes) throw new ArgumentException("A document exceeds the configured size limit.", nameof(content));

        var now = DateTime.UtcNow;
        var relative = $"{now:yyyy}/{now:MM}/{id:N}.bin";
        var absolute = SafeAbsolute(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);

        var scheme = Scheme;
        try
        {
            await File.WriteAllBytesAsync(absolute, key is null ? content : Encrypt(content, key), ct);
        }
        catch
        {
            // A half-written blob is worse than none: the row that would point at it does not exist yet.
            try { if (File.Exists(absolute)) File.Delete(absolute); } catch { /* best effort */ }
            throw;
        }
        return new EncryptedBlob(relative, scheme);
    }

    /// <summary>
    /// Reads a blob back, honouring the scheme it was written with. Null when the file is gone (a
    /// restored database whose blob directory was not restored with it is an operator problem, not an
    /// exception). A tampered or truncated blob throws out of AES-GCM rather than returning bytes that
    /// are not the ones that were stored — that is the entire reason for the authenticated mode.
    /// </summary>
    public async Task<byte[]?> ReadAsync(string storagePath, string? encryptionScheme, CancellationToken ct)
    {
        var absolute = SafeAbsolute(storagePath);
        if (!File.Exists(absolute)) return null;
        var stored = await File.ReadAllBytesAsync(absolute, ct);

        // A null scheme is a row written before the column was filled; those blobs are plaintext.
        var scheme = string.IsNullOrWhiteSpace(encryptionScheme)
            ? BlobEncryptionSchemes.None
            : encryptionScheme.Trim();
        return scheme switch
        {
            BlobEncryptionSchemes.None => stored,
            BlobEncryptionSchemes.AesGcmV1 => Decrypt(stored),
            _ => throw new InvalidOperationException($"Unknown document encryption scheme '{scheme}'.")
        };
    }

    /// <summary>Best effort: a blob that is already gone is the desired outcome, not a failure.</summary>
    public Task DeleteAsync(string storagePath, CancellationToken ct)
    {
        try
        {
            var absolute = SafeAbsolute(storagePath);
            if (File.Exists(absolute)) File.Delete(absolute);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Deleting a document must not fail because its blob is locked, missing or points nowhere
            // sane. The row is the record; an orphaned file is cleanable, an undeletable row is not.
        }
        return Task.CompletedTask;
    }

    private static byte[] Encrypt(byte[] plaintext, byte[] blobKey)
    {
        var nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];
        var cipherBytes = new byte[plaintext.Length];
        using (var aes = new AesGcm(blobKey, tag.Length))
            aes.Encrypt(nonce, plaintext, cipherBytes, tag);

        var combined = new byte[nonce.Length + tag.Length + cipherBytes.Length];
        Buffer.BlockCopy(nonce, 0, combined, 0, nonce.Length);
        Buffer.BlockCopy(tag, 0, combined, nonce.Length, tag.Length);
        Buffer.BlockCopy(cipherBytes, 0, combined, nonce.Length + tag.Length, cipherBytes.Length);
        return combined;
    }

    private byte[] Decrypt(byte[] stored)
    {
        if (key is null)
            throw new InvalidOperationException("The stored document is encrypted but no encryption key is configured.");

        var nonceLength = AesGcm.NonceByteSizes.MaxSize;
        var tagLength = AesGcm.TagByteSizes.MaxSize;
        if (stored.Length < nonceLength + tagLength) throw new CryptographicException("The stored document is malformed.");

        var nonce = stored.AsSpan(0, nonceLength);
        var tag = stored.AsSpan(nonceLength, tagLength);
        var cipherBytes = stored.AsSpan(nonceLength + tagLength);
        var plaintext = new byte[cipherBytes.Length];
        using (var aes = new AesGcm(key, tagLength))
            aes.Decrypt(nonce, cipherBytes, tag, plaintext);
        return plaintext;
    }

    // A stored path is data, and data that reaches Path.Combine decides which file the process opens.
    // An absolute path or one carrying a ".." segment leaves the root and is refused, never normalised
    // into something "close enough".
    private string SafeAbsolute(string relative)
    {
        var root = Path.GetFullPath(rootPath);
        var candidate = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.Ordinal)) throw new InvalidOperationException("Invalid document storage path.");
        return candidate;
    }
}
