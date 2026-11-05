using System.Text;

namespace FullWorth.Backend.Modules.Purchases.ReceiptImports;

/// <summary>
/// Setzt JPEG-Fotos zu einem mehrseitigen PDF zusammen, ohne sie neu zu kodieren: Paperless nimmt pro
/// Dokument eine Datei, ein Beleg aus mehreren Kamerafotos muss also eine werden. Die Bytes der Fotos
/// kommen unveraendert als DCT-Strom in das PDF - das PDF ist exakt so scharf wie die Aufnahme.
/// </summary>
internal static class ImagePdf
{
    /// <summary>Die Seitengroesse folgt dem Foto bei dieser Aufloesung; Paperless/OCRmyPDF leiten daraus ihre Rasterweite ab.</summary>
    private const double DotsPerInch = 300;

    /// <summary>Baut das PDF; wirft <see cref="ReceiptImportException"/>, wenn ein Foto sich nicht ohne Veraenderung einbetten laesst.</summary>
    public static byte[] Build(IReadOnlyList<byte[]> photos)
    {
        if (photos.Count == 0) throw new ReceiptImportException("Es gibt keine Fotos, aus denen ein Dokument entstehen koennte.");
        var images = photos.Select(Inspect).ToList();

        using var pdf = new MemoryStream();
        var offsets = new List<long>();
        void Begin() { offsets.Add(pdf.Position); Write(pdf, $"{offsets.Count} 0 obj\n"); }
        void End() => Write(pdf, "endobj\n");

        // Die vier Bytes ueber 127 in der zweiten Zeile sagen Programmen, dass die Datei binaer ist.
        pdf.Write("%PDF-1.4\n%"u8);
        pdf.Write([0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n']);
        // Objekte: 1 Katalog, 2 Seitenbaum, danach je Foto Seite, Inhalt, Bild.
        Begin(); Write(pdf, "<< /Type /Catalog /Pages 2 0 R >>\n"); End();
        Begin(); Write(pdf, $"<< /Type /Pages /Count {images.Count} /Kids [{string.Join(' ', images.Select((_, i) => $"{3 + i * 3} 0 R"))}] >>\n"); End();

        for (var i = 0; i < images.Count; i++)
        {
            var image = images[i];
            var width = Points(image.Width);
            var height = Points(image.Height);
            var page = 3 + i * 3;
            var content = $"q {width} 0 0 {height} 0 0 cm /Im0 Do Q\n";

            Begin();
            Write(pdf, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {width} {height}] /Resources << /XObject << /Im0 {page + 2} 0 R >> >> /Contents {page + 1} 0 R >>\n");
            End();
            Begin(); Write(pdf, $"<< /Length {content.Length} >>\nstream\n{content}endstream\n"); End();
            Begin();
            Write(pdf, $"<< /Type /XObject /Subtype /Image /Width {image.Width} /Height {image.Height} /ColorSpace /{(image.Components == 1 ? "DeviceGray" : "DeviceRGB")} /BitsPerComponent 8 /Filter /DCTDecode /Length {image.Bytes.Length} >>\nstream\n");
            pdf.Write(image.Bytes);
            Write(pdf, "\nendstream\n");
            End();
        }

        var xref = pdf.Position;
        Write(pdf, $"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets) Write(pdf, $"{offset:D10} 00000 n \n");
        Write(pdf, $"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return pdf.ToArray();
    }

    private static string Points(int pixels) => (pixels * 72 / DotsPerInch).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    private static void Write(Stream stream, string text) => stream.Write(Encoding.ASCII.GetBytes(text));

    private sealed record Jpeg(byte[] Bytes, int Width, int Height, int Components);

    /// <summary>
    /// Liest Groesse und Farbraum aus dem JPEG-Kopf. Zwei Dinge lehnt es ab, weil das PDF sie falsch
    /// zeigen wuerde: CMYK-Bilder (die Farben kaemen invertiert an) und Fotos, die nur ueber die
    /// Drehangabe im Exif-Block aufrecht stehen - ein PDF kennt diese Angabe nicht.
    /// </summary>
    private static Jpeg Inspect(byte[] bytes)
    {
        if (bytes.Length < 4 || bytes[0] != 0xFF || bytes[1] != 0xD8) throw new ReceiptImportException("Eine der Dateien ist kein JPEG-Foto.");
        var position = 2;
        while (position + 4 <= bytes.Length)
        {
            if (bytes[position] != 0xFF) throw new ReceiptImportException("Ein JPEG-Foto ist beschaedigt.");
            var marker = bytes[position + 1];
            if (marker == 0xFF) { position++; continue; }
            if (marker is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7)) { position += 2; continue; }
            var length = (bytes[position + 2] << 8) | bytes[position + 3];
            if (length < 2 || position + 2 + length > bytes.Length) throw new ReceiptImportException("Ein JPEG-Foto ist beschaedigt.");

            if (marker == 0xE1 && IsRotatedExif(bytes.AsSpan(position + 4, length - 2).ToArray()))
                throw new ReceiptImportException("Ein Foto steht nur ueber seine Drehangabe aufrecht und laesst sich so nicht in ein PDF uebernehmen. Mit der Belegkamera aufgenommene Fotos haben das Problem nicht.");

            if (marker is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC))
            {
                if (length < 8) throw new ReceiptImportException("Ein JPEG-Foto ist beschaedigt.");
                var height = (bytes[position + 5] << 8) | bytes[position + 6];
                var width = (bytes[position + 7] << 8) | bytes[position + 8];
                var components = bytes[position + 9];
                if (width == 0 || height == 0) throw new ReceiptImportException("Ein JPEG-Foto ist beschaedigt.");
                if (components is not (1 or 3)) throw new ReceiptImportException("Ein Foto hat einen Farbraum, den das PDF nicht unveraendert uebernehmen kann.");
                return new Jpeg(bytes, width, height, components);
            }
            if (marker == 0xDA) break;
            position += 2 + length;
        }
        throw new ReceiptImportException("Ein JPEG-Foto ist beschaedigt.");
    }

    /// <summary>Ob ein Exif-Block eine Drehung verlangt (Tag 0x0112 mit einem anderen Wert als 1).</summary>
    private static bool IsRotatedExif(byte[] app1)
    {
        if (app1.Length < 14 || !app1.AsSpan(0, 6).SequenceEqual("Exif\0\0"u8)) return false;
        var tiff = app1[6..];
        var littleEndian = tiff[0] == (byte)'I';
        int U16(int at) => at + 2 > tiff.Length ? -1 : littleEndian ? tiff[at] | (tiff[at + 1] << 8) : (tiff[at] << 8) | tiff[at + 1];
        long U32(int at) => at + 4 > tiff.Length ? -1 : littleEndian
            ? tiff[at] | ((long)tiff[at + 1] << 8) | ((long)tiff[at + 2] << 16) | ((long)tiff[at + 3] << 24)
            : ((long)tiff[at] << 24) | ((long)tiff[at + 1] << 16) | ((long)tiff[at + 2] << 8) | tiff[at + 3];

        var ifd = U32(4);
        if (ifd < 8 || ifd > tiff.Length) return false;
        var count = U16((int)ifd);
        for (var i = 0; i < count; i++)
        {
            var entry = (int)ifd + 2 + i * 12;
            if (U16(entry) == 0x0112) return U16(entry + 8) is not (1 or -1);
        }
        return false;
    }
}
