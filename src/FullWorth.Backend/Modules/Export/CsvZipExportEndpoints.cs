using System.Globalization;
using System.IO.Compression;
using System.Text;
using FullWorth.Backend.Modules.Export;
using FullWorth.Backend.Modules.Purchases;
using FullWorth.Backend.Security;
using Microsoft.EntityFrameworkCore;

using static FullWorth.Backend.Modules.Export.CsvCell;

namespace FullWorth.Backend.Modules.Export;

public static class CsvZipExportEndpoints
{
    public static IEndpointRouteBuilder MapCsvZipExportEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/export/csv-zip-v2", Export).WithTags("Export");
        return app;
    }

    private static async Task<IResult> Export(
        Guid fullWorthSpaceId,
        DateOnly? from,
        DateOnly? to,
        string? accountIds,
        bool? includeArchived,
        bool? includePurchases,
        bool? includeInvestments,
        CurrentUserContext currentUser,
        SpaceAccess space,
        ExportService exportService,
        ExportDataStore exportData,
        PurchaseAuthorizationStore purchaseStore,
        CancellationToken ct)
    {
        var includeArchivedFlag = includeArchived ?? false;
        var includePurchasesFlag = includePurchases ?? false;
        var includeInvestmentsFlag = includeInvestments ?? false;
        var userId = currentUser.RequireUserId();
        if (!await space.HasCapabilityAsync(userId, fullWorthSpaceId, "export.read", ct))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (from.HasValue && to.HasValue && from > to)
            return Results.BadRequest(new { error = "Invalid date range." });

        var snapshot = await exportService.SnapshotForUserAsync(userId, fullWorthSpaceId, ct);
        if (snapshot is null) return Results.NotFound();

        var visibleIds = snapshot.Accounts.Select(account => account.Id).ToHashSet();
        var selectedIds = ParseIds(accountIds);
        if (selectedIds.Count > 0 && selectedIds.Any(id => !visibleIds.Contains(id)))
            return Results.BadRequest(new { error = "Selected account is unavailable." });
        var exportedAccountIds = selectedIds.Count > 0 ? selectedIds : visibleIds;

        var accounts = snapshot.Accounts
            .Where(account => exportedAccountIds.Contains(account.Id) && (includeArchivedFlag || account.IsActive))
            .OrderBy(account => account.SortOrder).ThenBy(account => account.DisplayName).ToArray();
        var transactions = snapshot.Transactions
            .Where(transaction => exportedAccountIds.Contains(transaction.AccountId))
            .Where(transaction => !from.HasValue || (transaction.BookingDate ?? transaction.ValueDate) >= from.Value)
            .Where(transaction => !to.HasValue || (transaction.BookingDate ?? transaction.ValueDate) <= to.Value)
            .OrderBy(transaction => transaction.BookingDate ?? transaction.ValueDate).ThenBy(transaction => transaction.Id).ToArray();
        var transactionIds = transactions.Select(transaction => transaction.Id).ToHashSet();

        var categories = await exportData.CategoriesAsync(fullWorthSpaceId, includeArchivedFlag, ct);
        var categoryNames = categories.ToDictionary(category => category.Id, category => category.Name);

        var allocations = await exportData.AllocationsAsync(transactionIds, ct);

        var files = new Dictionary<string, List<string[]>>(StringComparer.OrdinalIgnoreCase)
        {
            ["metadata.csv"] = Table(
                new[] { "Key", "Value" },
                new[] { "SchemaVersion", "2" },
                new[] { "ExportedAtUtc", DateTimeOffset.UtcNow.ToString("O") },
                new[] { "FullWorthSpaceId", fullWorthSpaceId.ToString() },
                new[] { "From", from?.ToString("yyyy-MM-dd") ?? "" },
                new[] { "To", to?.ToString("yyyy-MM-dd") ?? "" }),
            ["accounts.csv"] = Accounts(accounts),
            ["transactions.csv"] = Transactions(transactions, categoryNames),
            ["transaction_splits.csv"] = Splits(allocations, categoryNames),
            ["categories.csv"] = Categories(categories),
            ["rules.csv"] = Rules(snapshot.Rules),
            ["contracts.csv"] = Contracts(snapshot.Contracts.Where(contract =>
                (includeArchivedFlag || contract.IsActive) && (!contract.AccountId.HasValue || exportedAccountIds.Contains(contract.AccountId.Value)))),
            ["budgets.csv"] = Budgets(snapshot.Budgets.Where(budget => includeArchivedFlag || budget.IsActive)),
            ["assets.csv"] = Assets(snapshot.Assets),
            ["liabilities.csv"] = Liabilities(snapshot.Liabilities),
            ["net_worth_history.csv"] = NetWorth(snapshot.NetWorthHistory)
        };

        files["tags.csv"] = await exportData.Tags(fullWorthSpaceId, ct);
        files["transaction_tags.csv"] = await exportData.TransactionTags(transactionIds, ct);

        if (includePurchasesFlag)
        {
            var purchases = (await purchaseStore.ListForUserAsync(userId, fullWorthSpaceId, null, null, from, to, ct))
                .Where(purchase => !purchase.TransactionId.HasValue || transactionIds.Contains(purchase.TransactionId.Value))
                .ToList();
            files["purchases.csv"] = Purchases(purchases);
            files["purchase_items.csv"] = PurchaseItems(purchases);
        }

        if (includeInvestmentsFlag)
        {
            var investmentFiles = await exportData.Investments(fullWorthSpaceId, exportedAccountIds, includeArchivedFlag, from, to, ct);
            foreach (var pair in investmentFiles) files[pair.Key] = pair.Value;
        }

        var bytes = BuildZip(files);
        return Results.File(bytes, "application/zip", $"fullworth-export-{DateTime.UtcNow:yyyyMMdd-HHmm}.zip");
    }

    private static HashSet<Guid> ParseIds(string? value)
    {
        var ids = new HashSet<Guid>();
        if (string.IsNullOrWhiteSpace(value)) return ids;
        foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (Guid.TryParse(part, out var id)) ids.Add(id);
        return ids;
    }

    private static byte[] BuildZip(IReadOnlyDictionary<string, List<string[]>> files)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var pair in files.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                var entry = zip.CreateEntry(pair.Key, CompressionLevel.Fastest);
                using var stream = entry.Open();
                using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
                foreach (var row in pair.Value)
                    writer.WriteLine(string.Join(',', row.Select(EscapeCsv)));
            }
        }
        return output.ToArray();
    }

    private static string EscapeCsv(string? value)
    {
        var text = value ?? string.Empty;
        return text.IndexOfAny([',', '"', '\r', '\n']) >= 0
            ? $"\"{text.Replace("\"", "\"\"")}\""
            : text;
    }

    private static List<string[]> Accounts(IEnumerable<ExportAccount> rows)
    {
        var result = Table(new[] { "Id", "DisplayName", "InstitutionName", "Product", "AccountType", "Currency", "IbanLast4", "IsActive", "IncludeInNetWorth", "SortOrder" });
        result.AddRange(rows.Select(row => new[] { row.Id.ToString(), row.DisplayName, row.InstitutionName, row.Product ?? "", row.AccountType ?? "", row.Currency, row.IbanLast4 ?? "", Bool(row.IsActive), Bool(row.IncludeInNetWorth), row.SortOrder.ToString(CultureInfo.InvariantCulture) }));
        return result;
    }

    private static List<string[]> Transactions(IEnumerable<ExportTransaction> rows, IReadOnlyDictionary<Guid, string> categories)
    {
        var result = Table(new[] { "Id", "AccountId", "Date", "Status", "Amount", "Currency", "Counterparty", "Description", "CategoryId", "Category", "CategorizationSource", "IsTransfer", "IsIgnored", "UserNote" });
        result.AddRange(rows.Select(row => new[] { row.Id.ToString(), row.AccountId.ToString(), Date(row.BookingDate ?? row.ValueDate), row.Status, Num(row.Amount), row.Currency, row.Counterparty ?? "", row.Description ?? "", row.CategoryId?.ToString() ?? "", row.CategoryId.HasValue ? categories.GetValueOrDefault(row.CategoryId.Value, "") : "", row.CategorizationSource, Bool(row.IsTransfer), Bool(row.IsIgnored), row.UserNote ?? "" }));
        return result;
    }

    private static List<string[]> Splits(IEnumerable<FullWorth.Backend.Modules.Transactions.TransactionAllocation> rows, IReadOnlyDictionary<Guid, string> categories)
    {
        var result = Table(new[] { "Id", "TransactionId", "CategoryId", "Category", "Amount", "Note", "PurchaseItemId" });
        result.AddRange(rows.Select(row => new[] { row.Id.ToString(), row.TransactionId.ToString(), row.CategoryId?.ToString() ?? "", row.CategoryId.HasValue ? categories.GetValueOrDefault(row.CategoryId.Value, "") : "", Num(row.Amount), row.Note ?? "", row.PurchaseItemId?.ToString() ?? "" }));
        return result;
    }

    private static List<string[]> Categories(IEnumerable<FullWorth.Backend.Modules.Categories.FinanceCategory> rows)
    {
        var result = Table(new[] { "Id", "Key", "Name", "ParentId", "Icon", "IsSystem", "IsArchived", "SortOrder" });
        result.AddRange(rows.Select(row => new[] { row.Id.ToString(), row.Key, row.Name, row.ParentId?.ToString() ?? "", row.Icon ?? "", Bool(row.IsSystem), Bool(row.IsArchived), row.SortOrder.ToString(CultureInfo.InvariantCulture) }));
        return result;
    }

    private static List<string[]> Rules(IEnumerable<ExportRule> rows)
    {
        var result = Table(new[] { "Id", "Name", "IsEnabled", "Priority", "Target", "MatchField", "MatchMode", "Pattern", "Direction", "MinAmount", "MaxAmount", "MerchantCategoryCode", "CategoryId", "MarkAsTransfer", "StopProcessing" });
        result.AddRange(rows.Select(row => new[] { row.Id.ToString(), row.Name, Bool(row.IsEnabled), row.Priority.ToString(CultureInfo.InvariantCulture), row.Target, row.MatchField, row.MatchMode, row.Pattern, row.Direction, row.MinAmount?.ToString(CultureInfo.InvariantCulture) ?? "", row.MaxAmount?.ToString(CultureInfo.InvariantCulture) ?? "", row.MerchantCategoryCode ?? "", row.CategoryId.ToString(), Bool(row.MarkAsTransfer), Bool(row.StopProcessing) }));
        return result;
    }

    private static List<string[]> Contracts(IEnumerable<ExportContract> rows)
    {
        var result = Table(new[] { "Id", "Name", "ProviderName", "Kind", "CategoryId", "AccountId", "Amount", "Currency", "BillingCycle", "Interval", "StartDate", "EndDate", "NextDueDate", "AutoDetected", "IsActive", "Notes" });
        result.AddRange(rows.Select(row => new[] { row.Id.ToString(), row.Name, row.ProviderName ?? "", row.Kind, row.CategoryId?.ToString() ?? "", row.AccountId?.ToString() ?? "", Num(row.Amount), row.Currency, row.BillingCycle, row.Interval.ToString(CultureInfo.InvariantCulture), Date(row.StartDate), Date(row.EndDate), Date(row.NextDueDate), Bool(row.AutoDetected), Bool(row.IsActive), row.Notes ?? "" }));
        return result;
    }

    private static List<string[]> Budgets(IEnumerable<ExportBudget> rows)
    {
        var result = Table(new[] { "Id", "Name", "CategoryId", "Amount", "Currency", "Period", "CarryOver", "IsActive", "StartDate", "EndDate" });
        result.AddRange(rows.Select(row => new[] { row.Id.ToString(), row.Name, row.CategoryId?.ToString() ?? "", Num(row.Amount), row.Currency, row.Period, Bool(row.CarryOver), Bool(row.IsActive), Date(row.StartDate), Date(row.EndDate) }));
        return result;
    }

    private static List<string[]> Assets(IEnumerable<ExportAsset> rows)
    {
        var result = Table(new[] { "Id", "Name", "Kind", "CurrentValue", "Currency", "ValuedAt", "AnnualGrowthRate", "IncludeInNetWorth", "Notes" });
        result.AddRange(rows.Select(row => new[] { row.Id.ToString(), row.Name, row.Kind, Num(row.CurrentValue), row.Currency, Date(row.ValuedAt), row.AnnualGrowthRate?.ToString(CultureInfo.InvariantCulture) ?? "", Bool(row.IncludeInNetWorth), row.Notes ?? "" }));
        return result;
    }

    private static List<string[]> Liabilities(IEnumerable<ExportLiability> rows)
    {
        var result = Table(new[] { "Id", "Name", "Kind", "CurrentBalance", "Currency", "InterestRate", "RegularPayment", "PaymentCycle", "NextDueDate", "EndDate", "IncludeInNetWorth", "Notes" });
        result.AddRange(rows.Select(row => new[] { row.Id.ToString(), row.Name, row.Kind, Num(row.CurrentBalance), row.Currency, row.InterestRate?.ToString(CultureInfo.InvariantCulture) ?? "", row.RegularPayment?.ToString(CultureInfo.InvariantCulture) ?? "", row.PaymentCycle, Date(row.NextDueDate), Date(row.EndDate), Bool(row.IncludeInNetWorth), row.Notes ?? "" }));
        return result;
    }

    private static List<string[]> NetWorth(IEnumerable<ExportNetWorth> rows)
    {
        var result = Table(new[] { "Id", "Date", "Currency", "Accounts", "Assets", "Liabilities", "NetWorth", "CreatedAt" });
        result.AddRange(rows.Select(row => new[] { row.Id.ToString(), Date(row.Date), row.Currency, Num(row.Accounts), Num(row.Assets), Num(row.Liabilities), Num(row.NetWorth), row.CreatedAt.ToString("O") }));
        return result;
    }

    private static List<string[]> Purchases(IEnumerable<PurchaseView> rows)
    {
        var result = Table(new[] { "Id", "TransactionId", "Source", "Merchant", "ExternalOrderId", "PurchaseDate", "TotalAmount", "Currency", "Status", "MatchConfidence", "Notes", "HasReceipt" });
        result.AddRange(rows.Select(row => new[] { row.Id.ToString(), row.TransactionId?.ToString() ?? "", row.Source, row.Merchant, row.ExternalOrderId ?? "", Date(row.PurchaseDate), Num(row.TotalAmount), row.Currency, row.Status, row.MatchConfidence?.ToString(CultureInfo.InvariantCulture) ?? "", row.Notes ?? "", Bool(row.HasReceipt) }));
        return result;
    }

    private static List<string[]> PurchaseItems(IEnumerable<PurchaseView> rows)
    {
        var result = Table(new[] { "PurchaseId", "ItemId", "CategoryId", "Name", "Brand", "Sku", "Asin", "Quantity", "UnitPrice", "TotalPrice", "Currency", "CategorizationSource", "Notes" });
        foreach (var purchase in rows)
            result.AddRange(purchase.Items.Select(item => new[] { purchase.Id.ToString(), item.Id.ToString(), item.CategoryId?.ToString() ?? "", item.Name, item.Brand ?? "", item.Sku ?? "", item.Asin ?? "", Num(item.Quantity), item.UnitPrice?.ToString(CultureInfo.InvariantCulture) ?? "", Num(item.TotalPrice), item.Currency, item.CategorizationSource, item.Notes ?? "" }));
        return result;
    }

}
