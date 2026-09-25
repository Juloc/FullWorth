// Den Text aus einem PDF holen. Gehoerte nie zu einer Route (#134).

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using FullWorth.Backend.Documents;

namespace FullWorth.Backend.Modules.Portfolio;

internal static class PdfTextExtractor
{
    public static async Task<string> ExtractAsync(byte[] content, int maxChars, CancellationToken ct)
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"fullworth-pdf-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        var input = Path.Combine(tempDirectory, "input.pdf");
        var output = Path.Combine(tempDirectory, "output.txt");
        try
        {
            await File.WriteAllBytesAsync(input, content, ct);
            try
            {
                await LocalTool.RunAsync("pdftotext", ["-layout", "-nopgbrk", "-enc", "UTF-8", input, output], TimeSpan.FromSeconds(20), ct);
            }
            catch (LocalToolException exception) when (exception.Kind == LocalToolFailure.Missing)
            {
                throw new FileNotFoundException("pdftotext is not installed.", exception);
            }
            catch (LocalToolException exception)
            {
                // Die Route antwortet mit der Meldung - stderr haette ihr den Pfad der Temp-Datei mitgegeben.
                throw new InvalidDataException(exception.Kind == LocalToolFailure.TimedOut
                    ? "PDF text extraction timed out."
                    : "PDF text extraction failed.", exception);
            }
            if (!File.Exists(output)) throw new InvalidDataException("PDF text extraction produced no output.");
            var info = new FileInfo(output);
            if (info.Length > maxChars * 4L) throw new InvalidDataException("Extracted PDF text is too large.");
            var text = await File.ReadAllTextAsync(output, Encoding.UTF8, ct);
            return text.Length > maxChars ? text[..maxChars] : text;
        }
        finally
        {
            try { Directory.Delete(tempDirectory, recursive: true); } catch { }
        }
    }
}
