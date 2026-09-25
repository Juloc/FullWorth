using System.ComponentModel;
using System.Diagnostics;

namespace FullWorth.Backend.Documents;

public enum LocalToolFailure { Missing, Failed, TimedOut }

/// <summary>
/// Ein lokales Werkzeug lief nicht durch. Die Meldung nennt nur Werkzeug und Art - nie stderr: poppler
/// und tesseract setzen den Pfad der Eingabedatei hinein, und der Pfad eines hochgeladenen Dokuments
/// gehoert weder in eine Logzeile noch in eine Antwort an den Browser. Was der Nutzer liest, formuliert
/// der Aufrufer aus <see cref="Kind"/>.
/// </summary>
public sealed class LocalToolException(string tool, LocalToolFailure kind, Exception? inner = null)
    : Exception($"Local tool '{tool}': {kind}.", inner)
{
    public string Tool { get; } = tool;
    public LocalToolFailure Kind { get; } = kind;
}

/// <summary>
/// Startet ein lokales Werkzeug (pdftotext, pdftoppm, pdfinfo, tesseract) ohne Shell und liefert stdout.
/// Laeuft es ueber die Frist, wird es samt Kindprozessen beendet, statt im Hintergrund weiterzurechnen.
/// </summary>
public static class LocalTool
{
    public static async Task<string> RunAsync(string fileName, IEnumerable<string> args, TimeSpan timeout, CancellationToken ct)
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
        try { process.Start(); }
        catch (Win32Exception exception) { throw new LocalToolException(fileName, LocalToolFailure.Missing, exception); }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
            // stderr wird gelesen, damit ein gespraechiges Werkzeug nicht an einer vollen Pipe haengt -
            // und verworfen (siehe LocalToolException).
            var stderr = process.StandardError.ReadToEndAsync(deadline.Token);
            await process.WaitForExitAsync(deadline.Token);
            var output = await stdout;
            await stderr;
            if (process.ExitCode != 0) throw new LocalToolException(fileName, LocalToolFailure.Failed);
            return output;
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* schon beendet */ }
            if (ct.IsCancellationRequested) throw;
            throw new LocalToolException(fileName, LocalToolFailure.TimedOut);
        }
    }
}
