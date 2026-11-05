using FullWorth.Backend.Documents;

namespace FullWorth.Backend.Modules.Purchases;

/// <summary>
/// Process wrapper around Poppler for untrusted receipt PDFs. It never invokes a shell, enforces
/// timeouts and only returns bounded page information / rendered PNG bytes. The backend image installs
/// poppler-utils explicitly so local OCR can process every PDF page without depending on Codex.
/// </summary>
public static partial class ReceiptPdfRasterizer
{
    public static async Task<int> GetPageCountAsync(string absolutePdfPath, int maxPages, CancellationToken ct)
    {
        if (maxPages <= 0) throw new ArgumentOutOfRangeException(nameof(maxPages));
        var stdout = await RunAsync("pdfinfo", [absolutePdfPath], TimeSpan.FromSeconds(15), "Receipt PDF could not be inspected.", ct);

        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith("Pages:", StringComparison.OrdinalIgnoreCase)) continue;
            if (!int.TryParse(line["Pages:".Length..].Trim(), out var pages) || pages <= 0) break;
            if (pages > maxPages)
                throw new InvalidOperationException($"Receipt PDF has {pages} pages; the limit is {maxPages}.");
            return pages;
        }

        throw new InvalidOperationException("Receipt PDF page count could not be determined.");
    }

    /// <summary>The page as PNG. 180 dpi is enough to show to a model; text recognition reads small print and wants <paramref name="dpi"/> 300.</summary>
    public static async Task<byte[]> RenderPageAsync(string absolutePdfPath, int pageNumber, int maxPages, CancellationToken ct, int dpi = 180)
    {
        if (pageNumber <= 0 || pageNumber > maxPages) throw new ArgumentOutOfRangeException(nameof(pageNumber));

        var tempRoot = Path.Combine(Path.GetTempPath(), $"fullworth-receipt-pdf-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var outputBase = Path.Combine(tempRoot, "page");
        var outputPath = $"{outputBase}.png";
        const string notRendered = "Receipt PDF page could not be rendered.";
        try
        {
            var invariant = System.Globalization.CultureInfo.InvariantCulture;
            await RunAsync(
                "pdftoppm",
                ["-f", pageNumber.ToString(invariant), "-l", pageNumber.ToString(invariant),
                 "-singlefile", "-png", "-r", dpi.ToString(invariant), absolutePdfPath, outputBase],
                TimeSpan.FromSeconds(30),
                notRendered,
                ct);
            if (!File.Exists(outputPath))
                throw new InvalidOperationException(notRendered);
            return await File.ReadAllBytesAsync(outputPath, ct);
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// The text a scanned or digital PDF already carries (paperless-ngx embeds its own OCR in every
    /// document). It costs nothing to read and is one more reading of the page; null when there is none.
    /// </summary>
    public static async Task<string?> ReadPageTextAsync(string absolutePdfPath, int pageNumber, CancellationToken ct)
    {
        var page = pageNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            var text = await LocalTool.RunAsync("pdftotext", ["-f", page, "-l", page, "-layout", absolutePdfPath, "-"], TimeSpan.FromSeconds(15), ct);
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (LocalToolException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether the page was typeset by a program (a digital eBon) rather than scanned. A scan is one
    /// big picture of the paper - even when a text layer was added to it afterwards, that text is OCR
    /// and only as good as the OCR was. A digital page carries real text and no picture of any size.
    /// Anything that cannot be inspected counts as "not digital": the caller then takes the careful road.
    /// Returns the page's exact text when it is digital, otherwise null.
    /// </summary>
    public static async Task<string?> ReadDigitalPageTextAsync(string absolutePdfPath, int pageNumber, CancellationToken ct)
    {
        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        var page = pageNumber.ToString(invariant);
        try
        {
            var info = await LocalTool.RunAsync("pdfinfo", ["-f", page, "-l", page, absolutePdfPath], TimeSpan.FromSeconds(15), ct);
            var size = PageSizeRegex.Match(info);
            if (!size.Success) return null;
            var pageArea = double.Parse(size.Groups[1].Value, invariant) * double.Parse(size.Groups[2].Value, invariant);
            if (pageArea <= 0) return null;

            var images = await LocalTool.RunAsync("pdfimages", ["-list", "-f", page, "-l", page, absolutePdfPath], TimeSpan.FromSeconds(15), ct);
            foreach (var line in images.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Skip(2))
            {
                var columns = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                // page num type width height color comp bpc enc interp object ID x-ppi y-ppi size ratio
                if (columns.Length < 15
                    || !double.TryParse(columns[3], System.Globalization.NumberStyles.Float, invariant, out var width)
                    || !double.TryParse(columns[4], System.Globalization.NumberStyles.Float, invariant, out var height)
                    || !double.TryParse(columns[12], System.Globalization.NumberStyles.Float, invariant, out var xPpi)
                    || !double.TryParse(columns[13], System.Globalization.NumberStyles.Float, invariant, out var yPpi)
                    || xPpi <= 0 || yPpi <= 0)
                    return null;
                // The displayed size in points; a logo or QR code is small, a scan fills the page.
                if (width / xPpi * 72 * (height / yPpi * 72) >= pageArea * MaxDecorationShare) return null;
            }
        }
        catch (LocalToolException)
        {
            return null;
        }

        var text = await ReadPageTextAsync(absolutePdfPath, pageNumber, ct);
        return text is not null && text.Count(char.IsLetterOrDigit) >= MinDigitalTextCharacters ? text : null;
    }

    /// <summary>A picture covering more than this share of the page is the page itself, not a logo.</summary>
    private const double MaxDecorationShare = 0.2;
    private const int MinDigitalTextCharacters = 40;

    [System.Text.RegularExpressions.GeneratedRegex(@"Page\s+\d+\s+size:\s*([\d.]+)\s*x\s*([\d.]+)")]
    private static partial System.Text.RegularExpressions.Regex PageSizeRegex { get; }

    private static async Task<string> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, string failed, CancellationToken ct)
    {
        try { return await LocalTool.RunAsync(fileName, arguments, timeout, ct); }
        catch (LocalToolException exception)
        {
            throw exception.Kind switch
            {
                LocalToolFailure.Missing => new InvalidOperationException($"Required PDF helper '{fileName}' is unavailable.", exception),
                LocalToolFailure.TimedOut => new TimeoutException($"PDF helper '{fileName}' timed out.", exception),
                _ => new InvalidOperationException(failed, exception)
            };
        }
    }
}
