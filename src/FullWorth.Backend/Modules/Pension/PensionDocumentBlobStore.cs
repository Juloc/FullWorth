using System.Security.Cryptography;
using FullWorth.Backend.Security;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace FullWorth.Backend.Modules.Pension;

public sealed class PensionStorageOptions
{
    public const string SectionName = "PensionStorage";

    public string RootPath { get; set; } = "/data/pension";

    // Same ceiling the payslip extractor uses: a Standmitteilung is a few hundred KB of PDF and a
    // scanned 30-page one still fits, while a mis-uploaded archive does not.
    public long MaxDocumentBytes { get; set; } = 12 * 1024 * 1024;
}

/// <summary>
/// Where an uploaded pension document lives. A payslip is never persisted; a pension document is a
/// contract document and has to be kept — so it is kept encrypted, under
/// <see cref="PensionStorageOptions.RootPath"/>, and it never leaves the installation.
///
/// AES-256-GCM, a fresh random nonce per blob, layout <c>nonce | tag | ciphertext</c> — deliberately the
/// same layout <see cref="FieldCipher"/> writes for a field, so one re-key job can eventually re-wrap
/// both. The stored <see cref="BavStoredBlob.EncryptionScheme"/> is what tells that job what it is
/// looking at, and it is what <see cref="ReadAsync"/> obeys: a blob written on a dev box before a key
/// existed stays readable after one is configured, because the scheme is read, never guessed.
///
/// The blob key is <b>not</b> the data key. It is HKDF-derived from it under its own info label
/// (<see cref="BlobKeyInfo"/>), because these two secrets live behind different walls: the blobs sit in a
/// bind-mounted directory an operator or a backup job can copy wholesale, while the field key protects
/// database columns. HKDF is one-way, so a leaked blob key reveals neither the data key nor an encrypted
/// policy number.
///
/// No file name, no path and no byte of content reaches a log line from here — nothing in this class
/// logs at all, and the exceptions it throws name only the scheme, never the document.
/// </summary>
public sealed class PensionDocumentBlobStore : IBavDocumentBlobStore
{
    private const string BlobKeyInfo = "fullworth-pension-blob";

    private readonly PensionStorageOptions storage;
    private readonly byte[]? key;

    public PensionDocumentBlobStore(IOptions<PensionStorageOptions> storageOptions, FieldCipher cipher)
    {
        storage = storageOptions.Value;
        key = cipher.DeriveSubKey(BlobKeyInfo);
    }

    /// <summary>
    /// Builds a store the way the host does, so "Production without a key fails closed" is
    /// <see cref="FieldCipher.FromConfiguration"/>'s single rule here too rather than a second copy of it.
    /// </summary>
    public static PensionDocumentBlobStore FromConfiguration(
        IConfiguration configuration, IHostEnvironment environment, PensionStorageOptions options)
        => new(Options.Create(options), FieldCipher.FromConfiguration(configuration, environment));

    /// <summary>The scheme a blob written now would carry. <c>none</c> only on a keyless dev/test host.</summary>
    public string Scheme => key is null ? BavDocumentEncryptionSchemes.None : BavDocumentEncryptionSchemes.AesGcmV1;

    /// <summary>
    /// Writes the blob and returns its relative path plus the scheme that protects it. Both
    /// <paramref name="fullWorthSpaceId"/> and <paramref name="extension"/> stay out of the path on
    /// purpose: the layout is <c>yyyy/MM/{documentId:N}.bin</c>, so a directory listing alone reveals
    /// neither which space a document belongs to nor what kind of file it was. The original extension is
    /// the DB column's job, and an encrypted blob has no meaningful extension anyway.
    /// </summary>
    public async Task<BavStoredBlob> WriteAsync(
        Guid fullWorthSpaceId, Guid documentId, string extension, byte[] content, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.Length == 0) throw new ArgumentException("A pension document must not be empty.", nameof(content));
        if (content.Length > storage.MaxDocumentBytes)
            throw new ArgumentException("A pension document exceeds the configured size limit.", nameof(content));

        var now = DateTime.UtcNow;
        var relative = $"{now:yyyy}/{now:MM}/{documentId:N}.bin";
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
        return new BavStoredBlob(relative, scheme);
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
            ? BavDocumentEncryptionSchemes.None
            : encryptionScheme.Trim();
        return scheme switch
        {
            BavDocumentEncryptionSchemes.None => stored,
            BavDocumentEncryptionSchemes.AesGcmV1 => Decrypt(stored),
            _ => throw new InvalidOperationException($"Unknown pension document encryption scheme '{scheme}'.")
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
            throw new InvalidOperationException("The stored pension document is encrypted but no encryption key is configured.");

        var nonceLength = AesGcm.NonceByteSizes.MaxSize;
        var tagLength = AesGcm.TagByteSizes.MaxSize;
        if (stored.Length < nonceLength + tagLength) throw new CryptographicException("The stored pension document is malformed.");

        var nonce = stored.AsSpan(0, nonceLength);
        var tag = stored.AsSpan(nonceLength, tagLength);
        var cipherBytes = stored.AsSpan(nonceLength + tagLength);
        var plaintext = new byte[cipherBytes.Length];
        using (var aes = new AesGcm(key, tagLength))
            aes.Decrypt(nonce, cipherBytes, tag, plaintext);
        return plaintext;
    }

    // Identical to PurchaseDocumentService.SafeAbsolute: a stored path is data, and data that reaches
    // Path.Combine decides which file the process opens. An absolute path or one carrying a ".." segment
    // leaves the root and is refused, never normalised into something "close enough".
    private string SafeAbsolute(string relative)
    {
        var root = Path.GetFullPath(storage.RootPath);
        var candidate = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.Ordinal)) throw new InvalidOperationException("Invalid document storage path.");
        return candidate;
    }
}
