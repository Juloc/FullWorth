using System.Diagnostics;

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
        var result = await RunAsync("pdfinfo", [absolutePdfPath], TimeSpan.FromSeconds(15), ct);
        if (result.ExitCode != 0)
            throw new InvalidOperationException("Receipt PDF could not be inspected.");

        foreach (var line in result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
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
        try
        {
            var invariant = System.Globalization.CultureInfo.InvariantCulture;
            var result = await RunAsync(
                "pdftoppm",
                ["-f", pageNumber.ToString(invariant), "-l", pageNumber.ToString(invariant),
                 "-singlefile", "-png", "-r", "180", absolutePdfPath, outputBase],
                TimeSpan.FromSeconds(30),
                ct);
            if (result.ExitCode != 0 || !File.Exists(outputPath))
                throw new InvalidOperationException("Receipt PDF page could not be rendered.");
            return await File.ReadAllBytesAsync(outputPath, ct);
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { /* best effort */ }
        }
    }

    private static async Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Required PDF helper '{fileName}' is unavailable.", ex);
        }

        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"PDF helper '{fileName}' timed out.");
        }

        return new(process.ExitCode, await stdout, await stderr);
    }

    private sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);
}
