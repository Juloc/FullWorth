using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using FullWorth.Backend.Documents;
using FullWorth.Backend.Validation;

namespace FullWorth.Backend.Modules.Compensation;

/// <summary>Der Text eines Dokuments aus dem Bereich Gehalt. Eine Schnittstelle nur, weil das Werkzeug dahinter (Tesseract) nicht ueberall liegt, wo getestet wird.</summary>
public interface IDocumentOcr
{
    /// <summary>Der Text der ersten <paramref name="maxPages"/> Seiten.</summary>
    Task<string> ReadTextAsync(Stream content, string fileName, int maxPages, CancellationToken ct);
}

/// <summary>
/// Die eine Kette, die eine Lohnabrechnung liest - fuer den Upload im Browser wie fuer den Import aus
/// Paperless: OCR einmal, dann strukturiert Codex den Text; ist Codex nicht erreichbar oder liefert
/// nichts Brauchbares, springt der deterministische Regex-Parser ein. So wird eine Abrechnung nie
/// halb gedeutet und als vollstaendig ausgegeben.
/// </summary>
public sealed class PayslipExtractionService(IDocumentOcr ocr, PayslipCodexExtractor codex) : IDocumentReader
{
    public string Kind => DocumentKinds.Payslip;

    async Task<object> IDocumentReader.ReadAsync(Guid userId, Stream content, string fileName, CancellationToken ct) =>
        await ExtractAsync(userId, content, fileName, ct);

    public async Task<PayslipExtractionResult> ExtractAsync(Guid userId, Stream content, string fileName, CancellationToken ct)
    {
        var text = await ocr.ReadTextAsync(content, fileName, 1, ct);
        if (string.IsNullOrWhiteSpace(text))
            return PayslipTextParser.Empty("OCR konnte keinen Text erkennen.");
        return await codex.TryStructureAsync(userId, text, ct) ?? PayslipTextParser.Parse(text);
    }
}

/// <summary>
/// Liest ein PDF oder Bild lokal. Ein digitales PDF hat eine Textebene, die genauer ist als jede Erkennung -
/// auch bei einer einzelnen Lohnabrechnung, deren Zahlen eine Erkennung gern um eine Stelle verfehlt -, also
/// wird zuerst sie gelesen; nur ohne brauchbaren Text werden die Seiten gerendert und mit Tesseract (deu+eng)
/// erkannt. Die Datei wird hier nie abgelegt.
/// </summary>
public sealed class LocalDocumentOcr : IDocumentOcr
{
    private const long MaxBytes = 25L * 1024 * 1024;
    // Weniger als das ist keine Textebene, sondern ein Scan mit ein paar Fusszeilen.
    private const int MinTextLayerCharacters = 200;
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".webp", ".tif", ".tiff", ".bmp" };

    public async Task<string> ReadTextAsync(Stream content, string fileName, int maxPages, CancellationToken ct)
    {
        if (content.Length <= 0) throw new ArgumentException("Die Datei ist leer.");
        if (content.Length > MaxBytes) throw new ArgumentException("Die Datei darf höchstens 25 MB groß sein.");

        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (extension != ".pdf" && !ImageExtensions.Contains(extension))
            throw new ArgumentException("Unterstützt werden PDF, JPG, PNG, WEBP, TIFF und BMP.");

        var workDir = Path.Combine(Path.GetTempPath(), $"fullworth-document-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        try
        {
            var source = Path.Combine(workDir, $"source{extension}");
            await using (var stream = File.Create(source)) await content.CopyToAsync(stream, ct);

            if (extension != ".pdf") return await RunProcessAsync("tesseract", [source, "stdout", "-l", "deu+eng", "--psm", "6"], ct);

            var layer = await RunProcessAsync("pdftotext", ["-l", maxPages.ToString(), "-layout", source, "-"], ct);
            if (layer.Count(char.IsLetterOrDigit) >= MinTextLayerCharacters) return layer;

            var prefix = Path.Combine(workDir, "page");
            await RunProcessAsync("pdftoppm", ["-f", "1", "-l", maxPages.ToString(), "-png", "-r", "220", source, prefix], ct);
            var pages = Directory.GetFiles(workDir, "page*.png").Order(StringComparer.Ordinal).ToList();
            if (pages.Count == 0) throw new InvalidOperationException("Die erste PDF-Seite konnte nicht gerendert werden.");

            var text = new StringBuilder();
            foreach (var page in pages)
                text.AppendLine(await RunProcessAsync("tesseract", [page, "stdout", "-l", "deu+eng", "--psm", "6"], ct));
            return text.ToString();
        }
        finally
        {
            try { Directory.Delete(workDir, true); } catch { /* best effort; never retain intentionally */ }
        }
    }

    private static async Task<string> RunProcessAsync(string fileName, IEnumerable<string> args, CancellationToken ct)
    {
        try { return await LocalTool.RunAsync(fileName, args, TimeSpan.FromSeconds(45), ct); }
        catch (LocalToolException exception)
        {
            throw new InvalidOperationException(exception.Kind == LocalToolFailure.Missing
                ? $"Lokales Extraktionswerkzeug '{fileName}' ist nicht verfügbar."
                : $"{fileName} konnte das Dokument nicht verarbeiten.", exception);
        }
    }
}

public static partial class PayslipTextParser
{
    private static readonly string[] GrossLabels = { "gesamtbrutto", "brutto gesamt", "steuerbrutto", "bruttoentgelt", "brutto" };
    private static readonly string[] NetLabels = { "nettoverdienst", "netto-entgelt", "netto entgelt", "netto" };
    private static readonly string[] PayoutLabels = { "auszahlungsbetrag", "zahlbetrag", "auszahlung", "überweisungsbetrag", "ueberweisungsbetrag", "überweisung" };
    private static readonly string[] WageTaxLabels = { "lohnsteuer", "lst" };
    private static readonly string[] SoliLabels = { "solidaritätszuschlag", "solidaritaetszuschlag", "soli" };
    private static readonly string[] ChurchLabels = { "kirchensteuer", "kist" };
    // Die knappen Kuerzel der Abzugszeilen stehen vorn: der volle Name steht auf vielen Abrechnungen noch einmal in
    // einer Tabelle der Beitragsgrundlagen, und dort ist die erste Zahl die Grundlage, nicht der Beitrag.
    private static readonly string[] PensionLabels = { "rv-beitrag", "rv beitrag", "rv an", "rv-an", "rentenversicherung" };
    private static readonly string[] UnemploymentLabels = { "av-beitrag", "av beitrag", "av an", "av-an", "arbeitslosenversicherung" };
    private static readonly string[] HealthLabels = { "kv-beitrag", "kv beitrag", "kv an", "kv-an", "krankenversicherung" };
    private static readonly string[] CareLabels = { "pv-beitrag", "pv beitrag", "pv an", "pv-an", "pflegeversicherung" };
    private static readonly string[] CompanyCarLabels = { "geldwerter vorteil", "firmenwagen", "pkw private nutzung", "pkw-nutzung", "pkw nutzung", "kfz-nutzung", "kfz nutzung" };
    private static readonly string[] BavLabels = { "entgeltumw", "betriebliche altersvorsorge", "direktversicherung", "bav" };
    private static readonly string[] BavEmployerLabels = { "ag-zuschuss bav", "ag zuschuss bav", "arbeitgeber bav", "ag direktversicherung", "ag-direktversich" };
    private static readonly string[] BonusLabels = { "bonus", "prämie", "praemie", "sonderzahlung", "tantieme" };

    public static PayslipExtractionResult Parse(string text)
    {
        var normalized = Normalize(text);
        var lines = normalized.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var detected = new List<string>();

        decimal? Pick(string name, string[] labels)
        {
            var value = FindAmount(lines, labels);
            if (value is not null) detected.Add(name);
            return value;
        }

        var period = FindPeriod(normalized);
        if (period is not null) detected.Add("Abrechnungszeitraum");
        var gross = Pick("Brutto", GrossLabels);
        var net = Pick("Netto", NetLabels);
        var payout = Pick("Auszahlung", PayoutLabels) ?? net;
        var wageTax = Pick("Lohnsteuer", WageTaxLabels);
        var soli = Pick("Solidaritätszuschlag", SoliLabels);
        var church = Pick("Kirchensteuer", ChurchLabels);
        var pension = Pick("Rentenversicherung", PensionLabels);
        var unemployment = Pick("Arbeitslosenversicherung", UnemploymentLabels);
        var health = Pick("Krankenversicherung", HealthLabels);
        var care = Pick("Pflegeversicherung", CareLabels);
        var car = Pick("Firmenwagen", CompanyCarLabels);
        var bavEmployer = Pick("bAV Arbeitgeber", BavEmployerLabels);
        var bav = Pick("bAV Arbeitnehmer", BavLabels);
        var bonus = Pick("Bonus", BonusLabels);

        decimal score = 0m;
        if (period is not null) score += 10m;
        if (gross is not null) score += 20m;
        if (net is not null) score += 20m;
        if (payout is not null) score += 10m;
        if (wageTax is not null) score += 10m;
        foreach (var value in new[] { pension, unemployment, health, care }) if (value is not null) score += 5m;
        if (soli is not null || church is not null || car is not null || bav is not null || bonus is not null) score += 10m;
        score = Math.Min(100m, score);

        var warnings = new List<string>();
        if (period is null) warnings.Add("Abrechnungsmonat nicht sicher erkannt.");
        if (gross is null) warnings.Add("Bruttolohn nicht sicher erkannt.");
        if (net is null) warnings.Add("Nettolohn nicht sicher erkannt.");
        if (score < 70m) warnings.Add("Niedrige Erkennungssicherheit. Werte vor dem Speichern vollständig prüfen.");
        else warnings.Add("OCR-Werte vor dem Speichern mit der Originalabrechnung abgleichen.");

        return new PayslipExtractionResult(
            period, gross, net, payout, wageTax, soli, church, pension, unemployment, health, care,
            car, bav, bavEmployer, bonus, score, detected.Distinct().ToArray(), warnings);
    }

    public static PayslipExtractionResult Empty(string warning) => new(
        null, null, null, null, null, null, null, null, null, null, null, null, null, null, null,
        0m, Array.Empty<string>(), new[] { warning });

    /// <summary>
    /// Der Betrag der ersten Zeile, die das Etikett traegt. Auf einer Abrechnung stehen hinter dem Etikett oft zwei
    /// Spalten, "Monat" und "Jahr" (die Jahressumme seit Januar): gemeint ist der Monat, also die ERSTE Zahl.
    /// Dass hier einmal die letzte genommen wurde, machte aus 3.567 € Brutto im Februar die 11.134 € der
    /// Jahressumme - und jeder Monat sah aus wie ein Gehaltssprung.
    ///
    /// Zeilen mit "pauschal" sind Abgaben des Arbeitgebers, keine Abzuege des Arbeitnehmers; und eine Zeile mit
    /// vier oder mehr Betraegen ist die Tabelle der Beitragsgrundlagen unter der Abrechnung (Grundlage Monat und
    /// Jahr, Beitrag Monat und Jahr), in der die erste Zahl die Grundlage ist und nicht der Beitrag.
    /// </summary>
    private static decimal? FindAmount(IEnumerable<string> lines, IEnumerable<string> labels)
    {
        foreach (var label in labels)
        {
            foreach (var line in lines)
            {
                if (!line.Contains(label, StringComparison.OrdinalIgnoreCase) || line.Contains("pauschal", StringComparison.OrdinalIgnoreCase)) continue;
                var matches = MoneyRegex().Matches(line);
                if (matches.Count >= 4) continue;
                foreach (Match match in matches)
                {
                    if (TryParseAmount(match.Value, out var amount)) return Math.Abs(amount);
                }
            }
        }
        return null;
    }

    private static DateOnly? FindPeriod(string text)
    {
        foreach (Match match in PeriodRegex().Matches(text))
        {
            if (int.TryParse(match.Groups[1].Value, out var month) && int.TryParse(match.Groups[2].Value, out var year)
                && month is >= 1 and <= 12 && year is >= 2000 and <= 2100)
                return new DateOnly(year, month, DateTime.DaysInMonth(year, month));
        }

        var months = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["januar"] = 1, ["februar"] = 2, ["märz"] = 3, ["maerz"] = 3, ["april"] = 4,
            ["mai"] = 5, ["juni"] = 6, ["juli"] = 7, ["august"] = 8, ["september"] = 9,
            ["oktober"] = 10, ["november"] = 11, ["dezember"] = 12
        };
        foreach (var pair in months)
        {
            var match = Regex.Match(text, $@"\b{Regex.Escape(pair.Key)}\s+(20\d{{2}})\b", RegexOptions.IgnoreCase);
            if (match.Success && int.TryParse(match.Groups[1].Value, out var year))
                return new DateOnly(year, pair.Value, DateTime.DaysInMonth(year, pair.Value));
        }
        return null;
    }

    /// <summary>
    /// Hier stand ein eigener Zahlenleser, der <see cref="ImportNumber"/> fast nachbaute und an einer
    /// Stelle davon abwich: bei genau einem Punkt und keinem Komma ließ er ihn stehen, las "3.500"
    /// also als dreieinhalb statt als dreitausendfünfhundert. Auf einer Abrechnung ist jede Zahl ein
    /// Betrag mit zwei Nachkommastellen, also ist eine dreistellige Endgruppe immer eine Tausendergruppe.
    ///
    /// Genau davor warnt CLAUDE.md: jeder Importer hat einmal seinen eigenen Parser mitgebracht, und
    /// zwei davon lasen "1234.56" als 123456.
    /// </summary>
    private static bool TryParseAmount(string token, out decimal amount)
    {
        var raw = token.Replace("€", "", StringComparison.Ordinal)
            .Replace("EUR", "", StringComparison.OrdinalIgnoreCase);
        try
        {
            var parsed = ImportNumber.TryParse(raw, ImportNumber.ThreeDigitTail.Grouping);
            amount = parsed ?? 0m;
            return parsed.HasValue;
        }
        catch (FormatException)
        {
            amount = 0m;
            return false;
        }
    }

    private static string Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text.Replace('\r', '\n')) builder.Append(char.IsControl(c) && c != '\n' ? ' ' : c);
        return Regex.Replace(builder.ToString(), "[ \\t]+", " ").ToLowerInvariant();
    }

    [GeneratedRegex(@"(?<!\d)([-+]?\d{1,3}(?:[. ]\d{3})*(?:,\d{2})|[-+]?\d+(?:,\d{2})|[-+]?\d+\.\d{2})(?:\s*(?:€|eur))?", RegexOptions.IgnoreCase)]
    private static partial Regex MoneyRegex();

    [GeneratedRegex(@"(?:abrechnungs(?:monat|zeitraum)?|monat|für|fuer)?\s*[:\-]?\s*(0?[1-9]|1[0-2])[./-](20\d{2})", RegexOptions.IgnoreCase)]
    private static partial Regex PeriodRegex();
}
