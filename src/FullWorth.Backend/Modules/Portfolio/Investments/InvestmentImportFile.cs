using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using FullWorth.Backend.Validation;

namespace FullWorth.Backend.Modules.Portfolio;

/// <summary>
/// CSV und XLSX in Zeilen verwandeln, und die einzelnen Werte darin lesen.
///
/// Zwei Stellen hier sind keine Bequemlichkeit, sondern die Antwort auf echte Dateien:
///
/// Ein Datumsfeld enthaelt bei Brokern haeufig einen vollen Zeitstempel, obwohl fachlich ein
/// Handelstag gemeint ist. Der wird auf das Kalenderdatum zurueckgefuehrt, statt die ganze Datei
/// abzulehnen.
///
/// Zahlen werden mit <see cref="ImportNumber.ThreeDigitTail.Decimal"/> gelesen, nicht mit
/// <c>Grouping</c>: hier stehen Kurse und Stueckzahlen, und "12.500" heisst zwoelfeinhalb und nicht
/// zwoelftausendfuenfhundert. Bei den Kontoumsaetzen ist es genau andersherum.
/// </summary>
internal static class InvestmentImportFile
{
    public static List<Dictionary<string, string>> Parse(string fileName, byte[] bytes) =>
        Path.GetExtension(fileName).Equals(".csv", StringComparison.OrdinalIgnoreCase)
            ? ParseCsv(bytes)
            : ParseXlsx(bytes);

    /// <summary>Rateversuch fuer die Spaltenzuordnung, deutsch und englisch.</summary>
    public static InvestmentImportColumnMapping Suggest(IEnumerable<string> headers)
    {
        var all = headers.ToArray();
        string? Find(params string[] names) =>
            all.FirstOrDefault(header => names.Any(name => Normalize(header) == Normalize(name)));

        return new InvestmentImportColumnMapping(
            Find("trade date", "datum", "date", "handelsdatum") ?? "",
            Find("type", "typ", "transaction type", "art", "transaktion") ?? "",
            Find("settlement date", "valuta", "wertstellung"),
            Find("security", "wertpapier", "name", "security name"),
            Find("isin"), Find("wkn"), Find("ticker", "symbol"),
            Find("quantity", "stück", "stueck", "anzahl", "shares"),
            Find("price", "kurs"), Find("gross", "brutto", "gross amount"),
            Find("amount", "betrag", "net", "netto"), Find("currency", "währung", "waehrung"),
            Find("fees", "gebühren", "gebuehren", "fee"), Find("taxes", "steuern", "tax"),
            Find("withholding tax", "quellensteuer"),
            Find("asset class", "asset_class", "asset type", "assettype", "anlageklasse", "wertpapierart"),
            null,
            Find("id", "transaction id", "order id", "external id"));
    }

    public static DateOnly ParseDate(string? value) =>
        ParseOptionalDate(value) ?? throw new FormatException("Trade date is missing.");

    public static DateOnly? ParseOptionalDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();

        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial)
            && serial is > 20000 and < 100000)
            return DateOnly.FromDateTime(new DateTime(1899, 12, 30).AddDays(serial));

        var formats = new[] { "yyyy-MM-dd", "dd.MM.yyyy", "d.M.yyyy", "dd/MM/yyyy", "MM/dd/yyyy", "yyyy/MM/dd" };
        foreach (var format in formats)
            if (DateOnly.TryParseExact(text, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                return date;

        // Broker exports frequently contain a full ISO timestamp even when the logical field is a trade date
        // (for example Trade Republic's leading "datetime" column). Preserve the calendar date encoded by
        // the timestamp instead of rejecting the whole import.
        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal, out var timestamp))
            return DateOnly.FromDateTime(timestamp.Date);

        // Not the host's culture: that read a German 03.04.2026 as 4 March everywhere but de-DE.
        if (ImportDate.TryParse(text, allowExcelSerial: true) is { } shared) return shared;
        throw new FormatException($"Invalid date '{value}'.");
    }

    public static decimal? ParseOptionalAmount(string? value) =>
        ImportNumber.TryParse(value, ImportNumber.ThreeDigitTail.Decimal);

    public static decimal? AbsNullable(decimal? value) => value.HasValue ? Math.Abs(value.Value) : null;

    public static string ParseCurrency(string? value)
    {
        var currency = Clean(value)?.ToUpperInvariant();
        return currency is { Length: 3 } && currency.All(char.IsLetter) ? currency : "EUR";
    }

    public static string Normalize(string value) =>
        new(value.Trim().ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    public static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    public static string Sha256Bytes(byte[] value) =>
        Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    /// <summary>
    /// Das Trennzeichen wird nicht angenommen, sondern aus der Kopfzeile gezaehlt - Semikolon,
    /// Komma und Tabulator kommen alle vor, je nach Broker und Sprachraum.
    /// </summary>
    private static List<Dictionary<string, string>> ParseCsv(byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes).TrimStart('﻿');
        var records = SplitCsvRecords(text);
        if (records.Count < 2) return [];

        var delimiter = new[] { ';', ',', '\t' }
            .OrderByDescending(character => records[0].Count(value => value == character)).First();
        var header = ParseCsvLine(records[0], delimiter);

        return records.Skip(1).Where(line => !string.IsNullOrWhiteSpace(line)).Select(line =>
        {
            var cells = ParseCsvLine(line, delimiter);
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < header.Count; index++)
                row[header[index]] = index < cells.Count ? cells[index] : "";
            return row;
        }).ToList();
    }

    /// <summary>Ein Zeilenumbruch in Anfuehrungszeichen gehoert zum Wert und trennt keine Zeile.</summary>
    private static List<string> SplitCsvRecords(string text)
    {
        var rows = new List<string>();
        var builder = new StringBuilder();
        var quoted = false;

        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (character == '"')
            {
                if (quoted && index + 1 < text.Length && text[index + 1] == '"')
                {
                    builder.Append("\"\"");
                    index++;
                    continue;
                }
                quoted = !quoted;
                builder.Append(character);
            }
            else if (character is '\n' or '\r' && !quoted)
            {
                if (character == '\r' && index + 1 < text.Length && text[index + 1] == '\n') index++;
                rows.Add(builder.ToString());
                builder.Clear();
            }
            else builder.Append(character);
        }

        if (builder.Length > 0) rows.Add(builder.ToString());
        return rows;
    }

    private static List<string> ParseCsvLine(string line, char delimiter)
    {
        var cells = new List<string>();
        var builder = new StringBuilder();
        var quoted = false;

        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (character == '"')
            {
                if (quoted && index + 1 < line.Length && line[index + 1] == '"')
                {
                    builder.Append('"');
                    index++;
                }
                else quoted = !quoted;
            }
            else if (character == delimiter && !quoted)
            {
                cells.Add(builder.ToString());
                builder.Clear();
            }
            else builder.Append(character);
        }

        cells.Add(builder.ToString());
        return cells;
    }

    private static List<Dictionary<string, string>> ParseXlsx(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        var sharedStrings = ReadSharedStrings(zip);
        var sheet = zip.GetEntry("xl/worksheets/sheet1.xml")
            ?? throw new InvalidDataException("XLSX has no first worksheet.");

        using var sheetStream = sheet.Open();
        var document = XDocument.Load(sheetStream);
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

        var rows = document.Descendants(ns + "row").Select(row => ReadXlsxRow(row, ns, sharedStrings)).ToList();
        if (rows.Count < 2) return [];

        var header = rows[0];
        var result = new List<Dictionary<string, string>>();
        foreach (var cells in rows.Skip(1))
        {
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < header.Count; index++)
                row[header[index]] = index < cells.Count ? cells[index] : "";
            result.Add(row);
        }
        return result;
    }

    private static List<string> ReadSharedStrings(ZipArchive zip)
    {
        var entry = zip.GetEntry("xl/sharedStrings.xml");
        if (entry is null) return [];

        using var stream = entry.Open();
        var document = XDocument.Load(stream);
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        return document.Descendants(ns + "si")
            .Select(item => string.Concat(item.Descendants(ns + "t").Select(text => text.Value))).ToList();
    }

    /// <summary>
    /// Leere Zellen stehen in einer XLSX gar nicht in der Datei. Die Spalte kommt darum aus dem
    /// Zellbezug (A1, B1, ...) und nicht aus der Reihenfolge - sonst verrutscht eine Zeile mit Luecke.
    /// </summary>
    private static List<string> ReadXlsxRow(XElement row, XNamespace ns, IReadOnlyList<string> sharedStrings)
    {
        var values = new SortedDictionary<int, string>();
        foreach (var cell in row.Elements(ns + "c"))
        {
            var column = ColumnIndex((string?)cell.Attribute("r") ?? "A1");
            var type = (string?)cell.Attribute("t");
            var value = type == "inlineStr"
                ? string.Concat(cell.Descendants(ns + "t").Select(text => text.Value))
                : cell.Element(ns + "v")?.Value ?? "";
            if (type == "s" && int.TryParse(value, out var sharedIndex)
                && sharedIndex >= 0 && sharedIndex < sharedStrings.Count)
                value = sharedStrings[sharedIndex];
            values[column] = value;
        }

        var max = values.Count == 0 ? -1 : values.Keys.Max();
        return Enumerable.Range(0, max + 1).Select(index => values.GetValueOrDefault(index, "")).ToList();
    }

    private static int ColumnIndex(string reference)
    {
        var letters = new string(reference.TakeWhile(char.IsLetter).ToArray()).ToUpperInvariant();
        var value = 0;
        foreach (var character in letters) value = value * 26 + (character - 'A' + 1);
        return Math.Max(0, value - 1);
    }
}
