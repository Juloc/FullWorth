using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml;
using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Accounts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FullWorth.Backend.Modules.Purchases;

public sealed record PurchaseExportFile(byte[] Bytes, string ContentType, string FileName);

public sealed class PurchaseExportService(FullWorthDbContext db, IOptions<PurchaseStorageOptions> storageOptions)
{
    private readonly PurchaseStorageOptions storage = storageOptions.Value;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public async Task<PurchaseExportFile?> ExportAsync(Guid userId, Guid fullWorthSpaceId, string format, bool includeDocuments, CancellationToken ct)
    {
        if (!await db.FullWorthSpaceMembers.AsNoTracking().AnyAsync(x => x.UserId == userId && x.FullWorthSpaceId == fullWorthSpaceId, ct)) return null;
        var purchases = await Visible(userId, fullWorthSpaceId)
            .Include(x => x.Items.OrderBy(i => i.SortOrder))
            .Include(x => x.Discounts.OrderBy(d => d.CreatedAt))
            .Include(x => x.PaymentLinks)
            .Include(x => x.Documents)
            .Include(x => x.Tags).ThenInclude(x => x.Tag)
            .OrderBy(x => x.PurchaseDate).ThenBy(x => x.CreatedAt).ToListAsync(ct);
        var rows = Flatten(purchases);
        var discountRows = FlattenDiscounts(purchases);
        var normalized = (format ?? "json").Trim().ToLowerInvariant();
        return normalized switch
        {
            "csv" => new PurchaseExportFile(Encoding.UTF8.GetBytes(ToCsv(rows)), "text/csv; charset=utf-8", "fullworth-purchases.csv"),
            "xlsx" => new PurchaseExportFile(ToXlsx(rows, discountRows), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "fullworth-purchases.xlsx"),
            "zip" => new PurchaseExportFile(await ToZipAsync(purchases, rows, discountRows, includeDocuments, ct), "application/zip", "fullworth-purchases-export.zip"),
            _ => new PurchaseExportFile(JsonSerializer.SerializeToUtf8Bytes(ToJsonModel(purchases), JsonOptions), "application/json", "fullworth-purchases.json")
        };
    }

    private static object ToJsonModel(IReadOnlyList<Purchase> purchases) => new
    {
        schema = "fullworth-purchases-v2",
        exportedAt = DateTimeOffset.UtcNow,
        purchases = purchases.Select(p => new
        {
            p.Id, p.FullWorthSpaceId, p.Source, p.Merchant, p.MerchantId, p.MerchantRaw, p.ExternalOrderId, p.PurchaseDate, p.PurchaseTime,
            p.SubtotalAmount, p.DiscountAmount, p.DepositAmount, p.RoundingAmount, p.TaxAmount, p.TipAmount, p.ShippingAmount, p.FeeAmount, p.TotalAmount, p.Currency,
            p.Status, p.ReviewState, p.ReceiptNumber, p.InvoiceNumber, p.PaymentMethodText, p.SourceReference, p.Notes, p.IsBookmarked, p.Visibility,
            items = p.Items.Select(i => new
            {
                i.Id, i.ProductId, i.CategoryId, i.RawName, i.Name, i.Brand, i.Sku, i.Barcode, i.Asin,
                i.Quantity, i.QuantityUnit, i.PackageQuantity, i.PackageUnit, i.PackageCount,
                i.UnitPrice, i.OriginalUnitPrice, i.BaseUnitPrice, i.TotalPrice, i.DiscountAmount, i.DiscountLabel, i.DepositAmount,
                i.TaxRate, i.TaxAmount, i.Currency, i.LineType, i.CategorizationSource, i.ExtractionConfidence,
                i.IsManuallyCorrected, i.TotalPriceOverridden, i.Notes, i.SortOrder, i.ReturnDeadline, i.WarrantyEnd, i.SerialNumber
            }),
            discounts = p.Discounts.Select(d => new
            {
                d.Id, d.PurchaseItemId, d.Type, d.Label, d.Amount, d.Percentage, d.CouponCode,
                d.RawText, d.Source, d.Confidence, d.CreatedAt, d.UpdatedAt
            }),
            payments = p.PaymentLinks.Select(x => new { x.Id, x.TransactionId, x.Amount, x.Currency, x.LinkSource, x.Confidence }),
            documents = p.Documents.Select(x => new { x.Id, x.DocumentType, x.OriginalFileName, x.MediaType, x.Sha256, x.SizeBytes, x.Status, x.CreatedAt }),
            tags = p.Tags.Select(x => new { x.Tag.Id, x.Tag.Name })
        })
    };

    private sealed record FlatRow(
        Guid PurchaseId, DateOnly? Date, TimeOnly? Time, string Merchant,
        decimal? PurchaseSubtotal, decimal? PurchaseDiscountTotal, decimal? PurchaseDepositTotal, decimal PurchaseRounding,
        decimal? PurchaseTax, decimal? PurchaseTip, decimal? PurchaseShipping, decimal? PurchaseFee,
        decimal PurchaseTotal, string Currency, string Source, string ReviewState,
        Guid? ItemId, Guid? ProductId, Guid? CategoryId, string? LineType, string? ItemName, string? Brand, string? Barcode,
        decimal? Quantity, string? QuantityUnit, decimal? PackageCount, decimal? PackageQuantity, string? PackageUnit,
        decimal? UnitPrice, decimal? OriginalUnitPrice, decimal? BaseUnitPrice, decimal? ItemDiscountAmount, string? ItemDiscountLabel,
        decimal? ItemDepositAmount, decimal? ItemTotal, DateOnly? ReturnDeadline, DateOnly? WarrantyEnd, string? SerialNumber,
        string DiscountsJson, string Tags);

    private sealed record FlatDiscountRow(
        Guid PurchaseId, DateOnly? Date, string Merchant, string Currency,
        Guid DiscountId, Guid? PurchaseItemId, string Type, string Label, decimal Amount, decimal? Percentage,
        string? CouponCode, string Source, decimal? Confidence, string? RawText);

    private static List<FlatRow> Flatten(IReadOnlyList<Purchase> purchases)
    {
        var rows = new List<FlatRow>();
        foreach (var p in purchases)
        {
            var tags = string.Join("; ", p.Tags.Select(x => x.Tag.Name).OrderBy(x => x));
            var discountsJson = JsonSerializer.Serialize(p.Discounts.OrderBy(x => x.CreatedAt).Select(x => new
            {
                x.Id, x.PurchaseItemId, x.Type, x.Label, x.Amount, x.Percentage, x.CouponCode, x.Source, x.Confidence
            }));
            if (p.Items.Count == 0)
            {
                rows.Add(new(
                    p.Id, p.PurchaseDate, p.PurchaseTime, p.Merchant,
                    p.SubtotalAmount, p.DiscountAmount, p.DepositAmount, p.RoundingAmount,
                    p.TaxAmount, p.TipAmount, p.ShippingAmount, p.FeeAmount,
                    p.TotalAmount, p.Currency, p.Source, p.ReviewState,
                    null, null, null, null, null, null, null,
                    null, null, null, null, null,
                    null, null, null, null, null,
                    null, null, null, null, null,
                    discountsJson, tags));
                continue;
            }
            foreach (var i in p.Items.OrderBy(x => x.SortOrder))
                rows.Add(new(
                    p.Id, p.PurchaseDate, p.PurchaseTime, p.Merchant,
                    p.SubtotalAmount, p.DiscountAmount, p.DepositAmount, p.RoundingAmount,
                    p.TaxAmount, p.TipAmount, p.ShippingAmount, p.FeeAmount,
                    p.TotalAmount, p.Currency, p.Source, p.ReviewState,
                    i.Id, i.ProductId, i.CategoryId, i.LineType, i.Name, i.Brand, i.Barcode,
                    i.Quantity, i.QuantityUnit, i.PackageCount, i.PackageQuantity, i.PackageUnit,
                    i.UnitPrice, i.OriginalUnitPrice, i.BaseUnitPrice, i.DiscountAmount, i.DiscountLabel,
                    i.DepositAmount, i.TotalPrice, i.ReturnDeadline, i.WarrantyEnd, i.SerialNumber,
                    discountsJson, tags));
        }
        return rows;
    }

    private static List<FlatDiscountRow> FlattenDiscounts(IReadOnlyList<Purchase> purchases) => purchases
        .SelectMany(p => p.Discounts.OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).Select(d => new FlatDiscountRow(
            p.Id, p.PurchaseDate, p.Merchant, p.Currency,
            d.Id, d.PurchaseItemId, d.Type, d.Label, d.Amount, d.Percentage,
            d.CouponCode, d.Source, d.Confidence, d.RawText)))
        .ToList();

    private static readonly string[] Headers =
    [
        "PurchaseId", "Date", "Time", "Merchant",
        "PurchaseSubtotal", "PurchaseDiscountTotal", "PurchaseDepositTotal", "PurchaseRounding", "PurchaseTax", "PurchaseTip", "PurchaseShipping", "PurchaseFee",
        "PurchaseTotal", "Currency", "Source", "ReviewState",
        "ItemId", "ProductId", "CategoryId", "LineType", "ItemName", "Brand", "Barcode", "Quantity", "QuantityUnit",
        "PackageCount", "PackageQuantity", "PackageUnit", "UnitPrice", "OriginalUnitPrice", "BaseUnitPrice",
        "ItemDiscountAmount", "ItemDiscountLabel", "ItemDepositAmount", "ItemTotal",
        "ReturnDeadline", "WarrantyEnd", "SerialNumber", "DiscountsJson", "Tags"
    ];

    private static readonly string[] DiscountHeaders =
    [
        "PurchaseId", "Date", "Merchant", "Currency", "DiscountId", "PurchaseItemId", "Type", "Label",
        "Amount", "Percentage", "CouponCode", "Source", "Confidence", "RawText"
    ];

    private static string ToCsv(IReadOnlyList<FlatRow> rows)
    {
        var sb = new StringBuilder(); sb.AppendLine(string.Join(',', Headers.Select(Csv)));
        foreach (var r in rows) sb.AppendLine(string.Join(',', Values(r).Select(Csv)));
        return sb.ToString();
    }

    private static string ToDiscountCsv(IReadOnlyList<FlatDiscountRow> rows)
    {
        var sb = new StringBuilder(); sb.AppendLine(string.Join(',', DiscountHeaders.Select(Csv)));
        foreach (var r in rows) sb.AppendLine(string.Join(',', DiscountValues(r).Select(Csv)));
        return sb.ToString();
    }

    private static IEnumerable<string?> Values(FlatRow r)
    {
        yield return r.PurchaseId.ToString(); yield return r.Date?.ToString("yyyy-MM-dd"); yield return r.Time?.ToString("HH:mm:ss"); yield return r.Merchant;
        yield return Num(r.PurchaseSubtotal); yield return Num(r.PurchaseDiscountTotal); yield return Num(r.PurchaseDepositTotal); yield return Num(r.PurchaseRounding);
        yield return Num(r.PurchaseTax); yield return Num(r.PurchaseTip); yield return Num(r.PurchaseShipping); yield return Num(r.PurchaseFee);
        yield return Num(r.PurchaseTotal); yield return r.Currency; yield return r.Source; yield return r.ReviewState;
        yield return r.ItemId?.ToString(); yield return r.ProductId?.ToString(); yield return r.CategoryId?.ToString(); yield return r.LineType; yield return r.ItemName; yield return r.Brand; yield return r.Barcode;
        yield return Num(r.Quantity); yield return r.QuantityUnit; yield return Num(r.PackageCount); yield return Num(r.PackageQuantity); yield return r.PackageUnit;
        yield return Num(r.UnitPrice); yield return Num(r.OriginalUnitPrice); yield return Num(r.BaseUnitPrice); yield return Num(r.ItemDiscountAmount); yield return r.ItemDiscountLabel;
        yield return Num(r.ItemDepositAmount); yield return Num(r.ItemTotal); yield return r.ReturnDeadline?.ToString("yyyy-MM-dd"); yield return r.WarrantyEnd?.ToString("yyyy-MM-dd"); yield return r.SerialNumber;
        yield return r.DiscountsJson; yield return r.Tags;
    }

    private static IEnumerable<string?> DiscountValues(FlatDiscountRow r)
    {
        yield return r.PurchaseId.ToString(); yield return r.Date?.ToString("yyyy-MM-dd"); yield return r.Merchant; yield return r.Currency;
        yield return r.DiscountId.ToString(); yield return r.PurchaseItemId?.ToString(); yield return r.Type; yield return r.Label;
        yield return Num(r.Amount); yield return Num(r.Percentage); yield return r.CouponCode; yield return r.Source; yield return Num(r.Confidence); yield return r.RawText;
    }

    private static string Csv(string? value)
    {
        value ??= string.Empty;
        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
    }
    private static string? Num(decimal? value) => value?.ToString("0.################", CultureInfo.InvariantCulture);

    private static byte[] ToXlsx(IReadOnlyList<FlatRow> rows, IReadOnlyList<FlatDiscountRow> discountRows)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
        {
            WriteEntry(zip, "[Content_Types].xml", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/><Override PartName=\"/xl/worksheets/sheet2.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/><Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/></Types>");
            WriteEntry(zip, "_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
            WriteEntry(zip, "xl/workbook.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"Purchases\" sheetId=\"1\" r:id=\"rId1\"/><sheet name=\"Discounts\" sheetId=\"2\" r:id=\"rId2\"/></sheets></workbook>");
            WriteEntry(zip, "xl/_rels/workbook.xml.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/><Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet2.xml\"/><Relationship Id=\"rId3\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/></Relationships>");
            WriteEntry(zip, "xl/styles.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><fonts count=\"1\"><font><sz val=\"11\"/><name val=\"Aptos\"/></font></fonts><fills count=\"1\"><fill><patternFill patternType=\"none\"/></fill></fills><borders count=\"1\"><border/></borders><cellStyleXfs count=\"1\"><xf/></cellStyleXfs><cellXfs count=\"1\"><xf xfId=\"0\"/></cellXfs></styleSheet>");
            WriteWorksheet(zip, "xl/worksheets/sheet1.xml", Headers, rows.Select(Values));
            WriteWorksheet(zip, "xl/worksheets/sheet2.xml", DiscountHeaders, discountRows.Select(DiscountValues));
        }
        return memory.ToArray();
    }

    private static void WriteWorksheet(ZipArchive zip, string path, IReadOnlyList<string> headers, IEnumerable<IEnumerable<string?>> sourceRows)
    {
        using var sheetMemory = new MemoryStream();
        using (var writer = XmlWriter.Create(sheetMemory, new XmlWriterSettings { Encoding = new UTF8Encoding(false), CloseOutput = false }))
        {
            writer.WriteStartDocument(); writer.WriteStartElement("worksheet", "http://schemas.openxmlformats.org/spreadsheetml/2006/main"); writer.WriteStartElement("sheetData");
            WriteXlsxRow(writer, 1, headers);
            var rowIndex = 2;
            foreach (var row in sourceRows) WriteXlsxRow(writer, rowIndex++, row.Select(x => x ?? string.Empty).ToArray());
            writer.WriteEndElement(); writer.WriteEndElement(); writer.WriteEndDocument();
        }
        sheetMemory.Position = 0; var entry = zip.CreateEntry(path, CompressionLevel.Optimal); using var target = entry.Open(); sheetMemory.CopyTo(target);
    }

    private static void WriteXlsxRow(XmlWriter writer, int row, IReadOnlyList<string> values)
    {
        writer.WriteStartElement("row"); writer.WriteAttributeString("r", row.ToString(CultureInfo.InvariantCulture));
        for (var i = 0; i < values.Count; i++)
        {
            writer.WriteStartElement("c"); writer.WriteAttributeString("r", $"{ColumnName(i + 1)}{row}"); writer.WriteAttributeString("t", "inlineStr");
            writer.WriteStartElement("is"); writer.WriteElementString("t", values[i]); writer.WriteEndElement(); writer.WriteEndElement();
        }
        writer.WriteEndElement();
    }
    private static string ColumnName(int number) { var name = ""; while (number > 0) { number--; name = (char)('A' + number % 26) + name; number /= 26; } return name; }
    private static void WriteEntry(ZipArchive zip, string path, string content) { var entry = zip.CreateEntry(path, CompressionLevel.Optimal); using var stream = entry.Open(); using var writer = new StreamWriter(stream, new UTF8Encoding(false)); writer.Write(content); }

    private async Task<byte[]> ToZipAsync(IReadOnlyList<Purchase> purchases, IReadOnlyList<FlatRow> rows, IReadOnlyList<FlatDiscountRow> discountRows, bool includeDocuments, CancellationToken ct)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
        {
            WriteBytes(zip, "purchases.json", JsonSerializer.SerializeToUtf8Bytes(ToJsonModel(purchases), JsonOptions));
            WriteBytes(zip, "items.csv", Encoding.UTF8.GetBytes(ToCsv(rows)));
            WriteBytes(zip, "discounts.csv", Encoding.UTF8.GetBytes(ToDiscountCsv(discountRows)));
            WriteBytes(zip, "purchases.xlsx", ToXlsx(rows, discountRows));
            if (includeDocuments)
            {
                foreach (var purchase in purchases)
                foreach (var doc in purchase.Documents)
                {
                    ct.ThrowIfCancellationRequested();
                    var absolute = SafeAbsolute(doc.StoragePath); if (!File.Exists(absolute)) continue;
                    var name = SafeFileName(doc.OriginalFileName); var entry = zip.CreateEntry($"documents/{purchase.Id:N}/{doc.Id:N}-{name}", CompressionLevel.Optimal);
                    await using var source = File.OpenRead(absolute); await using var target = entry.Open(); await source.CopyToAsync(target, ct);
                }
            }
        }
        return memory.ToArray();
    }
    private static void WriteBytes(ZipArchive zip, string path, byte[] bytes) { var entry = zip.CreateEntry(path, CompressionLevel.Optimal); using var stream = entry.Open(); stream.Write(bytes); }

    private string SafeAbsolute(string relative)
    {
        var root = Path.GetFullPath(storage.RootPath); var candidate = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar))); var prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.Ordinal)) throw new InvalidOperationException("Invalid purchase document path."); return candidate;
    }
    private static string SafeFileName(string name) { var safe = Path.GetFileName(name); return string.IsNullOrWhiteSpace(safe) ? "document" : safe; }
    private IQueryable<Purchase> Visible(Guid userId, Guid fullWorthSpaceId) => db.Purchases.AsNoTracking().Where(p => p.FullWorthSpaceId == fullWorthSpaceId && (p.Visibility != "private" || p.CreatedByUserId == userId) && (!p.PaymentLinks.Any() || p.PaymentLinks.Any(l => db.Transactions.Any(t => t.Id == l.TransactionId && db.Accounts.Any(a => a.Id == t.AccountId && a.Owners.Any(o => o.UserId == userId))))) && (p.TransactionId == null || db.Transactions.Any(t => t.Id == p.TransactionId && db.Accounts.Any(a => a.Id == t.AccountId && a.Owners.Any(o => o.UserId == userId)))));
}

public static class PurchaseExportEndpoints
{
    public static IEndpointRouteBuilder MapPurchaseExportEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/purchases/export", async (Guid fullWorthSpaceId, string? format, bool? includeDocuments, FullWorth.Backend.Security.CurrentUserContext user, PurchaseExportService service, CancellationToken ct) =>
        {
            var file = await service.ExportAsync(user.RequireUserId(), fullWorthSpaceId, format ?? "json", includeDocuments == true, ct);
            return file is null ? Results.NotFound() : Results.File(file.Bytes, file.ContentType, file.FileName);
        }).WithTags("Purchases");
        return app;
    }
}