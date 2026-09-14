using System.Security;
using System.IO.Compression;
using System.Text;

namespace FullWorth.Backend.Modules.Export;

/// <summary>
/// Schreibt eine Mappe im xlsx-Format: je Blatt eine Liste von Zeilen, jede Zelle ein Text.
///
/// Von Finanzen weiss diese Klasse nichts, und das ist der Punkt - sie lag bis 2026-09-15 in
/// derselben Datei wie vier Datenbankabfragen und der Aufbau der Buchungszeilen.
/// </summary>
public static class XlsxWriter
{
    public static byte[] Write(IReadOnlyDictionary<string, List<IReadOnlyList<string>>> sheets)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            Add(zip, "[Content_Types].xml", ContentTypes(sheets.Count));
            Add(zip, "_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
            Add(zip, "xl/workbook.xml", Workbook(sheets.Keys));
            Add(zip, "xl/_rels/workbook.xml.rels", WorkbookRelationships(sheets.Count));
            var index = 1;
            foreach (var sheet in sheets)
                Add(zip, $"xl/worksheets/sheet{index++}.xml", SheetXml(sheet.Value));
        }
        return stream.ToArray();
    }

    private static void Add(ZipArchive zip, string path, string content)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Fastest);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static string ContentTypes(int count) =>
        $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>{string.Concat(Enumerable.Range(1, count).Select(i => $"<Override PartName=\"/xl/worksheets/sheet{i}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>"))}</Types>";

    private static string Workbook(IEnumerable<string> names) =>
        $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>{string.Concat(names.Select((name, index) => $"<sheet name=\"{Xml(name)}\" sheetId=\"{index + 1}\" r:id=\"rId{index + 1}\"/>"))}</sheets></workbook>";

    private static string WorkbookRelationships(int count) =>
        $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">{string.Concat(Enumerable.Range(1, count).Select(i => $"<Relationship Id=\"rId{i}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{i}.xml\"/>"))}</Relationships>";

    private static string SheetXml(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var builder = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");
        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            builder.Append($"<row r=\"{rowIndex + 1}\">");
            for (var columnIndex = 0; columnIndex < rows[rowIndex].Count; columnIndex++)
            {
                var cell = Column(columnIndex) + (rowIndex + 1);
                builder.Append($"<c r=\"{cell}\" t=\"inlineStr\"><is><t>{Xml(rows[rowIndex][columnIndex])}</t></is></c>");
            }
            builder.Append("</row>");
        }
        return builder.Append("</sheetData></worksheet>").ToString();
    }

    private static string Column(int index)
    {
        var result = string.Empty;
        for (index++; index > 0; index = (index - 1) / 26)
            result = (char)('A' + (index - 1) % 26) + result;
        return result;
    }

    private static string Xml(string? value) => SecurityElement.Escape(value ?? string.Empty) ?? string.Empty;
}
