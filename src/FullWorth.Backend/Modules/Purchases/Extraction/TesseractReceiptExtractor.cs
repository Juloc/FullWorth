using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace FullWorth.Backend.Modules.Purchases.Extraction;

/// <summary>
/// Local, provider-free OCR via the Tesseract CLI (installed in the backend image). Runs the engine
/// on a raster receipt image and hands the recognized text to <see cref="ReceiptTextParser"/>. It
/// fails soft: a missing binary, a PDF (Tesseract needs a rasterizer), an unreadable image or a
/// timeout all yield an empty result rather than an error, so receipt capture never breaks — manual
/// entry and review remain available.
/// </summary>
public sealed class TesseractReceiptExtractor(IOptions<ReceiptExtractionOptions> options, ILogger<TesseractReceiptExtractor> logger) : IReceiptExtractor
{
    private static readonly HashSet<string> ImageContentTypes = new(StringComparer.OrdinalIgnoreCase)
        { "image/jpeg", "image/png", "image/webp", "image/heic", "image/tiff", "image/bmp" };

    public string Provider => "tesseract";

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
            var text = await RunAsync(temp, options.Value, ct);
            return string.IsNullOrWhiteSpace(text)
                ? ReceiptExtractionResult.Empty(Provider)
                : ReceiptTextParser.Parse(text, request.CurrencyHint);
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

    private static async Task<string> RunAsync(string imagePath, ReceiptExtractionOptions opts, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = string.IsNullOrWhiteSpace(opts.TesseractPath) ? "tesseract" : opts.TesseractPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(imagePath);
        startInfo.ArgumentList.Add("stdout");
        startInfo.ArgumentList.Add("-l");
        startInfo.ArgumentList.Add(string.IsNullOrWhiteSpace(opts.Languages) ? "eng" : opts.Languages);

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(timeout.Token);
        return await stdout;
    }
}
