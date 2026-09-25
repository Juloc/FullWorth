using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace FullWorth.Backend.Documents;

/// <summary>Ein Wort auf einer PDF-Seite, mit seiner Box in Punkt (oben links ist 0/0).</summary>
public sealed record PdfWord(double Left, double Top, double Right, double Bottom, string Text)
{
    public double Middle => (Top + Bottom) / 2;
}

/// <summary>Die Worte, die auf einer Hoehe stehen - von links nach rechts.</summary>
public sealed record PdfLine(double Middle, IReadOnlyList<PdfWord> Words)
{
    public string Text => string.Join(' ', Words.Select(word => word.Text));
}

/// <summary>Ein PDF liess sich nicht lesen - mit einem Grund, den die Oberflaeche zeigen kann.</summary>
public sealed class PdfWordsException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Die Worte eines PDF mit ihrer Lage auf der Seite, zu Zeilen zusammengesetzt.
///
/// Warum Koordinaten und nicht der Textmodus: ein Kontoauszug ist eine Tabelle, und im Textmodus
/// ("pdftotext -layout") verschieben sich die Betraege gegen ihre Zeilen, sobald der Erzeuger Betrag und
/// Buchungstext als getrennte Bloecke setzt. Beim Ikano-Auszug stand so jeder Betrag eine Zeile zu hoch
/// - ein Leser auf diesem Text haette jeden Betrag still der falschen Buchung zugeordnet. Die Lage eines
/// Wortes dagegen luegt nicht: was auf einer Hoehe steht, gehoert zusammen.
/// </summary>
public interface IPdfWordSource
{
    /// <summary>Je Seite die Zeilen, oben beginnend.</summary>
    Task<IReadOnlyList<IReadOnlyList<PdfLine>>> ReadLinesAsync(byte[] pdf, CancellationToken ct);
}

/// <summary>
/// Liest die Worte mit poppler ("pdftotext -bbox-layout"). Das Werkzeug liegt schon im Image, fuer die
/// Altersvorsorge-Dokumente - hier entsteht kein zweiter PDF-Stack, nur ein zweiter Blick auf denselben.
/// </summary>
public sealed partial class PopplerPdfWordSource : IPdfWordSource
{
    /// <summary>Dieselbe Obergrenze wie fuer die uebrigen Dokumente.</summary>
    public const long MaxBytes = 12 * 1024 * 1024;

    // Ein Kontoauszug hat eine Handvoll Seiten. Was darueber liegt, ist kein Auszug.
    private const int MaxPages = 30;

    public async Task<IReadOnlyList<IReadOnlyList<PdfLine>>> ReadLinesAsync(byte[] pdf, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        if (pdf.Length == 0) throw new PdfWordsException("Die Datei ist leer.");
        if (pdf.Length > MaxBytes) throw new PdfWordsException("Die Datei darf höchstens 12 MB groß sein.");

        // Der Klartext eines Kontoauszugs liegt nur fuer die Dauer des Aufrufs auf der Platte.
        var workDir = Path.Combine(Path.GetTempPath(), $"fullworth-pdf-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        try
        {
            var source = Path.Combine(workDir, "source.pdf");
            await File.WriteAllBytesAsync(source, pdf, ct);
            string xhtml;
            try
            {
                xhtml = await LocalTool.RunAsync("pdftotext",
                    ["-enc", "UTF-8", "-bbox-layout", "-f", "1", "-l", MaxPages.ToString(CultureInfo.InvariantCulture), source, "-"],
                    TimeSpan.FromSeconds(60), ct);
            }
            catch (LocalToolException exception)
            {
                throw new PdfWordsException(exception.Kind == LocalToolFailure.Missing
                    ? "Das Werkzeug zum Lesen von PDF-Dateien (pdftotext) ist nicht verfügbar."
                    : "Die PDF-Datei ließ sich nicht lesen.", exception);
            }
            return ParseBboxLayout(xhtml).Select(page => AssembleLines(page)).ToList();
        }
        finally
        {
            try { Directory.Delete(workDir, true); } catch { /* best effort; der Klartext wird nie behalten */ }
        }
    }

    /// <summary>Die Worte je Seite aus der Ausgabe von <c>-bbox-layout</c>.</summary>
    internal static IReadOnlyList<IReadOnlyList<PdfWord>> ParseBboxLayout(string xhtml) =>
        PagePattern().Split(xhtml)
            .Skip(1)
            .Select(page => (IReadOnlyList<PdfWord>)WordPattern().Matches(page)
                .Select(match => new PdfWord(
                    Number(match.Groups["x1"].Value), Number(match.Groups["y1"].Value),
                    Number(match.Groups["x2"].Value), Number(match.Groups["y2"].Value),
                    WebUtility.HtmlDecode(match.Groups["text"].Value)))
                .Where(word => word.Text.Length > 0)
                .ToList())
            .ToList();

    /// <summary>
    /// Setzt Zeilen aus der Lage der Worte zusammen: Worte, deren Mitte naeher als
    /// <paramref name="tolerance"/> Punkt beieinander liegt, stehen auf einer Zeile.
    ///
    /// Die Mitte, nicht die Oberkante: ein Betrag in einer anderen Schriftgroesse als sein Buchungstext
    /// hat eine andere Oberkante, steht aber auf derselben Zeile. 2,5 Punkt sind weit unter einem
    /// Zeilenabstand (Kontoauszuege setzen ihre Zeilen 15 bis 20 Punkt auseinander) und weit ueber dem
    /// Versatz, den unterschiedliche Schriften auf einer Zeile haben.
    /// </summary>
    internal static IReadOnlyList<PdfLine> AssembleLines(IReadOnlyList<PdfWord> words, double tolerance = 2.5)
    {
        var lines = new List<(double Middle, List<PdfWord> Words)>();
        foreach (var word in words.OrderBy(word => word.Middle))
        {
            var index = lines.FindIndex(line => Math.Abs(line.Middle - word.Middle) < tolerance);
            if (index < 0) lines.Add((word.Middle, [word]));
            else lines[index].Words.Add(word);
        }
        return lines
            .Select(line => new PdfLine(line.Middle, line.Words.OrderBy(word => word.Left).ToList()))
            .ToList();
    }

    private static double Number(string value) => double.Parse(value, CultureInfo.InvariantCulture);

    [GeneratedRegex("<page ", RegexOptions.CultureInvariant)]
    private static partial Regex PagePattern();

    [GeneratedRegex("""<word xMin="(?<x1>[\d.]+)" yMin="(?<y1>[\d.]+)" xMax="(?<x2>[\d.]+)" yMax="(?<y2>[\d.]+)">(?<text>[^<]*)</word>""",
        RegexOptions.CultureInvariant)]
    private static partial Regex WordPattern();
}
