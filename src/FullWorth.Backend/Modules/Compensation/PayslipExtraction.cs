using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace FullWorth.Backend.Modules.Compensation;

public static partial class PayslipExtractor
{
    private const long MaxBytes = 12 * 1024 * 1024;
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".webp", ".tif", ".tiff", ".bmp" };

    public static async Task<PayslipExtractionResult> ExtractAsync(IFormFile file, CancellationToken ct)
    {
        if (file.Length <= 0) throw new ArgumentException("Die Datei ist leer.");
        if (file.Length > MaxBytes) throw new ArgumentException("Die Datei darf höchstens 12 MB groß sein.");

        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (extension != ".pdf" && !ImageExtensions.Contains(extension))
            throw new ArgumentException("Unterstützt werden PDF, JPG, PNG, WEBP, TIFF und BMP.");

        var workDir = Path.Combine(Path.GetTempPath(), $"fullworth-payslip-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        try
        {
            var source = Path.Combine(workDir, $"source{extension}");
            await using (var stream = File.Create(source)) await file.CopyToAsync(stream, ct);

            var image = source;
            if (extension == ".pdf")
            {
                var prefix = Path.Combine(workDir, "page");
                await RunProcessAsync("pdftoppm", new[] { "-f", "1", "-singlefile", "-png", "-r", "220", source, prefix }, ct);
                image = prefix + ".png";
                if (!File.Exists(image)) throw new InvalidOperationException("Die erste PDF-Seite konnte nicht gerendert werden.");
            }

            var text = await RunProcessAsync("tesseract", new[] { image, "stdout", "-l", "deu+eng", "--psm", "6" }, ct);
            if (string.IsNullOrWhiteSpace(text))
                return PayslipTextParser.Empty("OCR konnte keinen Text erkennen.");

            return PayslipTextParser.Parse(text);
        }
        finally
        {
            try { Directory.Delete(workDir, true); } catch { /* best effort; never retain intentionally */ }
        }
    }

    private static async Task<string> RunProcessAsync(string fileName, IEnumerable<string> args, CancellationToken ct)
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
        try
        {
            process.Start();
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException($"Lokales Extraktionswerkzeug '{fileName}' ist nicht verfügbar.", exception);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        var output = await stdout;
        var error = await stderr;
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{fileName} konnte die Lohnabrechnung nicht verarbeiten: {Trim(error, 300)}");
        return output;
    }

    private static string Trim(string value, int max) => value.Length <= max ? value : value[..max];
}

public static partial class PayslipTextParser
{
    private static readonly string[] GrossLabels = { "gesamtbrutto", "brutto gesamt", "steuerbrutto", "bruttoentgelt", "brutto" };
    private static readonly string[] NetLabels = { "nettoverdienst", "netto-entgelt", "netto entgelt", "netto" };
    private static readonly string[] PayoutLabels = { "auszahlungsbetrag", "auszahlung", "überweisungsbetrag", "ueberweisungsbetrag", "überweisung" };
    private static readonly string[] WageTaxLabels = { "lohnsteuer", "lst" };
    private static readonly string[] SoliLabels = { "solidaritätszuschlag", "solidaritaetszuschlag", "soli" };
    private static readonly string[] ChurchLabels = { "kirchensteuer", "kist" };
    private static readonly string[] PensionLabels = { "rentenversicherung", "rv-beitrag", "rv beitrag", "rv an", "rv-an" };
    private static readonly string[] UnemploymentLabels = { "arbeitslosenversicherung", "av-beitrag", "av beitrag", "av an", "av-an" };
    private static readonly string[] HealthLabels = { "krankenversicherung", "kv-beitrag", "kv beitrag", "kv an", "kv-an" };
    private static readonly string[] CareLabels = { "pflegeversicherung", "pv-beitrag", "pv beitrag", "pv an", "pv-an" };
    private static readonly string[] CompanyCarLabels = { "geldwerter vorteil", "firmenwagen", "pkw-nutzung", "pkw nutzung", "kfz-nutzung", "kfz nutzung" };
    private static readonly string[] BavLabels = { "entgeltumwandlung", "betriebliche altersvorsorge", "direktversicherung", "bav" };
    private static readonly string[] BavEmployerLabels = { "ag-zuschuss bav", "ag zuschuss bav", "arbeitgeber bav", "ag direktversicherung" };
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

    private static decimal? FindAmount(IEnumerable<string> lines, IEnumerable<string> labels)
    {
        foreach (var label in labels)
        {
            foreach (var line in lines)
            {
                if (!line.Contains(label, StringComparison.OrdinalIgnoreCase)) continue;
                var matches = MoneyRegex().Matches(line);
                for (var index = matches.Count - 1; index >= 0; index--)
                {
                    if (TryParseAmount(matches[index].Value, out var amount)) return Math.Abs(amount);
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

    private static bool TryParseAmount(string token, out decimal amount)
    {
        var raw = token.Replace("€", "", StringComparison.Ordinal).Replace("EUR", "", StringComparison.OrdinalIgnoreCase)
            .Replace(" ", "", StringComparison.Ordinal).Trim();
        if (raw.Contains(',')) raw = raw.Replace(".", "", StringComparison.Ordinal).Replace(',', '.');
        else if (raw.Count(c => c == '.') > 1) raw = raw.Replace(".", "", StringComparison.Ordinal);
        return decimal.TryParse(raw, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out amount);
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
