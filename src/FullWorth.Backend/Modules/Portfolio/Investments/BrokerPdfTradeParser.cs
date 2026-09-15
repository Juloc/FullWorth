// Aus einem Broker-PDF Handel lesen. Lag bis 2026-09-15 in derselben Datei wie die Route, die es
// benutzt - 268 Zeilen Parser hinter 125 Zeilen Endpunkt (#134).

using FullWorth.Backend.Validation;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace FullWorth.Backend.Modules.Portfolio;

public static partial class BrokerPdfTradeParser
{
    private static readonly Regex IsinRegex = new(@"\b[A-Z]{2}[A-Z0-9]{10}\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex DateRegex = new(@"(?<!\d)(?<date>\d{1,2}\.\d{1,2}\.\d{4})(?!\d)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CurrencyRegex = new(@"\b(EUR|USD|GBP|CHF|SEK|NOK|DKK|PLN|CZK|HUF|JPY|CAD|AUD)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly string MoneyNumber = @"[+-]?(?:\d{1,3}(?:\.\d{3})+|\d+)(?:,\d{1,8})?|[+-]?\d+\.\d{1,8}";

    public static BrokerPdfParseResult Parse(string text, string externalKey = "broker-pdf:test")
    {
        if (string.IsNullOrWhiteSpace(text))
            return new("unknown", 0m, null, ["PDF text is empty."]);

        var normalized = NormalizeText(text);
        var broker = DetectBroker(normalized);
        var warnings = new List<string>();
        var type = DetectTradeType(normalized);
        if (type is null)
        {
            warnings.Add("Transaction type could not be recognized.");
            return new(broker, 0.2m, null, warnings);
        }

        var tradeDate = FindDate(normalized,
            "Ausführungstag", "Ausfuehrungstag", "Ausführungsdatum", "Ausfuehrungsdatum",
            "Handelstag", "Schlusstag", "Geschäftstag", "Geschaeftstag", "Datum");
        if (tradeDate is null)
        {
            var fallback = DateRegex.Match(normalized);
            if (fallback.Success && TryDate(fallback.Groups["date"].Value, out var parsedDate))
            {
                tradeDate = parsedDate;
                warnings.Add("Trade date was inferred from the first date in the document.");
            }
        }
        if (tradeDate is null)
        {
            warnings.Add("Trade date is missing.");
            return new(broker, 0.3m, null, warnings);
        }

        var settlement = FindDate(normalized, "Valuta", "Wertstellung", "Settlement");
        var isin = IsinRegex.Match(normalized.ToUpperInvariant()) is { Success: true } isinMatch ? isinMatch.Value : null;
        var wkn = FindToken(normalized, @"\bWKN\b\s*[:\-]?\s*(?<value>[A-Z0-9]{6})\b");
        var securityName = FindSecurityName(normalized, isin);
        var quantity = FindNumber(normalized, "Stückzahl", "Stueckzahl", "Stück", "Stueck", "Anzahl", "Nominale");
        // Ein Kurs ist ein Stückpreis und trägt drei oder vier Nachkommastellen; ein Kurswert ist ein
        // Betrag mit zweien. Genau daran hing der Unterschied, den der frühere eigene Leser nicht kannte.
        var price = FindMoneyValue(normalized, ImportNumber.ThreeDigitTail.Decimal, out var priceCurrency,
            "Ausführungskurs", "Ausfuehrungskurs", "Kurs", "Preis");
        var gross = FindMoneyValue(normalized, ImportNumber.ThreeDigitTail.Grouping, out var grossCurrency,
            "Kurswert", "Bruttobetrag", "Brutto");
        var amount = FindMoneyValue(normalized, ImportNumber.ThreeDigitTail.Grouping, out var amountCurrency,
            "Ausmachender Betrag", "Endbetrag", "Abrechnungsbetrag", "Gesamtbetrag", "Gesamtsumme",
            "Zu Ihren Lasten", "Zu Ihren Gunsten", "Gutschrift", "Belastung");

        if (amount is null)
        {
            amount = gross ?? (price.HasValue && quantity.HasValue ? Math.Abs(price.Value * quantity.Value) : null);
            if (amount.HasValue) warnings.Add("Net amount was derived because no labelled final amount was found.");
        }
        if (amount is null || amount <= 0m)
        {
            warnings.Add("Transaction amount is missing.");
            return new(broker, 0.45m, null, warnings);
        }

        if (type is "buy" or "sell")
        {
            if (quantity is null or <= 0m) warnings.Add("Quantity was not recognized; review will reject the row until corrected.");
            if (string.IsNullOrWhiteSpace(isin) && string.IsNullOrWhiteSpace(wkn) && string.IsNullOrWhiteSpace(securityName))
                warnings.Add("Security identity was not recognized; review will require a security mapping.");
        }

        var fees = SumLabeledMoney(normalized,
            "Provision", "Orderentgelt", "Transaktionsentgelt", "Transaktionsgebühr", "Transaktionsgebuehr",
            "Fremde Spesen", "Börsenplatzentgelt", "Boersenplatzentgelt", "Handelsplatzgebühr", "Handelsplatzgebuehr");
        var withholding = SumLabeledMoney(normalized, "Quellensteuer", "ausländische Quellensteuer", "auslaendische Quellensteuer");
        var taxes = SumLabeledMoney(normalized,
            "Kapitalertragsteuer", "Solidaritätszuschlag", "Solidaritaetszuschlag", "Kirchensteuer");

        var currency = (amountCurrency ?? grossCurrency ?? priceCurrency ?? FindCurrency(normalized) ?? "EUR").ToUpperInvariant();
        var confidence = 0.55m;
        if (broker != "unknown") confidence += 0.08m;
        if (isin is not null || wkn is not null) confidence += 0.12m;
        if (quantity.HasValue || type is not ("buy" or "sell")) confidence += 0.08m;
        if (price.HasValue || type is not ("buy" or "sell")) confidence += 0.06m;
        if (amountCurrency is not null) confidence += 0.05m;
        confidence = Math.Min(0.95m, confidence);

        var trade = new BrokerPdfTrade(
            tradeDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            type,
            settlement?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            securityName,
            isin,
            wkn,
            null,
            Format(quantity),
            Format(price),
            Format(gross),
            Format(amount)!,
            currency,
            Format(fees) ?? "0",
            Format(taxes) ?? "0",
            Format(withholding) ?? "0",
            externalKey);

        if (Regex.Matches(normalized, @"Wertpapierabrechnung|Kaufabrechnung|Verkaufsabrechnung", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Count > 1)
            warnings.Add("The PDF appears to contain multiple confirmations. Only the first safely recognizable transaction is imported.");

        return new(broker, confidence, trade, warnings);
    }

    private static string NormalizeText(string text) => text.Replace('\u00A0', ' ').Replace("\r\n", "\n").Replace('\r', '\n');

    private static string DetectBroker(string text)
    {
        if (Contains(text, "TRADE REPUBLIC")) return "trade-republic";
        if (Contains(text, "SCALABLE CAPITAL")) return "scalable-capital";
        if (Contains(text, "ING-DIBA") || Regex.IsMatch(text, @"\bING\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return "ing";
        if (Contains(text, "DEUTSCHE KREDITBANK") || Regex.IsMatch(text, @"\bDKB\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return "dkb";
        if (Contains(text, "COMDIRECT")) return "comdirect";
        if (Contains(text, "FLATEX")) return "flatex";
        return "unknown";
    }

    private static string? DetectTradeType(string text)
    {
        var head = text.Length > 5000 ? text[..5000] : text;
        if (Regex.IsMatch(head, @"(?:Wertpapierabrechnung|Abrechnung|Kaufabrechnung)[^\n]{0,30}\bKauf\b|\bSparplanausf(?:ü|ue)hrung\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return "buy";
        if (Regex.IsMatch(head, @"(?:Wertpapierabrechnung|Abrechnung|Verkaufsabrechnung)[^\n]{0,30}\bVerkauf\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return "sell";
        if (Regex.IsMatch(head, @"Dividend(?:e|engutschrift)|Ertragsgutschrift|Ausschüttung|Ausschuettung", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return "dividend";
        if (Regex.IsMatch(head, @"Zinsgutschrift|\bZinsen\b|\bZins\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return "interest";
        if (Regex.IsMatch(head, @"\bVerkauf\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return "sell";
        if (Regex.IsMatch(head, @"\bKauf\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return "buy";
        return null;
    }

    private static DateOnly? FindDate(string text, params string[] labels)
    {
        foreach (var label in labels)
        {
            var match = Regex.Match(text, $@"{Regex.Escape(label)}[^\r\n]{{0,60}}?(?<date>\d{{1,2}}\.\d{{1,2}}\.\d{{4}})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (match.Success && TryDate(match.Groups["date"].Value, out var date)) return date;
        }
        return null;
    }

    private static bool TryDate(string value, out DateOnly date) =>
        DateOnly.TryParseExact(value, ["d.M.yyyy", "dd.MM.yyyy", "d.MM.yyyy", "dd.M.yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    private static decimal? FindNumber(string text, params string[] labels)
    {
        foreach (var label in labels)
        {
            var match = Regex.Match(text, $@"{Regex.Escape(label)}[^\r\n]{{0,40}}?(?<value>{MoneyNumber})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            // Stückzahl und Nominale: "1.234" ist hier eins Komma zwei drei vier, kein Tausender.
            if (match.Success && TryDecimal(match.Groups["value"].Value, ImportNumber.ThreeDigitTail.Decimal, out var value))
                return Math.Abs(value);
        }
        return null;
    }

    /// <summary>
    /// Der Anrufer sagt, ob er einen Betrag liest oder einen Stückpreis. Beides steht in derselben
    /// Abrechnung, und bei "1.234" gehen die Antworten um den Faktor tausend auseinander.
    /// </summary>
    private static decimal? FindMoneyValue(
        string text, ImportNumber.ThreeDigitTail tail, out string? currency, params string[] labels)
    {
        foreach (var label in labels)
        {
            var match = Regex.Match(text, $@"{Regex.Escape(label)}[^\r\n]{{0,80}}?(?<value>{MoneyNumber})\s*(?<currency>EUR|USD|GBP|CHF|SEK|NOK|DKK|PLN|CZK|HUF|JPY|CAD|AUD)?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (match.Success && TryDecimal(match.Groups["value"].Value, tail, out var value))
            {
                currency = match.Groups["currency"].Success ? match.Groups["currency"].Value.ToUpperInvariant() : null;
                return Math.Abs(value);
            }
        }
        currency = null;
        return null;
    }

    private static decimal SumLabeledMoney(string text, params string[] labels)
    {
        decimal total = 0m;
        foreach (var label in labels)
        {
            foreach (Match match in Regex.Matches(text, $@"{Regex.Escape(label)}[^\r\n]{{0,80}}?(?<value>{MoneyNumber})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                // Gebühren und Steuern sind Beträge mit zwei Nachkommastellen: "1.234" ist 1234.
                if (TryDecimal(match.Groups["value"].Value, ImportNumber.ThreeDigitTail.Grouping, out var value))
                    total += Math.Abs(value);
        }
        return Math.Round(total, 8, MidpointRounding.AwayFromZero);
    }

    private static string? FindToken(string text, string pattern)
    {
        var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? match.Groups["value"].Value.Trim().ToUpperInvariant() : null;
    }

    private static string? FindSecurityName(string text, string? isin)
    {
        foreach (var label in new[] { "Wertpapierbezeichnung", "Bezeichnung", "Wertpapier" })
        {
            var match = Regex.Match(text, $@"{Regex.Escape(label)}\s*[:\-]?\s*(?<value>[^\r\n]{{3,120}})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (match.Success)
            {
                var value = CleanSecurityName(match.Groups["value"].Value, isin);
                if (value is not null) return value;
            }
        }

        if (isin is null) return null;
        var lines = text.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0).ToArray();
        var index = Array.FindIndex(lines, line => line.Contains(isin, StringComparison.OrdinalIgnoreCase));
        if (index > 0)
        {
            var candidate = CleanSecurityName(lines[index - 1], isin);
            if (candidate is not null && candidate.Length >= 3 && !DateRegex.IsMatch(candidate)) return candidate;
        }
        return null;
    }

    private static string? CleanSecurityName(string value, string? isin)
    {
        var result = Regex.Replace(value, @"\s+", " ").Trim(' ', ':', '-', '|');
        if (isin is not null) result = result.Replace(isin, "", StringComparison.OrdinalIgnoreCase).Trim(' ', ':', '-', '|');
        result = Regex.Replace(result, @"\bWKN\b.*$", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Trim();
        return result.Length is >= 3 and <= 120 ? result : null;
    }

    /// <summary>
    /// Hier stand ein eigener Zahlenleser. Er kannte den Unterschied nicht, um den es in einer
    /// Depotabrechnung geht: "1.234" ist als Kurswert vierzehnhundertvierunddreißig und als Stückzahl
    /// eins Komma zwei drei vier. Ohne Komma im Text ließ er den Punkt stehen, las also beides als
    /// 1,234 - und derselbe Helfer bediente Kurs UND Kurswert, konnte für beide also gar nicht
    /// richtig sein.
    ///
    /// <see cref="ImportNumber"/> beantwortet genau diese Frage, und zwar je Feld
    /// (<see cref="ImportNumber.ThreeDigitTail"/>). Deshalb sagt jede Fundstelle jetzt, was sie liest.
    /// </summary>
    private static bool TryDecimal(string value, ImportNumber.ThreeDigitTail tail, out decimal result)
    {
        try
        {
            var parsed = ImportNumber.TryParse(value, tail);
            result = parsed ?? 0m;
            return parsed.HasValue;
        }
        catch (FormatException)
        {
            result = 0m;
            return false;
        }
    }

    private static string? FindCurrency(string text)
    {
        var match = CurrencyRegex.Match(text);
        return match.Success ? match.Value.ToUpperInvariant() : null;
    }

    private static string? Format(decimal? value) => value.HasValue ? value.Value.ToString("0.########", CultureInfo.InvariantCulture) : null;
    private static bool Contains(string text, string value) => text.Contains(value, StringComparison.OrdinalIgnoreCase);
}
