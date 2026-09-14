using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;
using FullWorth.Backend.Data;
using FullWorth.Backend.Security;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Export;

/// <summary>Der Tabellenexport. Kam aus Parity/ExperienceParityModule.</summary>
public static class XlsxExportEndpoints
{
    public static IEndpointRouteBuilder MapXlsxExportEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/export/xlsx", ExportXlsx).WithTags("Export");
        return app;
    }

    private static async Task<IResult> ExportXlsx(
        Guid fullWorthSpaceId, CurrentUserContext currentUser, FullWorthDbContext db, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await RawSql.IsMemberAsync(db, userId, fullWorthSpaceId, ct)) return Results.NotFound();
        var visibleAccounts = await RawSql.VisibleAccountIdsAsync(db, userId, fullWorthSpaceId, ct);

        var transactions = await db.Transactions.AsNoTracking()
            .Where(transaction => visibleAccounts.Contains(transaction.AccountId))
            .OrderByDescending(transaction => transaction.BookingDate)
            .Take(100000)
            .ToListAsync(ct);
        var categories = await db.Categories.AsNoTracking()
            .Where(category => category.FullWorthSpaceId == fullWorthSpaceId)
            .ToListAsync(ct);
        var contracts = await db.Contracts.AsNoTracking()
            .Where(contract => contract.FullWorthSpaceId == fullWorthSpaceId &&
                contract.MergedIntoContractId == null &&
                (contract.AccountId == null || visibleAccounts.Contains(contract.AccountId.Value)))
            .ToListAsync(ct);
        var accounts = await db.Accounts.AsNoTracking()
            .Where(account => visibleAccounts.Contains(account.Id))
            .ToDictionaryAsync(account => account.Id, account => account.DisplayName, ct);
        var categoryNames = categories.ToDictionary(category => category.Id, category => category.Name);

        var transactionRows = new List<IReadOnlyList<string>>
        {
            new string[] { "Date", "Account", "Amount", "Currency", "Counterparty", "Description", "Category", "Transfer", "Ignored" }
        };
        foreach (var transaction in transactions)
        {
            transactionRows.Add(new string[]
            {
                (transaction.BookingDate ?? transaction.ValueDate)?.ToString("yyyy-MM-dd") ?? string.Empty,
                accounts.GetValueOrDefault(transaction.AccountId, string.Empty),
                transaction.Amount.ToString(CultureInfo.InvariantCulture),
                transaction.Currency,
                transaction.Counterparty ?? string.Empty,
                transaction.Description ?? string.Empty,
                transaction.CategoryId.HasValue ? categoryNames.GetValueOrDefault(transaction.CategoryId.Value, string.Empty) : string.Empty,
                transaction.IsTransfer ? "true" : "false",
                transaction.IsIgnored ? "true" : "false"
            });
        }

        var categoryRows = new List<IReadOnlyList<string>>
        {
            new string[] { "Key", "Name", "ParentId", "Archived" }
        };
        foreach (var category in categories)
            categoryRows.Add(new string[] { category.Key, category.Name, category.ParentId?.ToString() ?? string.Empty, category.IsArchived ? "true" : "false" });

        var contractRows = new List<IReadOnlyList<string>>
        {
            new string[] { "Name", "Provider", "Amount", "Currency", "Cycle", "NextDue", "Active" }
        };
        foreach (var contract in contracts)
            contractRows.Add(new string[]
            {
                contract.Name, contract.ProviderName ?? string.Empty,
                contract.Amount.ToString(CultureInfo.InvariantCulture), contract.Currency,
                contract.BillingCycle, contract.NextDueDate?.ToString("yyyy-MM-dd") ?? string.Empty,
                contract.IsActive ? "true" : "false"
            });

        var bytes = BuildXlsx(new Dictionary<string, List<IReadOnlyList<string>>>
        {
            ["Transactions"] = transactionRows,
            ["Categories"] = categoryRows,
            ["Contracts"] = contractRows
        });
        return Results.File(bytes,
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"fullworth-{DateTime.UtcNow:yyyyMMdd}.xlsx");
    }

    private static byte[] BuildXlsx(Dictionary<string, List<IReadOnlyList<string>>> sheets)
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
