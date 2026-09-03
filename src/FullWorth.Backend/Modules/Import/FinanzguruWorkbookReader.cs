using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;

namespace FullWorth.Backend.Modules.Import;

public sealed record FinanzguruRow(
    int RowNumber,
    DateOnly BookingDate,
    string? ReferenceAccount,
    string? ReferenceAccountName,
    decimal Amount,
    string Currency,
    string? Counterparty,
    string? CounterpartyIban,
    string? Description,
    string? EntryReference,
    string? MainCategory,
    string? SubCategory,
    bool IsTransfer,
    string BookingId,
    string? OriginalReferenceId,
    string? SplitType,
    IReadOnlyDictionary<string, string?> RawValues);

public sealed class FinanzguruWorkbookException(string message) : Exception(message);

/// <summary>
/// Minimal, dependency-free reader for Finanzguru's "Alle Buchungen" .xlsx export. The format is
/// deliberately validated by header name, not column position, so harmless column reordering does not
/// corrupt money data. Only the first worksheet is read; workbook size and expanded XML are bounded.
/// </summary>
public sealed class FinanzguruWorkbookReader
{
    private const int MaxRows = 100_000;
    private const long MaxWorksheetBytes = 64L * 1024 * 1024;
    private const long MaxSharedStringsBytes = 32L * 1024 * 1024;
    private static readonly XNamespace SpreadsheetNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    private static readonly string[] RequiredHeaders =
    [
        "Buchungstag",
        "Betrag",
        "Waehrung",
        "Buchungs-ID",
        "Referenzkonto",
        "Name Referenzkonto",
        "Beguenstigter/Auftraggeber",
        "Verwendungszweck",
        "E-Ref",
        "Analyse-Hauptkategorie",
        "Analyse-Unterkategorie",
        "Analyse-Umbuchung",
        "Referenz-Original-ID",
        "Split-Typ"
    ];

    public IReadOnlyList<FinanzguruRow> Read(Stream input)
    {
        if (!input.CanRead) throw new FinanzguruWorkbookException("The uploaded workbook cannot be read.");

        try
        {
            using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
            var worksheetEntry = archive.Entries
                .Where(entry => entry.FullName.StartsWith("xl/worksheets/", StringComparison.OrdinalIgnoreCase)
                                && entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                .OrderBy(entry => entry.FullName, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault()
                ?? throw new FinanzguruWorkbookException("The uploaded file contains no Excel worksheet.");

            if (worksheetEntry.Length > MaxWorksheetBytes)
                throw new FinanzguruWorkbookException("The worksheet is too large to import.");

            var sharedStrings = ReadSharedStrings(archive);
            using var worksheetStream = worksheetEntry.Open();
            var document = XDocument.Load(worksheetStream, LoadOptions.None);
            var rows = document.Descendants(SpreadsheetNs + "row").Take(MaxRows + 2).ToList();
            if (rows.Count == 0) throw new FinanzguruWorkbookException("The workbook is empty.");
            if (rows.Count > MaxRows + 1) throw new FinanzguruWorkbookException($"The workbook exceeds the {MaxRows:N0}-row import limit.");

            var headerCells = ReadCells(rows[0], sharedStrings);
            var headers = headerCells
                .Where(cell => !string.IsNullOrWhiteSpace(cell.Value))
                .ToDictionary(cell => cell.Key, cell => cell.Value!.Trim());

            foreach (var required in RequiredHeaders)
            {
                if (!headers.Values.Contains(required, StringComparer.Ordinal))
                    throw new FinanzguruWorkbookException($"Missing Finanzguru column '{required}'.");
            }

            if (headers.Values.Count != headers.Values.Distinct(StringComparer.Ordinal).Count())
                throw new FinanzguruWorkbookException("The workbook contains duplicate column names.");

            var result = new List<FinanzguruRow>(Math.Max(0, rows.Count - 1));
            foreach (var rowElement in rows.Skip(1))
            {
                var rowNumber = ParseRowNumber(rowElement, result.Count + 2);
                var cells = ReadCells(rowElement, sharedStrings);
                if (cells.Count == 0) continue;

                string? Get(string header)
                {
                    var column = headers.Single(pair => pair.Value == header).Key;
                    return Normalize(cells.GetValueOrDefault(column));
                }

                var bookingId = Get("Buchungs-ID");
                var amountText = Get("Betrag");
                var dateText = Get("Buchungstag");
                var currency = (Get("Waehrung") ?? string.Empty).ToUpperInvariant();

                // Ignore completely blank trailing rows, but never silently drop a partially populated row.
                if (bookingId is null && amountText is null && dateText is null && string.IsNullOrWhiteSpace(currency))
                    continue;
                if (bookingId is null) throw RowError(rowNumber, "Buchungs-ID is empty.");
                if (!TryParseDecimal(amountText, out var amount)) throw RowError(rowNumber, "Betrag is invalid.");
                if (!TryParseDate(dateText, out var bookingDate)) throw RowError(rowNumber, "Buchungstag is invalid.");
                if (currency.Length != 3 || currency.Any(ch => ch is < 'A' or > 'Z')) throw RowError(rowNumber, "Waehrung must be a three-letter code.");

                var splitType = Get("Split-Typ");
                if (splitType is not null && splitType is not ("Original" or "Teilbuchung" or "Restbetrag"))
                    throw RowError(rowNumber, $"Unsupported Split-Typ '{splitType}'.");
                var originalReferenceId = Get("Referenz-Original-ID");
                if (splitType is "Teilbuchung" or "Restbetrag" && originalReferenceId is null)
                    throw RowError(rowNumber, "Split child has no Referenz-Original-ID.");

                var raw = headers.ToDictionary(
                    pair => pair.Value,
                    pair => Normalize(cells.GetValueOrDefault(pair.Key)),
                    StringComparer.Ordinal);

                result.Add(new FinanzguruRow(
                    rowNumber,
                    bookingDate,
                    Get("Referenzkonto"),
                    Get("Name Referenzkonto"),
                    amount,
                    currency,
                    Get("Beguenstigter/Auftraggeber"),
                    GetOptional(headers, cells, "IBAN Beguenstigter/Auftraggeber"),
                    Get("Verwendungszweck"),
                    Get("E-Ref"),
                    Get("Analyse-Hauptkategorie"),
                    Get("Analyse-Unterkategorie"),
                    string.Equals(Get("Analyse-Umbuchung"), "ja", StringComparison.OrdinalIgnoreCase),
                    bookingId,
                    originalReferenceId,
                    splitType,
                    raw));
            }

            if (result.Count == 0) throw new FinanzguruWorkbookException("The workbook contains no transactions.");
            return result;
        }
        catch (FinanzguruWorkbookException)
        {
            throw;
        }
        catch (InvalidDataException exception)
        {
            throw new FinanzguruWorkbookException($"The uploaded file is not a valid .xlsx workbook: {exception.Message}");
        }
        catch (System.Xml.XmlException exception)
        {
            throw new FinanzguruWorkbookException($"The workbook XML is invalid: {exception.Message}");
        }
    }

    private static List<string> ReadSharedStrings(ZipArchive archive)
    {
        var entry = archive.GetEntry("xl/sharedStrings.xml");
        if (entry is null) return [];
        if (entry.Length > MaxSharedStringsBytes) throw new FinanzguruWorkbookException("The shared-string table is too large to import.");
        using var stream = entry.Open();
        var document = XDocument.Load(stream, LoadOptions.None);
        return document.Descendants(SpreadsheetNs + "si")
            .Select(item => string.Concat(item.Descendants(SpreadsheetNs + "t").Select(text => text.Value)))
            .ToList();
    }

    private static Dictionary<int, string?> ReadCells(XElement row, IReadOnlyList<string> sharedStrings)
    {
        var result = new Dictionary<int, string?>();
        foreach (var cell in row.Elements(SpreadsheetNs + "c"))
        {
            var reference = (string?)cell.Attribute("r");
            if (string.IsNullOrWhiteSpace(reference)) continue;
            var column = ColumnNumber(reference);
            var type = (string?)cell.Attribute("t");
            string? value;
            if (type == "inlineStr")
            {
                value = string.Concat(cell.Descendants(SpreadsheetNs + "t").Select(text => text.Value));
            }
            else
            {
                value = cell.Element(SpreadsheetNs + "v")?.Value;
                if (type == "s" && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
                {
                    if (index < 0 || index >= sharedStrings.Count) throw new FinanzguruWorkbookException("The workbook contains an invalid shared-string reference.");
                    value = sharedStrings[index];
                }
            }
            result[column] = value;
        }
        return result;
    }

    private static string? GetOptional(IReadOnlyDictionary<int, string> headers, IReadOnlyDictionary<int, string?> cells, string header)
    {
        var pair = headers.FirstOrDefault(candidate => candidate.Value == header);
        return pair.Value is null ? null : Normalize(cells.GetValueOrDefault(pair.Key));
    }

    private static int ColumnNumber(string cellReference)
    {
        var column = 0;
        foreach (var ch in cellReference)
        {
            if (ch is not (>= 'A' and <= 'Z') and not (>= 'a' and <= 'z')) break;
            column = checked(column * 26 + (char.ToUpperInvariant(ch) - 'A' + 1));
        }
        return column;
    }

    private static int ParseRowNumber(XElement row, int fallback) =>
        int.TryParse((string?)row.Attribute("r"), NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    private static bool TryParseDecimal(string? value, out decimal amount) =>
        decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out amount)
        || decimal.TryParse(value, NumberStyles.Number, CultureInfo.GetCultureInfo("de-DE"), out amount);

    private static bool TryParseDate(string? value, out DateOnly date)
    {
        date = default;
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial))
        {
            try
            {
                date = DateOnly.FromDateTime(DateTime.FromOADate(serial));
                return true;
            }
            catch (ArgumentException) { return false; }
        }

        foreach (var culture in new[] { CultureInfo.GetCultureInfo("de-DE"), CultureInfo.InvariantCulture })
        {
            if (DateTime.TryParse(value, culture, DateTimeStyles.AllowWhiteSpaces, out var parsed))
            {
                date = DateOnly.FromDateTime(parsed);
                return true;
            }
        }
        return false;
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static FinanzguruWorkbookException RowError(int row, string message) => new($"Row {row}: {message}");
}
