// Texterkennung fuer Broker-PDFs ohne Textebene. Lag bis 2026-09-15 hinter der Route (#134).

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using FullWorth.Backend.Security;

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
                var text = await RunCaptureAsync("tesseract", [image, "stdout", "-l", "deu+eng", "--psm", "6"], TimeSpan.FromSeconds(35), ct);
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

    private static async Task RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeoutValue, CancellationToken ct)
    {
        using var process = CreateProcess(fileName, arguments, redirectOutput: false);
        Start(process, fileName);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(timeoutValue);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new InvalidDataException($"{fileName} timed out.");
        }
        if (process.ExitCode != 0)
        {
            var error = (await process.StandardError.ReadToEndAsync(ct)).Trim();
            throw new InvalidDataException(string.IsNullOrWhiteSpace(error) ? $"{fileName} failed." : $"{fileName} failed: {error}");
        }
    }

    private static async Task<string> RunCaptureAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeoutValue, CancellationToken ct)
    {
        using var process = CreateProcess(fileName, arguments, redirectOutput: true);
        Start(process, fileName);
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(timeoutValue);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new InvalidDataException($"{fileName} timed out.");
        }
        if (process.ExitCode != 0)
        {
            var error = (await process.StandardError.ReadToEndAsync(ct)).Trim();
            throw new InvalidDataException(string.IsNullOrWhiteSpace(error) ? $"{fileName} failed." : $"{fileName} failed: {error}");
        }
        return await stdout;
    }

    private static Process CreateProcess(string fileName, IReadOnlyList<string> arguments, bool redirectOutput)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = redirectOutput,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        return new Process { StartInfo = startInfo };
    }

    private static void Start(Process process, string fileName)
    {
        try
        {
            if (!process.Start()) throw new FileNotFoundException($"{fileName} could not be started.");
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new FileNotFoundException($"{fileName} is not installed.", exception);
        }
    }
}
