using System.IO.Compression;
using System.Security;
using System.Text;
using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Budgets;
using FullWorth.Backend.Modules.Contracts;
using FullWorth.Backend.Modules.Portfolio;
using FullWorth.Backend.Modules.Purchases;
using FullWorth.Backend.Security;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Export;

public static class XlsxExportEndpoints
{
    public static IEndpointRouteBuilder MapXlsxExportEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/export/xlsx", Export).WithTags("Export");
        return app;
    }

    private static async Task<IResult> Export(
        Guid fullWorthSpaceId, DateOnly? from, DateOnly? to, string? accountIds,
        bool? includeArchived, bool? includePurchases, bool? includeInvestments,
        CurrentUserContext currentUser, SpaceAccess space, ExportDataStore exportData, PurchaseAuthorizationStore purchases,
        ContractStore contracts, PortfolioStore portfolioStore, CancellationToken ct)
    {
        var includeArchivedFlag = includeArchived ?? false;
        var includePurchasesFlag = includePurchases ?? false;
        var includeInvestmentsFlag = includeInvestments ?? false;
        var userId = currentUser.RequireUserId();
        if (!await space.HasCapabilityAsync(userId, fullWorthSpaceId, "export.read", ct))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (from.HasValue && to.HasValue && from > to) return Results.BadRequest(new { error = "Invalid date range." });

        var visible = await space.VisibleAccountIdsAsync(userId, fullWorthSpaceId, ct);
        var selected = ParseIds(accountIds);
        if (selected.Count > 0 && selected.Any(id => !visible.Contains(id))) return Results.BadRequest(new { error = "Selected account is unavailable." });
        var accountsToExport = selected.Count > 0 ? selected : visible;

        var accounts = await exportData.AccountsAsync(accountsToExport, includeArchivedFlag, ct);

        var transactions = await exportData.TransactionsAsync(accountsToExport, from, to, ct);
        var transactionIds = transactions.Select(t => t.Id).ToHashSet();
        var allocations = await exportData.AllocationsAsync(transactionIds, ct);
        var categories = await exportData.CategoriesAsync(fullWorthSpaceId, includeArchivedFlag, ct);
        var categoryNames = categories.ToDictionary(c => c.Id, c => c.Name);

        var sheets = new Dictionary<string, List<IReadOnlyList<string>>>(StringComparer.Ordinal)
        {
            ["Metadata"] = Rows(new[] { "SchemaVersion", "2" }, new[] { "ExportedAtUtc", DateTimeOffset.UtcNow.ToString("O") }, new[] { "FullWorthSpaceId", fullWorthSpaceId.ToString() }, new[] { "From", from?.ToString("yyyy-MM-dd") ?? "" }, new[] { "To", to?.ToString("yyyy-MM-dd") ?? "" }),
            ["Accounts"] = BuildAccounts(accounts),
            ["Transactions"] = BuildTransactions(transactions, categoryNames),
            ["TransactionSplits"] = BuildSplits(allocations, categoryNames),
            ["Categories"] = BuildCategories(categories)
        };

        sheets["Tags"] = [.. await exportData.Tags(fullWorthSpaceId, ct)];
        sheets["TransactionTags"] = [.. await exportData.TransactionTags(transactionIds, ct)];
        sheets["Budgets"] = BuildBudgets(await exportData.BudgetsAsync(fullWorthSpaceId, includeArchivedFlag, ct));

        var contractRows = await contracts.ListForUserAsync(userId, fullWorthSpaceId, ct);
        sheets["Contracts"] = BuildContracts(contractRows.Where(c =>
            (includeArchivedFlag || c.IsActive) && (!c.AccountId.HasValue || accountsToExport.Contains(c.AccountId.Value))));

        var assets = await portfolioStore.AssetsForUserAsync(userId, fullWorthSpaceId, ct);
        var liabilities = await portfolioStore.LiabilitiesForUserAsync(userId, fullWorthSpaceId, ct);
        sheets["AssetsLiabilities"] = BuildAssetsLiabilities(assets, liabilities);

        if (includeInvestmentsFlag)
        {
            var investment = await exportData.Investments(fullWorthSpaceId, accountsToExport, includeArchivedFlag, from, to, ct);
            sheets["InvestmentPortfolios"] = [.. investment["investment_portfolios.csv"]];
            sheets["InvestmentTransactions"] = [.. investment["investment_transactions.csv"]];
            sheets["Securities"] = [.. investment["securities.csv"]];
            sheets["SecurityPrices"] = [.. await exportData.SecurityPrices(fullWorthSpaceId, from, to, ct)];
        }

        if (includePurchasesFlag)
        {
            var purchaseRows = (await purchases.ListForUserAsync(userId, fullWorthSpaceId, null, null, from, to, ct))
                .Where(purchase => !purchase.TransactionId.HasValue || transactionIds.Contains(purchase.TransactionId.Value))
                .ToList();
            sheets["Purchases"] = BuildPurchases(purchaseRows);
            sheets["PurchaseItems"] = BuildPurchaseItems(purchaseRows);
        }

        var bytes = BuildXlsx(sheets);
        return Results.File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"fullworth-export-{DateTime.UtcNow:yyyyMMdd-HHmm}.xlsx");
    }

    private static HashSet<Guid> ParseIds(string? value)
    {
        var result = new HashSet<Guid>();
        if (string.IsNullOrWhiteSpace(value)) return result;
        foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (Guid.TryParse(part, out var id)) result.Add(id);
        return result;
    }

    private static List<IReadOnlyList<string>> BuildAccounts(IEnumerable<FullWorth.Backend.Modules.Accounts.FinanceAccount> rows)
    {
        var result = Rows(new[] { "Id","DisplayName","InstitutionName","Provider","Product","AccountType","Currency","IbanLast4","IsActive","IncludeInNetWorth","GroupId","SortOrder" });
        foreach (var a in rows) result.Add(new[] { a.Id.ToString(),a.DisplayName,a.InstitutionName,a.Provider,a.Product??"",a.AccountType??"",a.Currency,a.IbanLast4??"",Bool(a.IsActive),Bool(a.IncludeInNetWorth),a.GroupId?.ToString()??"",a.SortOrder.ToString() });
        return result;
    }

    private static List<IReadOnlyList<string>> BuildTransactions(IEnumerable<FullWorth.Backend.Modules.Transactions.FinanceTransaction> rows, IReadOnlyDictionary<Guid,string> categoryNames)
    {
        var result=Rows(new[]{"Id","AccountId","Date","Status","Amount","Currency","Counterparty","NormalizedCounterparty","Description","CategoryId","Category","CategorizationSource","IsTransfer","TransferPurpose","IsIgnored","RefundOfTransactionId","RefundCategoryId","UserNote"});
        foreach(var t in rows)result.Add(new[]{t.Id.ToString(),t.AccountId.ToString(),(t.BookingDate??t.ValueDate)?.ToString("yyyy-MM-dd")??"",t.Status,Num(t.Amount),t.Currency,t.Counterparty??"",t.NormalizedCounterparty??"",t.Description??"",t.CategoryId?.ToString()??"",t.CategoryId.HasValue?categoryNames.GetValueOrDefault(t.CategoryId.Value,""):"",t.CategorizationSource??"",Bool(t.IsTransfer),t.TransferPurpose??"",Bool(t.IsIgnored),t.RefundOfTransactionId?.ToString()??"",t.RefundCategoryId?.ToString()??"",t.UserNote??""});
        return result;
    }

    private static List<IReadOnlyList<string>> BuildSplits(IEnumerable<FullWorth.Backend.Modules.Transactions.TransactionAllocation> rows,IReadOnlyDictionary<Guid,string> categoryNames)
    {var result=Rows(new[]{"Id","TransactionId","CategoryId","Category","Amount","Note","PurchaseItemId"});foreach(var a in rows)result.Add(new[]{a.Id.ToString(),a.TransactionId.ToString(),a.CategoryId?.ToString()??"",a.CategoryId.HasValue?categoryNames.GetValueOrDefault(a.CategoryId.Value,""):"",Num(a.Amount),a.Note??"",a.PurchaseItemId?.ToString()??""});return result;}
    private static List<IReadOnlyList<string>> BuildCategories(IEnumerable<FinanceCategory> rows)
    {var result=Rows(new[]{"Id","Key","Name","ParentId","Icon","IsSystem","IsArchived","SortOrder"});foreach(var c in rows)result.Add(new[]{c.Id.ToString(),c.Key,c.Name,c.ParentId?.ToString()??"",c.Icon??"",Bool(c.IsSystem),Bool(c.IsArchived),c.SortOrder.ToString()});return result;}

    private static List<IReadOnlyList<string>> BuildBudgets(IReadOnlyList<Budget> rows)
    {var result=Rows(new[]{"Id","Name","CategoryIdLegacy","Amount","Currency","Period","CarryOver","IsActive","StartDate","EndDate"});foreach(var b in rows)result.Add(new[]{b.Id.ToString(),b.Name,b.CategoryId?.ToString()??"",Num(b.Amount),b.Currency,b.Period,Bool(b.CarryOver),Bool(b.IsActive),b.StartDate?.ToString("yyyy-MM-dd")??"",b.EndDate?.ToString("yyyy-MM-dd")??""});return result;}
    private static List<IReadOnlyList<string>> BuildContracts(IEnumerable<ContractView> rows)
    {var result=Rows(new[]{"Id","Name","ProviderName","Kind","CategoryId","AccountId","Amount","Currency","BillingCycle","Interval","StartDate","EndDate","NextDueDate","AutoDetected","IsActive","Notes"});foreach(var c in rows)result.Add(new[]{c.Id.ToString(),c.Name,c.ProviderName??"",c.Kind,c.CategoryId?.ToString()??"",c.AccountId?.ToString()??"",Num(c.Amount),c.Currency,c.BillingCycle,c.Interval.ToString(),c.StartDate?.ToString("yyyy-MM-dd")??"",c.EndDate?.ToString("yyyy-MM-dd")??"",c.NextDueDate?.ToString("yyyy-MM-dd")??"",Bool(c.AutoDetected),Bool(c.IsActive),c.Notes??""});return result;}
    private static List<IReadOnlyList<string>> BuildAssetsLiabilities(IEnumerable<AssetView> assets,IEnumerable<LiabilityView> liabilities)
    {var result=Rows(new[]{"Type","Id","Name","Kind","Value","Currency","DateOrDue","Rate","Payment","IncludeInNetWorth","Notes"});foreach(var a in assets)result.Add(new[]{"asset",a.Id.ToString(),a.Name,a.Kind,Num(a.CurrentValue),a.Currency,a.ValuedAt?.ToString("yyyy-MM-dd")??"",a.AnnualGrowthRate?.ToString(System.Globalization.CultureInfo.InvariantCulture)??"","",Bool(a.IncludeInNetWorth),a.Notes??""});foreach(var l in liabilities)result.Add(new[]{"liability",l.Id.ToString(),l.Name,l.Kind,Num(l.CurrentBalance),l.Currency,l.NextDueDate?.ToString("yyyy-MM-dd")??"",l.InterestRate?.ToString(System.Globalization.CultureInfo.InvariantCulture)??"",l.RegularPayment?.ToString(System.Globalization.CultureInfo.InvariantCulture)??"",Bool(l.IncludeInNetWorth),l.Notes??""});return result;}


    private static List<IReadOnlyList<string>> BuildPurchases(IEnumerable<PurchaseView> rows){var result=Rows(new[]{"Id","TransactionId","Source","Merchant","ExternalOrderId","PurchaseDate","TotalAmount","Currency","Status","MatchConfidence","Notes","HasReceipt"});foreach(var p in rows)result.Add(new[]{p.Id.ToString(),p.TransactionId?.ToString()??"",p.Source,p.Merchant,p.ExternalOrderId??"",p.PurchaseDate?.ToString("yyyy-MM-dd")??"",Num(p.TotalAmount),p.Currency,p.Status,p.MatchConfidence?.ToString(System.Globalization.CultureInfo.InvariantCulture)??"",p.Notes??"",Bool(p.HasReceipt)});return result;}
    private static List<IReadOnlyList<string>> BuildPurchaseItems(IEnumerable<PurchaseView> rows){var result=Rows(new[]{"PurchaseId","ItemId","CategoryId","Name","Brand","Sku","Asin","Quantity","UnitPrice","TotalPrice","Currency","CategorizationSource","Notes"});foreach(var p in rows)foreach(var i in p.Items)result.Add(new[]{p.Id.ToString(),i.Id.ToString(),i.CategoryId?.ToString()??"",i.Name,i.Brand??"",i.Sku??"",i.Asin??"",Num(i.Quantity),i.UnitPrice?.ToString(System.Globalization.CultureInfo.InvariantCulture)??"",Num(i.TotalPrice),i.Currency,i.CategorizationSource,i.Notes??""});return result;}

    private static List<IReadOnlyList<string>> Rows(params string[][] rows)=>rows.Select(row=>(IReadOnlyList<string>)row).ToList();
    private static string Bool(bool value)=>value?"true":"false";
    private static string Num(decimal value)=>value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static byte[] BuildXlsx(IReadOnlyDictionary<string,List<IReadOnlyList<string>>> sheets)
    {
        using var ms=new MemoryStream();using(var zip=new ZipArchive(ms,ZipArchiveMode.Create,true))
        {
            Add(zip,"[Content_Types].xml",ContentTypes(sheets.Count));
            Add(zip,"_rels/.rels","<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
            Add(zip,"xl/workbook.xml",Workbook(sheets.Keys));Add(zip,"xl/_rels/workbook.xml.rels",WorkbookRels(sheets.Count));var index=1;foreach(var sheet in sheets)Add(zip,$"xl/worksheets/sheet{index++}.xml",SheetXml(sheet.Value));
        }return ms.ToArray();
    }
    private static void Add(ZipArchive zip,string path,string content){var entry=zip.CreateEntry(path,CompressionLevel.Fastest);using var writer=new StreamWriter(entry.Open(),new UTF8Encoding(false));writer.Write(content);}
    private static string ContentTypes(int count)=>"<?xml version=\"1.0\" encoding=\"UTF-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>"+string.Concat(Enumerable.Range(1,count).Select(i=>$"<Override PartName=\"/xl/worksheets/sheet{i}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>"))+"</Types>";
    private static string Workbook(IEnumerable<string> names){var i=1;return "<?xml version=\"1.0\" encoding=\"UTF-8\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>"+string.Concat(names.Select(n=>$"<sheet name=\"{Xml(n[..Math.Min(31,n.Length)])}\" sheetId=\"{i}\" r:id=\"rId{i++}\"/>"))+"</sheets></workbook>";}
    private static string WorkbookRels(int count)=>"<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"+string.Concat(Enumerable.Range(1,count).Select(i=>$"<Relationship Id=\"rId{i}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{i}.xml\"/>"))+"</Relationships>";
    private static string SheetXml(IEnumerable<IReadOnlyList<string>> rows){var sb=new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");var ri=1;foreach(var row in rows){sb.Append($"<row r=\"{ri++}\">");foreach(var value in row)sb.Append($"<c t=\"inlineStr\"><is><t xml:space=\"preserve\">{Xml(value)}</t></is></c>");sb.Append("</row>");}return sb.Append("</sheetData></worksheet>").ToString();}
    private static string Xml(string? value)=>SecurityElement.Escape(value??"")??"";
}