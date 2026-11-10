using FullWorth.Backend.Documents;
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
/// Where an uploaded pension document lives. A pension document is a contract document and has to be
/// kept, so it is kept encrypted under <see cref="PensionStorageOptions.RootPath"/> and never leaves
/// the installation - by the shared <see cref="EncryptedBlobStore"/>, which also explains the format.
/// This class only fixes what is pension's: the root, the size ceiling and the HKDF label
/// (<see cref="BlobKeyInfo"/>), so a leaked pension blob key opens no other kind of document.
/// </summary>
public sealed class PensionDocumentBlobStore : IBavDocumentBlobStore
{
    private const string BlobKeyInfo = "fullworth-pension-blob";

    private readonly EncryptedBlobStore blobs;

    public PensionDocumentBlobStore(IOptions<PensionStorageOptions> storageOptions, FieldCipher cipher) =>
        blobs = new(storageOptions.Value.RootPath, storageOptions.Value.MaxDocumentBytes, cipher.DeriveSubKey(BlobKeyInfo));

    /// <summary>
    /// Builds a store the way the host does, so "Production without a key fails closed" is
    /// <see cref="FieldCipher.FromConfiguration"/>'s single rule here too rather than a second copy of it.
    /// </summary>
    public static PensionDocumentBlobStore FromConfiguration(
        IConfiguration configuration, IHostEnvironment environment, PensionStorageOptions options)
        => new(Options.Create(options), FieldCipher.FromConfiguration(configuration, environment));

    /// <summary>The scheme a blob written now would carry. <c>none</c> only on a keyless dev/test host.</summary>
    public string Scheme => blobs.Scheme;

    // fullWorthSpaceId and extension stay out of the path on purpose: see EncryptedBlobStore.WriteAsync.
    public async Task<BavStoredBlob> WriteAsync(
        Guid fullWorthSpaceId, Guid documentId, string extension, byte[] content, CancellationToken ct)
    {
        var blob = await blobs.WriteAsync(documentId, content, ct);
        return new BavStoredBlob(blob.StoragePath, blob.EncryptionScheme);
    }

    public Task<byte[]?> ReadAsync(string storagePath, string? encryptionScheme, CancellationToken ct) =>
        blobs.ReadAsync(storagePath, encryptionScheme, ct);

    public Task DeleteAsync(string storagePath, CancellationToken ct) => blobs.DeleteAsync(storagePath, ct);
}
