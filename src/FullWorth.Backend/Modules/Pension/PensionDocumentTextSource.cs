using System.Diagnostics;

namespace FullWorth.Backend.Modules.Pension;

/// <summary>
/// The one failure shape of <see cref="PensionDocumentTextSource"/>. It carries a
/// <see cref="Category"/> the caller writes straight into <c>BavDocuments.ExtractionError</c>, which is
/// 64 chars wide, is returned to the browser and may end up in a log — so the category is one of the
/// four constants below and never a tool's output, a file name or a line of the document.
///
/// What maps to what:
/// <list type="bullet">
///   <item><see cref="Unsupported"/> — the extension is not a PDF and not one of the accepted images.</item>
///   <item><see cref="TooLarge"/> — over <see cref="PensionDocumentTextSource.MaxBytes"/>, refused before
///         a single process starts.</item>
///   <item><see cref="ToolMissing"/> — <c>pdftotext</c>, <c>pdftoppm</c> or <c>tesseract</c> is not
///         installed, or a tool exited non-zero. A self-hosted installation without the poppler/tesseract
///         binaries reaches the review screen through manual entry, which is why this is a stated
///         category and not an internal error.</item>
///   <item><see cref="NoText"/> — the tools ran and produced nothing usable: no text layer and OCR that
///         recognised nothing. The document is kept, the review screen asks for the fields.</item>
/// </list>
/// The message is German because it is shown; it never quotes the document or a tool's stderr.
/// </summary>
public sealed class BavDocumentTextException(string category, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public const string Unsupported = "unsupported";
    public const string TooLarge = "too_large";
    public const string ToolMissing = "tool_missing";
    public const string NoText = "no_text";

    public string Category { get; } = category;
}

/// <summary>
/// Turns an uploaded pension document into one string per page.
///
/// Text layer first, OCR only when there is none. A Standmitteilung is usually generated digitally and
/// carries exact text; running Tesseract over it would replace correct numbers with recognised ones, and
/// a wrong Vertragsguthaben is the most expensive kind of wrong this feature can produce.
///
/// Two things the payslip pipeline could not do, which is why it was not reused: it OCRs page one only,
/// and it never asks whether a text layer exists. Both differences live here; the process runner and its
/// "tool not available" translation are deliberately the same shape as
/// <c>PayslipExtractor.RunProcessAsync</c> so an operator sees one behaviour for a missing binary.
/// </summary>
public sealed class PensionDocumentTextSource : IBavDocumentTextSource
{
    /// <summary>Same ceiling as the payslip extractor; the blob store refuses the same size on write.</summary>
    internal const long MaxBytes = 12 * 1024 * 1024;

    // A statement runs to a handful of pages; 30 is far past any real one. Past the cap the remaining
    // pages are dropped rather than the upload failing: the pages that matter are the early ones, and a
    // 200-page scan would otherwise occupy the host with OCR for minutes.
    private const int MaxPages = 30;

    // Under this many non-whitespace characters per page on average, the "text layer" is an artefact of
    // the generator (page furniture, a single header) and the document is really a scan.
    private const int MinCharsPerPageForTextLayer = 40;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".webp", ".tif", ".tiff", ".bmp" };

    public async Task<BavDocumentText> ReadAsync(byte[] content, string extension, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.Length == 0) throw new BavDocumentTextException(BavDocumentTextException.NoText, "Die Datei ist leer.");
        if (content.Length > MaxBytes)
            throw new BavDocumentTextException(BavDocumentTextException.TooLarge, "Die Datei darf höchstens 12 MB groß sein.");

        var normalized = (extension ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized.Length > 0 && normalized[0] != '.') normalized = "." + normalized;
        var isPdf = normalized == ".pdf";
        if (!isPdf && !ImageExtensions.Contains(normalized))
            throw new BavDocumentTextException(BavDocumentTextException.Unsupported, "Unterstützt werden PDF, JPG, PNG, WEBP, TIFF und BMP.");

        // The kept copy of this document is the encrypted blob. This one is plaintext, so it exists only
        // for the length of the call and the finally below is what guarantees it does not outlive it.
        var workDir = Path.Combine(Path.GetTempPath(), $"fullworth-pension-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        try
        {
            var source = Path.Combine(workDir, $"source{normalized}");
            await File.WriteAllBytesAsync(source, content, ct);

            if (!isPdf)
            {
                // A single image is a scan by definition: there is no text layer to prefer.
                var text = await OcrAsync(source, ct);
                if (string.IsNullOrWhiteSpace(text))
                    throw new BavDocumentTextException(BavDocumentTextException.NoText, "Im Dokument wurde kein lesbarer Text gefunden.");
                return new BavDocumentText([text], TextLayerUsed: false);
            }

            var layer = SplitPages(await RunProcessAsync(
                "pdftotext", ["-layout", "-f", "1", "-l", MaxPages.ToString(), source, "-"], TimeSpan.FromSeconds(60), ct));
            if (HasUsableTextLayer(layer)) return new BavDocumentText(layer, TextLayerUsed: true);

            var ocr = await OcrPdfAsync(source, workDir, Math.Max(layer.Count, 1), ct);
            if (ocr.All(string.IsNullOrWhiteSpace))
                throw new BavDocumentTextException(BavDocumentTextException.NoText, "Im Dokument wurde kein lesbarer Text gefunden.");
            return new BavDocumentText(ocr, TextLayerUsed: false);
        }
        finally
        {
            try { Directory.Delete(workDir, true); } catch { /* best effort; the plaintext is never kept on purpose */ }
        }
    }

    /// <summary>
    /// pdftotext separates pages with a form feed and emits one after the last page too, so the trailing
    /// empty entry is dropped. Splitting one process' output is what keeps this to a single call instead
    /// of one <c>-f/-l</c> invocation per page.
    /// </summary>
    internal static IReadOnlyList<string> SplitPages(string output)
    {
        if (string.IsNullOrEmpty(output)) return [];
        var pages = output.Split('\f');
        if (pages.Length > 1 && string.IsNullOrWhiteSpace(pages[^1])) pages = pages[..^1];
        return pages.Take(MaxPages).Select(page => page.Replace("\r\n", "\n").Trim('\n')).ToList();
    }

    internal static bool HasUsableTextLayer(IReadOnlyList<string> pages)
    {
        if (pages.Count == 0) return false;
        var characters = pages.Sum(page => page.Count(c => !char.IsWhiteSpace(c)));
        return characters >= (long)MinCharsPerPageForTextLayer * pages.Count;
    }

    private static async Task<IReadOnlyList<string>> OcrPdfAsync(string source, string workDir, int pageCount, CancellationToken ct)
    {
        var last = Math.Min(pageCount, MaxPages);
        var prefix = Path.Combine(workDir, "page");
        await RunProcessAsync(
            "pdftoppm", ["-png", "-r", "220", "-f", "1", "-l", last.ToString(), source, prefix], TimeSpan.FromMinutes(5), ct);

        // pdftoppm pads the page number to the width of the page count ("page-1.png" or "page-01.png"),
        // so the files are found and then ordered numerically rather than by name.
        var rendered = Directory.GetFiles(workDir, "page-*.png")
            .Select(path => (path, number: PageNumber(path)))
            .Where(entry => entry.number > 0)
            .OrderBy(entry => entry.number)
            .Select(entry => entry.path)
            .ToList();
        if (rendered.Count == 0)
            throw new BavDocumentTextException(BavDocumentTextException.ToolMissing, "Die Seiten des PDFs konnten nicht gerendert werden.");

        var pages = new List<string>(rendered.Count);
        foreach (var image in rendered) pages.Add(await OcrAsync(image, ct));
        return pages;
    }

    private static Task<string> OcrAsync(string image, CancellationToken ct) => RunProcessAsync(
        "tesseract", [image, "stdout", "-l", "deu+eng", "--psm", "6"], TimeSpan.FromSeconds(120), ct);

    private static int PageNumber(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var dash = name.LastIndexOf('-');
        return dash >= 0 && int.TryParse(name[(dash + 1)..], out var number) ? number : 0;
    }

    private static async Task<string> RunProcessAsync(string fileName, IEnumerable<string> args, TimeSpan timeoutAfter, CancellationToken ct)
    {
        var start = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
        }
        catch (Exception exception)
        {
            throw new BavDocumentTextException(BavDocumentTextException.ToolMissing,
                $"Lokales Extraktionswerkzeug '{fileName}' ist nicht verfügbar.", exception);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(timeoutAfter);
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        var output = await stdout;
        _ = await stderr;
        if (process.ExitCode != 0)
            // Unlike the payslip runner this does not quote stderr: poppler and tesseract put the input
            // path into their error text, and a pension document's path must never reach a log line.
            throw new BavDocumentTextException(BavDocumentTextException.ToolMissing,
                $"Lokales Extraktionswerkzeug '{fileName}' konnte das Dokument nicht verarbeiten.");
        return output;
    }
}
