// Texterkennung fuer Broker-PDFs ohne Textebene. Lag bis 2026-09-15 hinter der Route (#134).

using System.Security.Cryptography;
using System.Text;
using FullWorth.Backend.Documents;

namespace FullWorth.Backend.Modules.Portfolio;

internal static class BrokerPdfOcr
{
    public static async Task<IReadOnlyList<string>> ExtractPagesAsync(byte[] content, int maxPages, int maxChars, CancellationToken ct)
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"fullworth-pdf-ocr-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        var input = Path.Combine(tempDirectory, "input.pdf");
        var prefix = Path.Combine(tempDirectory, "page");
        try
        {
            await File.WriteAllBytesAsync(input, content, ct);
            await RunAsync("pdftoppm", ["-f", "1", "-l", maxPages.ToString(), "-jpeg", "-r", "220", input, prefix], TimeSpan.FromSeconds(45), ct);

            var images = Directory.EnumerateFiles(tempDirectory, "page-*.jpg")
                .OrderBy(path => path, StringComparer.Ordinal)
                .Take(maxPages)
                .ToArray();
            if (images.Length == 0)
                throw new InvalidDataException("PDF rasterization produced no pages.");

            var result = new List<string>(images.Length);
            var totalChars = 0;
            foreach (var image in images)
            {
                var text = await RunAsync("tesseract", [image, "stdout", "-l", "deu+eng", "--psm", "6"], TimeSpan.FromSeconds(35), ct);
                if (text.Length + totalChars > maxChars)
                    text = text[..Math.Max(0, maxChars - totalChars)];
                result.Add(text);
                totalChars += text.Length;
                if (totalChars >= maxChars) break;
            }
            return result;
        }
        finally
        {
            try { Directory.Delete(tempDirectory, recursive: true); } catch { }
        }
    }

    // Die Route antwortet mit der Meldung - stderr haette ihr den Pfad der Temp-Datei mitgegeben.
    private static async Task<string> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
    {
        try { return await LocalTool.RunAsync(fileName, arguments, timeout, ct); }
        catch (LocalToolException exception) when (exception.Kind == LocalToolFailure.Missing)
        {
            throw new FileNotFoundException($"{fileName} is not installed.", exception);
        }
        catch (LocalToolException exception)
        {
            throw new InvalidDataException(exception.Kind == LocalToolFailure.TimedOut ? $"{fileName} timed out." : $"{fileName} failed.", exception);
        }
    }
}
