using System.Text;
using FullWorth.Backend.Modules.Purchases.ReceiptImports;

namespace FullWorth.Backend.Tests.Purchases.ReceiptImports;

public sealed class ImagePdfTests
{
    [Fact]
    public void TwoPhotosBecomeTwoPagesSizedByTheirPixels()
    {
        var pdf = ImagePdf.Build([Jpeg(1500, 3000), Jpeg(1500, 1200)]);
        var text = Encoding.Latin1.GetString(pdf);

        Assert.StartsWith("%PDF-1.4", text);
        Assert.EndsWith("%%EOF\n", text);
        Assert.Contains("/Count 2", text);
        // 300 Punkte je Zoll: 1500 px sind 360 pt, 3000 px 720 pt.
        Assert.Contains("/MediaBox [0 0 360 720]", text);
        Assert.Contains("/MediaBox [0 0 360 288]", text);
        Assert.Equal(2, text.Split("/Filter /DCTDecode").Length - 1);
    }

    [Fact]
    public void EveryCrossReferenceEntryPointsAtItsObject()
    {
        var pdf = ImagePdf.Build([Jpeg(800, 1600), Jpeg(800, 1600), Jpeg(800, 400)]);
        var text = Encoding.Latin1.GetString(pdf);

        // "\nxref": das Wort steckt auch in "startxref", das aber nicht die Tabelle ist.
        var xref = text.LastIndexOf("\nxref\n", StringComparison.Ordinal) + 1;
        Assert.Equal(xref, long.Parse(text[(text.LastIndexOf("startxref\n", StringComparison.Ordinal) + "startxref\n".Length)..].Split('\n')[0]));
        var entries = text[xref..].Split('\n').Skip(3).TakeWhile(line => line.EndsWith(" n ", StringComparison.Ordinal)).ToList();
        Assert.Equal(2 + 3 * 3, entries.Count);
        for (var i = 0; i < entries.Count; i++)
            Assert.StartsWith($"{i + 1} 0 obj", text[int.Parse(entries[i][..10])..]);
    }

    [Fact]
    public void ThePhotoBytesArePassedThroughUnchanged()
    {
        var photo = Jpeg(900, 1800);
        var pdf = ImagePdf.Build([photo]);

        Assert.True(pdf.AsSpan().IndexOf(photo) >= 0, "Das Foto muss unveraendert im PDF stehen.");
    }

    [Fact]
    public void GreyscaleAndColourPhotosGetTheirOwnColourSpace()
    {
        Assert.Contains("/DeviceGray", Encoding.Latin1.GetString(ImagePdf.Build([Jpeg(100, 100, components: 1)])));
        Assert.Contains("/DeviceRGB", Encoding.Latin1.GetString(ImagePdf.Build([Jpeg(100, 100, components: 3)])));
    }

    [Fact]
    public void APhotoThatIsOnlyUprightThroughItsExifRotationIsRefused()
    {
        var error = Assert.Throws<ReceiptImportException>(() => ImagePdf.Build([Jpeg(1000, 2000, exifOrientation: 6)]));
        Assert.Contains("Drehangabe", error.Message);

        // Orientierung 1 heisst "nichts zu drehen" und ist unbedenklich.
        ImagePdf.Build([Jpeg(1000, 2000, exifOrientation: 1)]);
    }

    [Fact]
    public void ThingsThatAreNotUsablePhotosAreRefused()
    {
        Assert.Throws<ReceiptImportException>(() => ImagePdf.Build([]));
        Assert.Throws<ReceiptImportException>(() => ImagePdf.Build([[0x89, 0x50, 0x4E, 0x47, 0, 0, 0, 0]]));
        Assert.Throws<ReceiptImportException>(() => ImagePdf.Build([Jpeg(100, 100, components: 4)]));
        Assert.Throws<ReceiptImportException>(() => ImagePdf.Build([[0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x04]]));
    }

    /// <summary>Ein JPEG, das nur so weit stimmt, wie ImagePdf es liest: Kopf mit Groesse und Farbraum.</summary>
    internal static byte[] Jpeg(int width, int height, int components = 3, int? exifOrientation = null)
    {
        var bytes = new List<byte> { 0xFF, 0xD8 };
        if (exifOrientation is { } orientation)
        {
            // APP1 mit Exif und einem TIFF-Kopf (little endian), ein Eintrag: Tag 0x0112, Typ SHORT.
            byte[] app1 =
            [
                .. "Exif\0\0"u8.ToArray(),
                (byte)'I', (byte)'I', 0x2A, 0x00, 0x08, 0x00, 0x00, 0x00,
                0x01, 0x00,
                0x12, 0x01, 0x03, 0x00, 0x01, 0x00, 0x00, 0x00, (byte)orientation, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00
            ];
            bytes.AddRange([0xFF, 0xE1, (byte)((app1.Length + 2) >> 8), (byte)((app1.Length + 2) & 0xFF)]);
            bytes.AddRange(app1);
        }
        var sofLength = 8 + components * 3;
        bytes.AddRange([0xFF, 0xC0, (byte)(sofLength >> 8), (byte)(sofLength & 0xFF), 8,
            (byte)(height >> 8), (byte)(height & 0xFF), (byte)(width >> 8), (byte)(width & 0xFF), (byte)components]);
        for (var i = 0; i < components; i++) bytes.AddRange([(byte)(i + 1), 0x11, 0x00]);
        bytes.AddRange([0xFF, 0xDA, 0x00, 0x08, 0x01, 0x01, 0x00, 0x00, 0x3F, 0x00, 0xAB, 0xCD, 0xFF, 0xD9]);
        return [.. bytes];
    }
}
