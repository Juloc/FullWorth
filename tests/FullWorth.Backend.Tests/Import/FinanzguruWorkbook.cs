using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace FullWorth.Backend.Tests.Import;

/// <summary>
/// Eine Finanzguru-Exportdatei, wie der Reader sie erwartet.
///
/// Die Tabelle stand vorher als privates Zubehoer in <c>FinanzguruImportTests</c>. Mit dem
/// Zwischenschritt (#131) braucht sie ein zweiter Test, und zwei Fassungen derselben Kopfzeile waeren
/// zwei Stellen, an denen eine neue Spalte nachgetragen werden muesste - eine davon wuerde vergessen.
/// </summary>
internal static class FinanzguruWorkbook
{
    private static readonly string[] Headers =
    [
        "Buchungstag", "Referenzkonto", "Name Referenzkonto", "Betrag", "Waehrung",
        "Beguenstigter/Auftraggeber", "Verwendungszweck", "E-Ref",
        "Analyse-Hauptkategorie", "Analyse-Unterkategorie", "Analyse-Umbuchung",
        "Buchungs-ID", "Referenz-Original-ID", "Split-Typ"
    ];

    internal static Dictionary<string, string?> Row(
        string date,
        decimal amount,
        string counterparty,
        string description,
        string mainCategory,
        string subCategory,
        string bookingId,
        string? originalId = null,
        string? splitType = null,
        bool isTransfer = false,
        string reference = "DE65500105175456601426",
        string referenceName = "Girokonto") => new(StringComparer.Ordinal)
    {
        ["Buchungstag"] = date,
        ["Referenzkonto"] = reference,
        ["Name Referenzkonto"] = referenceName,
        ["Betrag"] = amount.ToString(CultureInfo.InvariantCulture),
        ["Waehrung"] = "EUR",
        ["Beguenstigter/Auftraggeber"] = counterparty,
        ["Verwendungszweck"] = description,
        ["E-Ref"] = null,
        ["Analyse-Hauptkategorie"] = mainCategory,
        ["Analyse-Unterkategorie"] = subCategory,
        ["Analyse-Umbuchung"] = isTransfer ? "ja" : "nein",
        ["Buchungs-ID"] = bookingId,
        ["Referenz-Original-ID"] = originalId,
        ["Split-Typ"] = splitType
    };

    internal static byte[] Create(params Dictionary<string, string?>[] dataRows)
    {
        var spreadsheet = (XNamespace)"http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var rowElements = new List<XElement>
        {
            BuildRow(spreadsheet, 1, Headers.ToDictionary(header => header, header => (string?)header, StringComparer.Ordinal))
        };
        for (var index = 0; index < dataRows.Length; index++)
            rowElements.Add(BuildRow(spreadsheet, index + 2, dataRows[index]));

        var document = new XDocument(
            new XElement(spreadsheet + "worksheet",
                new XElement(spreadsheet + "sheetData", rowElements)));

        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("xl/worksheets/sheet1.xml");
            using var stream = entry.Open();
            document.Save(stream);
        }
        return output.ToArray();
    }

    private static XElement BuildRow(XNamespace ns, int rowNumber, IReadOnlyDictionary<string, string?> values)
    {
        var cells = new List<XElement>();
        for (var index = 0; index < Headers.Length; index++)
        {
            var value = values.GetValueOrDefault(Headers[index]);
            if (value is null) continue;
            cells.Add(new XElement(ns + "c",
                new XAttribute("r", $"{ColumnName(index + 1)}{rowNumber}"),
                new XAttribute("t", "inlineStr"),
                new XElement(ns + "is", new XElement(ns + "t", value))));
        }
        return new XElement(ns + "row", new XAttribute("r", rowNumber), cells);
    }

    private static string ColumnName(int column)
    {
        var builder = new StringBuilder();
        while (column > 0)
        {
            column--;
            builder.Insert(0, (char)('A' + column % 26));
            column /= 26;
        }
        return builder.ToString();
    }
}
