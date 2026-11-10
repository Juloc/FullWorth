using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using FullWorth.Backend.Validation;

namespace FullWorth.Backend.Modules.Compensation;

/// <summary>
/// Liest einen Arbeitsvertrag oder Nachtrag: OCR (bei einem digitalen PDF die Textebene), dann Codex, und
/// nur wenn Codex nichts Brauchbares liefert der deterministische Parser. Anders als bei der Abrechnung ist
/// der Parser hier ein Notbehelf: ein Vertrag ist Fliesstext, und was ein Regex darin findet, ist weniger
/// als was Codex liest - er liefert nur, was sich eindeutig an einem Stichwort festmachen laesst, und
/// laesst den Rest leer fuer die Eingabe von Hand.
/// </summary>
public sealed class ContractExtractionService(IDocumentOcr ocr, ContractCodexExtractor codex) : IDocumentReader
{
    // Ein Vertrag hat mehr als eine Seite; ein Nachtrag meist eine. Acht reichen fuer alles, was Gehalt,
    // Arbeitszeit und Urlaub nennt.
    private const int MaxPages = 8;

    public string Kind => DocumentKinds.Contract;

    async Task<object> IDocumentReader.ReadAsync(Guid userId, Stream content, string fileName, CancellationToken ct) =>
        await ExtractAsync(userId, content, fileName, ct);

    public async Task<ContractExtractionResult> ExtractAsync(Guid userId, Stream content, string fileName, CancellationToken ct)
    {
        var text = await ocr.ReadTextAsync(content, fileName, MaxPages, ct);
        if (string.IsNullOrWhiteSpace(text)) return ContractTextParser.Empty("OCR konnte keinen Text erkennen.");
        return await codex.TryStructureAsync(userId, text, ct) ?? ContractTextParser.Parse(text);
    }
}

public static partial class ContractTextParser
{
    public static ContractExtractionResult Empty(string warning) =>
        new(null, null, null, null, null, null, null, null, null, null, [], 0m, [], [warning]);

    public static ContractExtractionResult Parse(string text)
    {
        var flat = Regex.Replace(text.Replace(' ', ' '), @"[ \t]+", " ");
        var detected = new List<string>();
        var warnings = new List<string>();
        T? Found<T>(string label, T? value) where T : struct { if (value is not null) detected.Add(label); return value; }
        string? FoundText(string label, string? value) { if (value is not null) detected.Add(label); return value; }

        var kind = Regex.IsMatch(flat, @"(?i)nachtrag|änderungsvereinbarung|aenderungsvereinbarung|ergänzungsvereinbarung|änderungsvertrag")
            ? ContractKinds.Addendum
            : Regex.IsMatch(flat, @"(?i)arbeitsvertrag|anstellungsvertrag|dienstvertrag") ? ContractKinds.Contract : null;
        var validFrom = Found("Gültig ab", ValidFrom(flat));
        var gross = Found("Bruttogehalt", GrossMonthly(flat, warnings));
        var hours = Found("Wochenstunden", Decimal(flat, @"(?i)(\d{1,2}(?:[.,]\d{1,2})?)\s*(?:wochenstunden|stunden\s*(?:pro|je|in der|/)\s*woche|std\.?\s*/\s*woche|stunden wöchentlich)", ImportNumber.ThreeDigitTail.Decimal));
        var vacation = Found("Urlaubstage", Decimal(flat, @"(?i)urlaub[^.\n]{0,80}?(\d{2}(?:,\d)?)\s*(?:arbeitstage|werktage|urlaubstage|tage)", ImportNumber.ThreeDigitTail.Decimal)
            ?? Decimal(flat, @"(?i)(\d{2}(?:,\d)?)\s*(?:arbeitstage|werktage|urlaubstage)\s*(?:pro|je|im)\s*(?:kalender)?jahr", ImportNumber.ThreeDigitTail.Decimal));
        var car = Regex.IsMatch(flat, @"(?i)dienstwagen|firmenwagen|dienstfahrzeug") ? (bool?)true : null;
        if (car is not null) detected.Add("Dienstwagen");
        var bav = Found("bAV Arbeitgeberanteil", Decimal(flat, @"(?i)(?:arbeitgeber(?:zuschuss|anteil)|zuschuss des arbeitgebers)[^.\n]{0,100}?(\d{1,5}(?:[.,]\d{2})?)\s*(?:€|eur)"));
        var notice = FoundText("Kündigungsfrist", Notice(flat));
        var employer = FoundText("Arbeitgeber", Employer(flat));

        var score = 0m;
        if (validFrom is not null) score += 25m;
        if (gross is not null) score += 30m;
        if (kind is not null) score += 10m;
        if (employer is not null) score += 10m;
        if (hours is not null) score += 10m;
        if (vacation is not null) score += 10m;
        if (notice is not null) score += 5m;
        if (validFrom is null) warnings.Add("Gültig-ab-Datum nicht sicher erkannt.");
        if (gross is null) warnings.Add("Bruttogehalt nicht sicher erkannt.");
        warnings.Add("Aus dem Text gelesen, nicht von Codex - alle Felder mit dem Original abgleichen.");
        return new(validFrom, kind, employer, gross, hours, vacation, car, bav, notice, null, [], Math.Min(100m, score), detected.Distinct().ToArray(), warnings);
    }

    // Eine Zahl aus einem Dokument liest ImportNumber, nie eine Kultur: "5.400" sind bei einem Betrag
    // fuenftausendvierhundert, "37.5" bei Stunden siebenunddreissigeinhalb - das entscheidet die Art des Feldes.
    private static decimal? Decimal(string text, string pattern, ImportNumber.ThreeDigitTail tail = ImportNumber.ThreeDigitTail.Grouping)
    {
        var match = Regex.Match(text, pattern);
        if (!match.Success) return null;
        try { return ImportNumber.TryParse(match.Groups[1].Value, tail); }
        catch (FormatException) { return null; }
    }

    private static DateOnly? ValidFrom(string text)
    {
        var match = Regex.Match(text, @"(?i)(?:ab dem|ab dem|beginnt am|beginn des arbeitsverhältnisses|gültig ab|mit wirkung (?:zum|ab dem|vom)|rückwirkend zum|wirksam ab)[^\d\n]{0,20}(\d{1,2})\.\s?(\d{1,2})\.\s?(\d{4})");
        if (!match.Success) return null;
        return int.TryParse(match.Groups[3].Value, out var y) && int.TryParse(match.Groups[2].Value, out var m) && int.TryParse(match.Groups[1].Value, out var d)
               && y is >= 1980 and <= 2100 && m is >= 1 and <= 12 && d >= 1 && d <= DateTime.DaysInMonth(y, m)
            ? new DateOnly(y, m, d)
            : null;
    }

    // Ein Monatsbetrag steht neben "Bruttogehalt", "Bruttomonatsgehalt" oder "monatliches Gehalt"; ein
    // Jahresbetrag wird durch zwoelf geteilt und als solcher gekennzeichnet.
    private static decimal? GrossMonthly(string text, List<string> warnings)
    {
        var monthly = Decimal(text, @"(?i)(?<!jahres)(?:brutto\s*monats\s*gehalt|bruttomonatsgehalt|monatliche(?:s|n)?\s*(?:brutto)?\s*(?:gehalt|vergütung)|bruttogehalt|bruttovergütung|brutto(?:-)?entgelt)[^\d\n]{0,60}(\d{1,3}(?:\.\d{3})*(?:,\d{1,2})?|\d+(?:,\d{1,2})?)\s*(?:€|eur)");
        if (monthly is not null) return monthly;
        var yearly = Decimal(text, @"(?i)(?:jahresbrutto(?:gehalt)?|jahresgehalt|bruttojahresgehalt|jahresvergütung)[^\d\n]{0,60}(\d{1,3}(?:\.\d{3})*(?:,\d{1,2})?)\s*(?:€|eur)");
        if (yearly is null) return null;
        warnings.Add("Aus einem Jahresgehalt auf zwölf Monate umgerechnet.");
        return Math.Round(yearly.Value / 12m, 2);
    }

    private static string? Notice(string text)
    {
        var match = Regex.Match(text, @"(?i)kündigungsfrist[^\d\n]{0,60}(\d{1,2}\s*(?:wochen|monat\w*|tage\w*)[^.\n]{0,60})");
        return match.Success ? match.Groups[1].Value.Trim().TrimEnd(',', ';') : null;
    }

    private static string? Employer(string text)
    {
        var match = Regex.Match(text, @"(?i)zwischen\s+(?:der\s+|dem\s+|der firma\s+)?([^\n,(]{3,80}?(?:GmbH(?: & Co\. KG)?|mbH|AG|SE|KG|UG|GbR|e\. ?V\.))");
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }
}
