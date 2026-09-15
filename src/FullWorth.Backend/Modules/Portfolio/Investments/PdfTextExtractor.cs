// Den Text aus einem PDF holen. Gehoerte nie zu einer Route (#134).

using FullWorth.Backend.Validation;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using FullWorth.Backend.Security;

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
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "pdftotext",
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                }
            };
            process.StartInfo.ArgumentList.Add("-layout");
            process.StartInfo.ArgumentList.Add("-nopgbrk");
            process.StartInfo.ArgumentList.Add("-enc");
            process.StartInfo.ArgumentList.Add("UTF-8");
            process.StartInfo.ArgumentList.Add(input);
            process.StartInfo.ArgumentList.Add(output);

            try
            {
                if (!process.Start()) throw new FileNotFoundException("pdftotext could not be started.");
            }
            catch (System.ComponentModel.Win32Exception exception)
            {
                throw new FileNotFoundException("pdftotext is not installed.", exception);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                throw new InvalidDataException("PDF text extraction timed out.");
            }
            if (process.ExitCode != 0)
            {
                var error = (await process.StandardError.ReadToEndAsync(ct)).Trim();
                throw new InvalidDataException(string.IsNullOrWhiteSpace(error) ? "PDF text extraction failed." : $"PDF text extraction failed: {error}");
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
