using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace FullWorth.Backend.Modules.Import;

/// <summary>
/// Der eine Leser fuer CSV- und XLSX-Importdateien (#131).
///
/// Es gab zwei, in <c>ImportJobEndpoints</c> und in <c>ImportMappingEndpoints</c>, und sie sahen gleich
/// aus. Waren sie aber nicht - dieselbe Datei kam auf den beiden Wegen unterschiedlich an:
///
/// <list type="bullet">
///   <item>Eine CSV mit UTF-8-BOM hiess auf dem einen Weg <c>Datum</c> und auf dem anderen
///         <c>﻿Datum</c>. Die Spaltenerkennung fand die erste Spalte dann nicht.</item>
///   <item>Ein doppeltes Anfuehrungszeichen IN einem Feld (<c>"Er sagte ""Hallo"""</c>) hat auf dem
///         einen Weg den Zitatzustand umgedreht - ein Zeilenumbruch im selben Feld hat den Datensatz
///         danach mitten im Text zerschnitten.</item>
///   <item>Ein XLSX-Textfeld aus mehreren Formatlaeufen (<c>inlineStr</c> mit mehreren
///         <c>&lt;t&gt;</c>) verlor auf dem einen Weg alles nach dem ersten Lauf.</item>
///   <item>Ein Excel-Datum ist eine Tageszahl. Der eine Weg las sie, der andere nicht - dieselbe
///         Arbeitsmappe importierte einmal sauber und einmal mit lauter Datumsfehlern.</item>
/// </list>
///
/// Jede dieser Abweichungen ist genau das, was #131 meint: Importe verhalten sich an verschiedenen
/// Stellen unterschiedlich. Hier steht deshalb jeweils die bessere der beiden Fassungen, einmal.
/// </summary>
internal static class ImportTabularFile
{
    private static readonly XNamespace SpreadsheetNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    /// <summary>Die Endungen, die dieser Leser beherrscht.</summary>
    internal static readonly string[] Extensions = [".csv", ".xlsx"];

    internal static bool CouldBeTable(string extension) =>
        Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Zeilen als Spaltenname -&gt; Wert. Die erste Zeile der Datei ist die Kopfzeile und steht nicht in
    /// der Ergebnisliste. Leer, wenn die Datei ausser der Kopfzeile nichts enthaelt.
    /// </summary>
    internal static List<Dictionary<string, string>> Read(string fileName, byte[] bytes) =>
        Path.GetExtension(fileName).Equals(".csv", StringComparison.OrdinalIgnoreCase)
            ? ReadCsv(bytes)
            : ReadXlsx(bytes);

    /// <summary>
    /// Welche Spalte welches Feld ist, geraten aus den Kopfzeilennamen. Ein <c>null</c> heisst "nicht
    /// gefunden" - der Aufrufer entscheidet, ob das ein Fehler ist (Datum und Betrag) oder nur eine
    /// fehlende Zusatzinformation.
    /// </summary>
    internal static ImportColumnMapping SuggestColumns(IEnumerable<string> headers)
    {
        var all = headers.ToArray();
        string? Find(params string[] names) => all.FirstOrDefault(header => names.Any(name => Norm(header) == Norm(name)));
        return new ImportColumnMapping(
            Find("date", "datum", "booking date", "buchungsdatum", "buchungstag") ?? "",
            Find("amount", "betrag", "value", "umsatz") ?? "",
            Find("currency", "währung", "waehrung"),
            Find("counterparty", "empfänger", "empfaenger", "payee", "merchant", "gegenpartei"),
            Find("description", "verwendungszweck", "text", "purpose", "memo"),
            Find("account", "konto", "account name", "referenzkonto"),
            Find("category", "kategorie"),
            Find("id", "booking id", "transaction id", "buchungs-id"));
    }

    private static string Norm(string value) => new(value.Trim().ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    private static List<Dictionary<string, string>> ReadCsv(byte[] bytes)
    {
        // Das BOM gehoert zur Kodierung, nicht zum ersten Spaltennamen.
        var text = Encoding.UTF8.GetString(bytes).TrimStart('﻿');
        var records = SplitRecords(text);
        if (records.Count < 2) return [];
        var delimiter = GuessDelimiter(records[0]);
        var header = SplitCells(records[0], delimiter);
        return records.Skip(1)
            .Where(record => !string.IsNullOrWhiteSpace(record))
            .Select(record => ToRow(header, SplitCells(record, delimiter)))
            .ToList();
    }

    private static Dictionary<string, string> ToRow(List<string> header, List<string> cells)
    {
        var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < header.Count; index++) row[header[index]] = index < cells.Count ? cells[index] : "";
        return row;
    }

    private static char GuessDelimiter(string line) =>
        new[] { ';', ',', '\t' }.OrderByDescending(candidate => line.Count(character => character == candidate)).First();

    /// <summary>
    /// Zerlegt in Datensaetze, nicht in Zeilen: ein Zeilenumbruch innerhalb eines zitierten Feldes
    /// gehoert zum Wert. Ein doppeltes Anfuehrungszeichen ist ein Anfuehrungszeichen im Wert und darf
    /// den Zitatzustand NICHT umdrehen - sonst zerfaellt der Datensatz genau dort.
    /// </summary>
    private static List<string> SplitRecords(string text)
    {
        var records = new List<string>();
        var builder = new StringBuilder();
        var quoted = false;
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (character == '"')
            {
                if (quoted && index + 1 < text.Length && text[index + 1] == '"') { builder.Append("\"\""); index++; continue; }
                quoted = !quoted;
                builder.Append(character);
            }
            else if ((character == '\n' || character == '\r') && !quoted)
            {
                if (character == '\r' && index + 1 < text.Length && text[index + 1] == '\n') index++;
                records.Add(builder.ToString());
                builder.Clear();
            }
            else builder.Append(character);
        }
        if (builder.Length > 0) records.Add(builder.ToString());
        return records;
    }

    private static List<string> SplitCells(string record, char delimiter)
    {
        var cells = new List<string>();
        var builder = new StringBuilder();
        var quoted = false;
        for (var index = 0; index < record.Length; index++)
        {
            var character = record[index];
            if (character == '"')
            {
                if (quoted && index + 1 < record.Length && record[index + 1] == '"') { builder.Append('"'); index++; }
                else quoted = !quoted;
            }
            else if (character == delimiter && !quoted) { cells.Add(builder.ToString().Trim()); builder.Clear(); }
            else builder.Append(character);
        }
        cells.Add(builder.ToString().Trim());
        return cells;
    }

    private static List<Dictionary<string, string>> ReadXlsx(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var shared = ReadSharedStrings(archive);
        var sheet = archive.GetEntry("xl/worksheets/sheet1.xml")
            ?? throw new InvalidDataException("Workbook has no first worksheet.");
        using var sheetStream = sheet.Open();
        var document = XDocument.Load(sheetStream);
        var rows = document.Descendants(SpreadsheetNs + "row").Select(row => ReadRow(row, shared)).ToList();
        if (rows.Count < 2) return [];
        var header = rows[0];
        return rows.Skip(1).Select(cells => ToRow(header, cells)).ToList();
    }

    private static List<string> ReadSharedStrings(ZipArchive archive)
    {
        var entry = archive.GetEntry("xl/sharedStrings.xml");
        if (entry is null) return [];
        using var stream = entry.Open();
        return XDocument.Load(stream).Descendants(SpreadsheetNs + "si")
            .Select(item => string.Concat(item.Descendants(SpreadsheetNs + "t").Select(text => text.Value)))
            .ToList();
    }

    private static List<string> ReadRow(XElement row, IReadOnlyList<string> shared)
    {
        var values = new SortedDictionary<int, string>();
        foreach (var cell in row.Elements(SpreadsheetNs + "c"))
        {
            var reference = (string?)cell.Attribute("r") ?? "A1";
            var type = (string?)cell.Attribute("t");
            // Ein Textfeld kann aus mehreren Formatlaeufen bestehen - alle gehoeren zum Wert.
            var value = type == "inlineStr"
                ? string.Concat(cell.Descendants(SpreadsheetNs + "t").Select(text => text.Value))
                : cell.Element(SpreadsheetNs + "v")?.Value ?? "";
            if (type == "s" && int.TryParse(value, out var index) && index >= 0 && index < shared.Count) value = shared[index];
            values[ColumnIndex(reference)] = value;
        }
        var last = values.Count == 0 ? -1 : values.Keys.Max();
        return Enumerable.Range(0, last + 1).Select(index => values.GetValueOrDefault(index, "")).ToList();
    }

    private static int ColumnIndex(string reference)
    {
        var letters = new string(reference.TakeWhile(char.IsLetter).ToArray()).ToUpperInvariant();
        var index = 0;
        foreach (var letter in letters) index = index * 26 + (letter - 'A' + 1);
        return Math.Max(0, index - 1);
    }
}
