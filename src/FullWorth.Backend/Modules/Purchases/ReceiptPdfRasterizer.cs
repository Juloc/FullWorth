using FullWorth.Backend.Documents;

namespace FullWorth.Backend.Modules.Purchases;

/// <summary>
/// Process wrapper around Poppler for untrusted receipt PDFs. It never invokes a shell, enforces
/// timeouts and only returns bounded page information / rendered PNG bytes. The backend image installs
/// poppler-utils explicitly so local OCR can process every PDF page without depending on Codex.
/// </summary>
public static class ReceiptPdfRasterizer
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

    public static async Task<byte[]> RenderPageAsync(string absolutePdfPath, int pageNumber, int maxPages, CancellationToken ct)
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
                 "-singlefile", "-png", "-r", "180", absolutePdfPath, outputBase],
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
