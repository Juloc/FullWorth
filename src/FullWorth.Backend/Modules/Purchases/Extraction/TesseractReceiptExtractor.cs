using FullWorth.Backend.Documents;
using Microsoft.Extensions.Options;

namespace FullWorth.Backend.Modules.Purchases.Extraction;

/// <summary>
/// Local, provider-free OCR via the Tesseract CLI (installed in the backend image). Runs the engine
/// on a raster receipt image - twice, with two page layouts - and hands each recognized text to
/// <see cref="ReceiptTextParser"/>; <see cref="ReceiptReadingSelector"/> keeps the reading whose sums agree. It
/// fails soft: a missing binary, a PDF (Tesseract needs a rasterizer), an unreadable image or a
/// timeout all yield an empty result rather than an error, so receipt capture never breaks — manual
/// entry and review remain available.
/// </summary>
public sealed class TesseractReceiptExtractor(IOptions<ReceiptExtractionOptions> options, ILogger<TesseractReceiptExtractor> logger) : IReceiptExtractor
{
    private static readonly HashSet<string> ImageContentTypes = new(StringComparer.OrdinalIgnoreCase)
        { "image/jpeg", "image/png", "image/webp", "image/heic", "image/tiff", "image/bmp" };

    public string Provider => "tesseract";

    /// <summary>
    /// Tesseract page segmentation modes: 6 reads the page as one block of text, which suits a receipt;
    /// 4 reads it as a single column of variable-size lines. On receipts they fail on different lines.
    /// </summary>
    private static readonly int[] PageLayouts = [6, 4];

    private Task<string> ReadAsync(string image, int layout, CancellationToken ct)
    {
        var opts = options.Value;
        return LocalTool.RunAsync(
            string.IsNullOrWhiteSpace(opts.TesseractPath) ? "tesseract" : opts.TesseractPath,
            [image, "stdout", "-l", string.IsNullOrWhiteSpace(opts.Languages) ? "eng" : opts.Languages, "--oem", "1", "--psm", layout.ToString(System.Globalization.CultureInfo.InvariantCulture)],
            TimeSpan.FromSeconds(30), ct);
    }

    public async Task<ReceiptExtractionResult> ExtractAsync(ReceiptExtractionRequest request, CancellationToken ct)
    {
        if (request.Content is null || request.Content.Length == 0) return ReceiptExtractionResult.Empty(Provider);

        var ext = Path.GetExtension(request.FileName).ToLowerInvariant();
        var isImage = ImageContentTypes.Contains(request.ContentType)
            || ext is ".jpg" or ".jpeg" or ".png" or ".webp" or ".heic" or ".tiff" or ".bmp";
        if (!isImage) return ReceiptExtractionResult.Empty(Provider); // e.g. PDF: needs a rasterizer, out of scope

        var temp = Path.Combine(Path.GetTempPath(), $"fullworth-ocr-{Guid.NewGuid():N}{(ext.Length == 0 ? ".png" : ext)}");
        try
        {
            await File.WriteAllBytesAsync(temp, request.Content, ct);
            var readings = new List<ReceiptExtractionResult>();
            if (!string.IsNullOrWhiteSpace(request.EmbeddedText)) readings.Add(ReceiptTextParser.Parse(request.EmbeddedText, request.CurrencyHint));
            foreach (var layout in PageLayouts)
            {
                var text = await ReadAsync(temp, layout, ct);
                if (!string.IsNullOrWhiteSpace(text)) readings.Add(ReceiptTextParser.Parse(text, request.CurrencyHint));
            }
            return readings.Count == 0 ? ReceiptExtractionResult.Empty(Provider) : ReceiptReadingSelector.Choose(readings);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Tesseract OCR failed; returning empty extraction");
            return ReceiptExtractionResult.Empty(Provider);
        }
        finally
        {
            try { File.Delete(temp); } catch { /* best effort */ }
        }
    }
}
