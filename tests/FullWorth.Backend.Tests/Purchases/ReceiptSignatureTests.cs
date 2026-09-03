using FullWorth.Backend.Modules.Purchases;

namespace FullWorth.Backend.Tests.Purchases;

// P1.3: uploaded receipt bytes must match the claimed extension.
public sealed class ReceiptSignatureTests
{
    private static byte[] Jpeg => new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0, 0, 0 };
    private static byte[] Png => new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
    private static byte[] Pdf => "%PDF-1.7"u8.ToArray();
    private static byte[] Html => "<html><script>"u8.ToArray();
    private static byte[] Webp => "RIFF\0\0\0\0WEBPVP8 "u8.ToArray();

    [Fact]
    public void AcceptsMatchingSignatures()
    {
        Assert.True(ReceiptSignature.Matches(Jpeg, ".jpg"));
        Assert.True(ReceiptSignature.Matches(Jpeg, ".jpeg"));
        Assert.True(ReceiptSignature.Matches(Png, ".png"));
        Assert.True(ReceiptSignature.Matches(Pdf, ".pdf"));
        Assert.True(ReceiptSignature.Matches(Webp, ".webp"));
    }

    [Fact]
    public void RejectsMismatchedOrHostileContent()
    {
        Assert.False(ReceiptSignature.Matches(Html, ".jpg"));   // script renamed to .jpg
        Assert.False(ReceiptSignature.Matches(Html, ".pdf"));
        Assert.False(ReceiptSignature.Matches(Png, ".jpg"));    // real png claiming jpg
        Assert.False(ReceiptSignature.Matches(Jpeg, ".png"));
        Assert.False(ReceiptSignature.Matches(ReadOnlySpan<byte>.Empty, ".jpg"));
        Assert.False(ReceiptSignature.Matches(Jpeg, ".exe"));   // unsupported ext
    }
}
