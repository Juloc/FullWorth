using FullWorth.Web.Modules.Purchases;
using Microsoft.AspNetCore.Http;

namespace FullWorth.Web.Tests;

public sealed class ShareReceiptInboxTests
{
    [Fact]
    public async Task ValidReceiptSetIsBoundToTheAuthenticatedUserAndCanBeDeleted()
    {
        var owner = Guid.NewGuid();
        var stranger = Guid.NewGuid();
        var files = new IFormFile[]
        {
            File("top.png", Png(1), "image/png"),
            File("bottom.png", Png(2), "image/png")
        };

        var stored = await SharedReceiptInbox.StoreAsync(owner, files, CancellationToken.None);
        Assert.Null(stored.Error);
        Assert.False(string.IsNullOrWhiteSpace(stored.Token));

        var entry = await SharedReceiptInbox.ReadAsync(stored.Token!, owner, CancellationToken.None);
        Assert.NotNull(entry);
        Assert.Equal(2, entry!.Files.Count);
        Assert.Equal("top.png", entry.Files[0].OriginalFileName);
        Assert.Equal("bottom.png", entry.Files[1].OriginalFileName);
        Assert.All(entry.Files, file => Assert.True(System.IO.File.Exists(file.AbsolutePath)));

        Assert.Null(await SharedReceiptInbox.ReadAsync(stored.Token!, stranger, CancellationToken.None));

        await SharedReceiptInbox.DeleteAsync(entry, CancellationToken.None);
        Assert.Null(await SharedReceiptInbox.ReadAsync(stored.Token!, owner, CancellationToken.None));
    }

    [Fact]
    public async Task SpoofedFileExtensionIsRejectedBeforeItEntersTheInbox()
    {
        var owner = Guid.NewGuid();
        var file = File("receipt.png", "%PDF-not-a-png"u8.ToArray(), "image/png");

        var stored = await SharedReceiptInbox.StoreAsync(owner, new[] { file }, CancellationToken.None);

        Assert.Null(stored.Token);
        Assert.Contains("does not match", stored.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PathLikeUploadNameIsReducedToSafeFileName()
    {
        var owner = Guid.NewGuid();
        var stored = await SharedReceiptInbox.StoreAsync(owner, new[] { File("../../private/receipt.png", Png(3), "image/png") }, CancellationToken.None);
        Assert.Null(stored.Error);

        var entry = await SharedReceiptInbox.ReadAsync(stored.Token!, owner, CancellationToken.None);
        Assert.NotNull(entry);
        Assert.Equal("receipt.png", entry!.Files[0].OriginalFileName);
        Assert.DoesNotContain("..", entry.Files[0].AbsolutePath, StringComparison.Ordinal);
        await SharedReceiptInbox.DeleteAsync(entry, CancellationToken.None);
    }

    private static FormFile File(string fileName, byte[] bytes, string contentType)
    {
        var stream = new MemoryStream(bytes, writable: false);
        return new FormFile(stream, 0, bytes.Length, "receipt", fileName)
        {
            Headers = new HeaderDictionary(),
            ContentType = contentType
        };
    }

    private static byte[] Png(byte marker) =>
    [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, marker, 0x00, 0x01, 0x02];
}