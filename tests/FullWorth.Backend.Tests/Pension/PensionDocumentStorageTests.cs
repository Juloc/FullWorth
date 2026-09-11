using System.Security.Cryptography;
using System.Text;
using FullWorth.Backend.Modules.Pension;
using FullWorth.Backend.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace FullWorth.Backend.Tests.Pension;

// The two infrastructure seams of the bAV document pipeline, tested without Postgres and without
// poppler/tesseract: the blob store must keep an uploaded statement unreadable on disk, must obey the
// scheme a blob was written with instead of guessing it, must refuse a crafted storage path, and must
// fail closed in Production; the text source must refuse what it cannot handle before starting a
// process, and must split pdftotext output where pdftotext actually separates pages.
public sealed class PensionDocumentStorageTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"fullworth-pension-tests-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { /* test cleanup */ }
    }

    private sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "test";
        public string ContentRootPath { get; set; } = ".";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private static FieldCipher Cipher(byte[]? key)
    {
        var values = new Dictionary<string, string?>();
        if (key is not null) values["Security:DataEncryptionKey"] = Convert.ToBase64String(key);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        return FieldCipher.FromConfiguration(configuration, new Env("Development"));
    }

    private PensionDocumentBlobStore Store(byte[]? key) => new(
        Options.Create(new PensionStorageOptions { RootPath = root }), Cipher(key));

    private static byte[] Key(byte value) => Enumerable.Repeat(value, 32).ToArray();

    private static byte[] Document() => Encoding.UTF8.GetBytes(
        "Standmitteilung 2026\nVertragsguthaben 12.345,67 EUR\nGarantiekapital 10.000,00 EUR");

    private string Absolute(string relative) => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

    [Fact]
    public async Task EncryptedBlobIsUnreadableOnDiskAndRoundTrips()
    {
        var store = Store(Key(7));
        var plaintext = Document();

        var blob = await store.WriteAsync(Guid.NewGuid(), Guid.NewGuid(), ".pdf", plaintext, default);

        Assert.Equal(BavDocumentEncryptionSchemes.AesGcmV1, blob.EncryptionScheme);
        Assert.EndsWith(".bin", blob.StoragePath);                      // an encrypted blob has no meaningful extension
        var onDisk = await File.ReadAllBytesAsync(Absolute(blob.StoragePath));
        Assert.NotEqual(plaintext, onDisk);
        Assert.True(onDisk.Length > plaintext.Length);                  // nonce + tag ride along
        Assert.DoesNotContain("Vertragsguthaben", Encoding.UTF8.GetString(onDisk));

        Assert.Equal(plaintext, await store.ReadAsync(blob.StoragePath, blob.EncryptionScheme, default));
    }

    [Fact]
    public async Task SameDocumentTwiceProducesDifferentCiphertext()
    {
        var store = Store(Key(9));
        var plaintext = Document();

        var first = await store.WriteAsync(Guid.NewGuid(), Guid.NewGuid(), ".pdf", plaintext, default);
        var second = await store.WriteAsync(Guid.NewGuid(), Guid.NewGuid(), ".pdf", plaintext, default);

        Assert.NotEqual(
            await File.ReadAllBytesAsync(Absolute(first.StoragePath)),
            await File.ReadAllBytesAsync(Absolute(second.StoragePath)));  // random nonce per blob
    }

    [Fact]
    public async Task KeylessHostStoresPlainAndSaysSo()
    {
        var store = Store(null);
        var plaintext = Document();

        var blob = await store.WriteAsync(Guid.NewGuid(), Guid.NewGuid(), ".pdf", plaintext, default);

        Assert.Equal(BavDocumentEncryptionSchemes.None, blob.EncryptionScheme);
        Assert.Equal(plaintext, await File.ReadAllBytesAsync(Absolute(blob.StoragePath)));
        Assert.Equal(plaintext, await store.ReadAsync(blob.StoragePath, blob.EncryptionScheme, default));
    }

    [Fact]
    public async Task BlobWrittenWithoutAKeyStaysReadableAfterOneIsConfigured()
    {
        var plaintext = Document();
        var blob = await Store(null).WriteAsync(Guid.NewGuid(), Guid.NewGuid(), ".pdf", plaintext, default);

        var keyed = Store(Key(3));

        // The stored scheme is obeyed, not guessed: enabling encryption must not orphan what came before.
        Assert.Equal(plaintext, await keyed.ReadAsync(blob.StoragePath, blob.EncryptionScheme, default));
        Assert.Equal(plaintext, await keyed.ReadAsync(blob.StoragePath, null, default));
        Assert.Equal(BavDocumentEncryptionSchemes.AesGcmV1, keyed.Scheme);
    }

    [Fact]
    public async Task TamperedBlobFailsInsteadOfReturningWrongBytes()
    {
        var store = Store(Key(11));
        var blob = await store.WriteAsync(Guid.NewGuid(), Guid.NewGuid(), ".pdf", Document(), default);
        var absolute = Absolute(blob.StoragePath);

        var stored = await File.ReadAllBytesAsync(absolute);
        stored[^1] ^= 0x01;
        await File.WriteAllBytesAsync(absolute, stored);

        await Assert.ThrowsAnyAsync<CryptographicException>(
            () => store.ReadAsync(blob.StoragePath, blob.EncryptionScheme, default));
    }

    [Fact]
    public async Task WrongKeyCannotRead()
    {
        var blob = await Store(Key(1)).WriteAsync(Guid.NewGuid(), Guid.NewGuid(), ".pdf", Document(), default);

        await Assert.ThrowsAnyAsync<CryptographicException>(
            () => Store(Key(2)).ReadAsync(blob.StoragePath, blob.EncryptionScheme, default));
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("2026/../../etc/passwd")]
    [InlineData("2026/09/../../../outside.bin")]
    [InlineData("/etc/passwd")]
    [InlineData("")]
    public async Task CraftedStoragePathIsRefused(string path)
    {
        var store = Store(Key(5));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.ReadAsync(path, BavDocumentEncryptionSchemes.AesGcmV1, default));
    }

    [Fact]
    public async Task MissingBlobReadsAsNullAndDeleteIsBestEffort()
    {
        var store = Store(Key(5));

        Assert.Null(await store.ReadAsync($"2026/09/{Guid.NewGuid():N}.bin", BavDocumentEncryptionSchemes.AesGcmV1, default));
        await store.DeleteAsync($"2026/09/{Guid.NewGuid():N}.bin", default);        // must not throw
        await store.DeleteAsync("../../etc/passwd", default);                        // nor for a refused path
    }

    [Fact]
    public async Task DeleteRemovesTheBlob()
    {
        var store = Store(Key(5));
        var blob = await store.WriteAsync(Guid.NewGuid(), Guid.NewGuid(), ".pdf", Document(), default);

        await store.DeleteAsync(blob.StoragePath, default);

        Assert.False(File.Exists(Absolute(blob.StoragePath)));
    }

    [Fact]
    public async Task UnknownSchemeIsRefusedRatherThanTreatedAsPlaintext()
    {
        var store = Store(Key(5));
        var blob = await store.WriteAsync(Guid.NewGuid(), Guid.NewGuid(), ".pdf", Document(), default);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReadAsync(blob.StoragePath, "aesgcm-v2", default));
    }

    [Fact]
    public async Task EmptyAndOversizeDocumentsAreRefused()
    {
        var store = Store(Key(5));
        var id = Guid.NewGuid();

        await Assert.ThrowsAsync<ArgumentException>(() => store.WriteAsync(Guid.NewGuid(), id, ".pdf", [], default));
        await Assert.ThrowsAsync<ArgumentException>(
            () => store.WriteAsync(Guid.NewGuid(), id, ".pdf", new byte[(12 * 1024 * 1024) + 1], default));
    }

    [Fact]
    public void ProductionWithoutAKeyFailsClosed()
    {
        var empty = new ConfigurationBuilder().Build();
        var options = new PensionStorageOptions { RootPath = root };

        Assert.Throws<InvalidOperationException>(
            () => PensionDocumentBlobStore.FromConfiguration(empty, new Env("Production"), options));

        // The same configuration outside Production is allowed to run without encryption.
        Assert.Equal(
            BavDocumentEncryptionSchemes.None,
            PensionDocumentBlobStore.FromConfiguration(empty, new Env("Development"), options).Scheme);
    }

    [Fact]
    public void BlobKeyIsNotTheFieldKey()
    {
        var cipher = Cipher(Key(4));

        var blobKey = cipher.DeriveSubKey("fullworth-pension-blob");
        var otherKey = cipher.DeriveSubKey("fullworth-something-else");

        Assert.NotNull(blobKey);
        Assert.Equal(32, blobKey!.Length);
        Assert.NotEqual(Key(4), blobKey);                        // not the data key itself
        Assert.NotEqual(blobKey, otherKey);                      // the info label separates the consumers
        Assert.Equal(blobKey, cipher.DeriveSubKey("fullworth-pension-blob"));   // and it is stable
        Assert.Null(Cipher(null).DeriveSubKey("fullworth-pension-blob"));
    }

    [Fact]
    public async Task TextSourceRefusesAnUnsupportedExtensionBeforeTouchingAnyTool()
    {
        var source = new PensionDocumentTextSource();

        var error = await Assert.ThrowsAsync<BavDocumentTextException>(
            () => source.ReadAsync(Document(), ".docx", default));

        Assert.Equal(BavDocumentTextException.Unsupported, error.Category);
        Assert.True(error.Category.Length <= 64);
    }

    [Fact]
    public async Task TextSourceRefusesAnOversizeInput()
    {
        var source = new PensionDocumentTextSource();

        var error = await Assert.ThrowsAsync<BavDocumentTextException>(
            () => source.ReadAsync(new byte[PensionDocumentTextSource.MaxBytes + 1], ".pdf", default));

        Assert.Equal(BavDocumentTextException.TooLarge, error.Category);
    }

    [Fact]
    public async Task TextSourceRefusesAnEmptyInput()
    {
        var error = await Assert.ThrowsAsync<BavDocumentTextException>(
            () => new PensionDocumentTextSource().ReadAsync([], ".pdf", default));

        Assert.Equal(BavDocumentTextException.NoText, error.Category);
    }

    [Fact]
    public void PdftotextOutputSplitsOnTheFormFeed()
    {
        // pdftotext writes a form feed after every page, including the last one.
        var pages = PensionDocumentTextSource.SplitPages("Seite eins\n\fSeite zwei\n\f");

        Assert.Equal(2, pages.Count);
        Assert.Equal("Seite eins", pages[0]);
        Assert.Equal("Seite zwei", pages[1]);
        Assert.Empty(PensionDocumentTextSource.SplitPages(""));
    }

    [Fact]
    public void PageSplitTruncatesAtTheCapInsteadOfFailing()
    {
        var output = string.Concat(Enumerable.Range(1, 40).Select(page => $"Seite {page}\n\f"));

        var pages = PensionDocumentTextSource.SplitPages(output);

        Assert.Equal(30, pages.Count);
        Assert.Equal("Seite 1", pages[0]);
        Assert.Equal("Seite 30", pages[^1]);
    }

    [Fact]
    public void ANearEmptyTextLayerMeansTheDocumentIsAScan()
    {
        // Page furniture only: OCR is the right source for this one.
        Assert.False(PensionDocumentTextSource.HasUsableTextLayer(["Seite 1", "Seite 2"]));
        Assert.False(PensionDocumentTextSource.HasUsableTextLayer([]));
        Assert.True(PensionDocumentTextSource.HasUsableTextLayer([new string('x', 40), new string('y', 40)]));
    }
}
