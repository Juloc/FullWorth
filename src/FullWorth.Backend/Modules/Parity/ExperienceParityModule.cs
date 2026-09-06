using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;
using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Audit;
using FullWorth.Backend.Modules.Merchants;
using FullWorth.Backend.Modules.Purchases;
using FullWorth.Backend.Security;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Parity;

public sealed record AccountAppearanceWrite(string? Icon, string? IconColor, string? BackgroundColor);
public sealed record CapabilityGrantWrite(Guid UserId, string Capability, bool IsAllowed);
public sealed record ProductAliasWrite(string DisplayName, Guid? CategoryId);

public static class ExperienceParityEndpoints
{
    private static readonly HashSet<string> Capabilities = new(StringComparer.OrdinalIgnoreCase)
    {
        "view", "categorize", "edit_transactions", "edit_budgets", "edit_contracts",
        "manage_banking", "manage_imports", "manage_investments", "admin"
    };

    public static IEndpointRouteBuilder MapExperienceParityEndpoints(this IEndpointRouteBuilder app)
    {
        var accounts = app.MapGroup("/api/account-experience").WithTags("Accounts");
        accounts.MapGet("/", AccountExperience);
        accounts.MapPut("/{accountId:guid}/appearance", PutAccountAppearance);
        accounts.MapPost("/{accountId:guid}/seen", MarkSeen);

        var products = app.MapGroup("/api/product-intelligence").WithTags("Purchases");
        products.MapGet("/summary", ProductSummary);
        products.MapGet("/history", ProductHistory);
        products.MapPut("/aliases/{normalizedName}", PutProductAlias);

        var grants = app.MapGroup("/api/capability-grants").WithTags("Sharing");
        grants.MapGet("/", ListGrants);
        grants.MapPut("/", PutGrant);

        app.MapGet("/api/export/xlsx", ExportXlsx).WithTags("Export");
        return app;
    }

    private static async Task<IResult> AccountExperience(
        Guid fullWorthSpaceId, CurrentUserContext currentUser, FullWorthDbContext db, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        var visible = await ParitySql.VisibleAccountIdsAsync(db, userId, fullWorthSpaceId, ct);
        if (visible.Count == 0) return Results.Ok(Array.Empty<object>());

        var accounts = await db.Accounts.AsNoTracking()
            .Where(account => visible.Contains(account.Id))
            .OrderBy(account => account.SortOrder)
            .ThenBy(account => account.DisplayName)
            .ToListAsync(ct);
        var connection = await ParitySql.OpenAsync(db, ct);
        var rows = new List<object>();

        foreach (var account in accounts)
        {
            string? icon = null;
            string? iconColor = null;
            string? backgroundColor = null;
            DateTimeOffset? lastSeenAt = null;

            await using (var cmd = ParitySql.Command(connection,
                "SELECT \"Icon\",\"IconColor\",\"BackgroundColor\" FROM \"AccountAppearances\" WHERE \"AccountId\"=@id",
                ("@id", account.Id)))
            await using (var reader = await cmd.ExecuteReaderAsync(ct))
            {
                if (await reader.ReadAsync(ct))
                {
                    icon = ParitySql.NullableString(reader, "Icon");
                    iconColor = ParitySql.NullableString(reader, "IconColor");
                    backgroundColor = ParitySql.NullableString(reader, "BackgroundColor");
                }
            }

            await using (var cmd = ParitySql.Command(connection,
                "SELECT \"LastSeenAt\" FROM \"AccountTransactionSeenStates\" WHERE \"UserId\"=@user AND \"AccountId\"=@account",
                ("@user", userId), ("@account", account.Id)))
            {
                var value = await cmd.ExecuteScalarAsync(ct);
                if (value is DateTimeOffset timestamp) lastSeenAt = timestamp;
                else if (value is DateTime timestampDateTime) lastSeenAt = new DateTimeOffset(timestampDateTime);
            }

            var unseen = await db.Transactions.AsNoTracking().CountAsync(transaction =>
                transaction.AccountId == account.Id &&
                (!lastSeenAt.HasValue || transaction.FirstSeenAt > lastSeenAt.Value), ct);

            rows.Add(new
            {
                accountId = account.Id,
                account.DisplayName,
                account.InstitutionName,
                account.Provider,
                account.AccountType,
                account.Product,
                icon,
                iconColor,
                backgroundColor,
                unseenTransactions = unseen,
                lastSeenAt
            });
        }

        return Results.Ok(rows);
    }

    private static async Task<IResult> PutAccountAppearance(
        Guid accountId, Guid fullWorthSpaceId, AccountAppearanceWrite request,
        CurrentUserContext currentUser, FullWorthDbContext db, AuditService audit, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        var writable = await ParitySql.WritableAccountIdsAsync(db, userId, fullWorthSpaceId, ct);
        if (!writable.Contains(accountId)) return Results.NotFound();
        if (!ValidColor(request.IconColor) || !ValidColor(request.BackgroundColor))
            return Results.BadRequest(new { error = "Colors must be #RRGGBB or #RRGGBBAA." });

        var connection = await ParitySql.OpenAsync(db, ct);
        await using var cmd = ParitySql.Command(connection, """
INSERT INTO "AccountAppearances" ("AccountId","Icon","IconColor","BackgroundColor","UpdatedAt")
VALUES (@id,@icon,@iconColor,@background,@now)
ON CONFLICT ("AccountId") DO UPDATE SET
  "Icon"=EXCLUDED."Icon",
  "IconColor"=EXCLUDED."IconColor",
  "BackgroundColor"=EXCLUDED."BackgroundColor",
  "UpdatedAt"=EXCLUDED."UpdatedAt"
""",
            ("@id", accountId),
            ("@icon", string.IsNullOrWhiteSpace(request.Icon) ? null : request.Icon.Trim()),
            ("@iconColor", NormalizeColor(request.IconColor)),
            ("@background", NormalizeColor(request.BackgroundColor)),
            ("@now", DateTimeOffset.UtcNow));
        await cmd.ExecuteNonQueryAsync(ct);
        audit.Record(fullWorthSpaceId, userId, "account.appearance.updated", "FinanceAccount", accountId);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> MarkSeen(
        Guid accountId, Guid fullWorthSpaceId, CurrentUserContext currentUser, FullWorthDbContext db, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        var visible = await ParitySql.VisibleAccountIdsAsync(db, userId, fullWorthSpaceId, ct);
        if (!visible.Contains(accountId)) return Results.NotFound();

        var connection = await ParitySql.OpenAsync(db, ct);
        await using var cmd = ParitySql.Command(connection, """
INSERT INTO "AccountTransactionSeenStates" ("UserId","AccountId","LastSeenAt")
VALUES (@user,@account,@now)
ON CONFLICT ("UserId","AccountId") DO UPDATE SET "LastSeenAt"=EXCLUDED."LastSeenAt"
""", ("@user", userId), ("@account", accountId), ("@now", DateTimeOffset.UtcNow));
        await cmd.ExecuteNonQueryAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> ProductSummary(
        Guid fullWorthSpaceId, CurrentUserContext currentUser, FullWorthDbContext db, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await ParitySql.IsMemberAsync(db, userId, fullWorthSpaceId, ct)) return Results.NotFound();
        var visibleAccounts = await ParitySql.VisibleAccountIdsAsync(db, userId, fullWorthSpaceId, ct);

        var purchases = await db.Purchases.AsNoTracking()
            .Where(purchase => purchase.FullWorthSpaceId == fullWorthSpaceId &&
                (purchase.TransactionId == null || db.Transactions.Any(transaction =>
                    transaction.Id == purchase.TransactionId.Value && visibleAccounts.Contains(transaction.AccountId))))
            .Include(purchase => purchase.Items)
            .OrderByDescending(purchase => purchase.PurchaseDate)
            .Take(5000)
            .ToListAsync(ct);
        var aliases = await LoadAliases(db, fullWorthSpaceId, ct);

        var rows = purchases
            .SelectMany(purchase => purchase.Items.Select(item => new
            {
                Purchase = purchase,
                Item = item,
                Normalized = NormalizeProduct(item.Name)
            }))
            .Where(row => row.Normalized.Length > 1)
            .GroupBy(row => row.Normalized)
            .Select(group =>
            {
                var latest = group.OrderByDescending(row => row.Purchase.PurchaseDate).First();
                aliases.TryGetValue(group.Key, out var alias);
                var unitPrices = group
                    .Where(row => row.Item.Quantity > 0)
                    .Select(row => row.Item.TotalPrice / row.Item.Quantity)
                    .ToArray();
                return new
                {
                    normalizedName = group.Key,
                    displayName = alias?.DisplayName ?? latest.Item.Name,
                    categoryId = alias?.CategoryId,
                    purchases = group.Count(),
                    latestPrice = latest.Item.Quantity == 0 ? latest.Item.TotalPrice : latest.Item.TotalPrice / latest.Item.Quantity,
                    averagePrice = unitPrices.Length == 0 ? 0 : Math.Round(unitPrices.Average(), 2),
                    currency = latest.Item.Currency,
                    lastPurchased = latest.Purchase.PurchaseDate,
                    merchants = group.Select(row => row.Purchase.Merchant).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct().Take(5)
                };
            })
            .OrderByDescending(row => row.purchases)
            .Take(500);

        return Results.Ok(rows);
    }

    private static async Task<IResult> ProductHistory(
        Guid fullWorthSpaceId, string name, CurrentUserContext currentUser, FullWorthDbContext db, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await ParitySql.IsMemberAsync(db, userId, fullWorthSpaceId, ct)) return Results.NotFound();
        var visibleAccounts = await ParitySql.VisibleAccountIdsAsync(db, userId, fullWorthSpaceId, ct);
        var normalized = NormalizeProduct(name);

        var purchases = await db.Purchases.AsNoTracking()
            .Where(purchase => purchase.FullWorthSpaceId == fullWorthSpaceId &&
                (purchase.TransactionId == null || db.Transactions.Any(transaction =>
                    transaction.Id == purchase.TransactionId.Value && visibleAccounts.Contains(transaction.AccountId))))
            .Include(purchase => purchase.Items)
            .ToListAsync(ct);

        var rows = purchases.SelectMany(purchase => purchase.Items
                .Where(item => NormalizeProduct(item.Name) == normalized)
                .Select(item => new
                {
                    date = purchase.PurchaseDate,
                    merchant = purchase.Merchant,
                    item.Name,
                    item.Quantity,
                    total = item.TotalPrice,
                    unitPrice = item.Quantity == 0 ? item.TotalPrice : item.TotalPrice / item.Quantity,
                    item.Currency,
                    purchaseId = purchase.Id
                }))
            .OrderBy(row => row.date);
        return Results.Ok(rows);
    }

    private static async Task<IResult> PutProductAlias(
        string normalizedName, Guid fullWorthSpaceId, ProductAliasWrite request,
        CurrentUserContext currentUser, FullWorthDbContext db, AuditService audit, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await ParitySql.IsOwnerAsync(db, userId, fullWorthSpaceId, ct)) return Results.StatusCode(403);
        var normalized = NormalizeProduct(normalizedName);
        if (string.IsNullOrWhiteSpace(request.DisplayName) || normalized.Length < 2) return Results.BadRequest();
        if (request.CategoryId.HasValue && !await db.Categories.AsNoTracking().AnyAsync(category =>
                category.Id == request.CategoryId.Value && category.FullWorthSpaceId == fullWorthSpaceId, ct))
            return Results.BadRequest(new { error = "Category is invalid." });

        // Canonical model: a product carries the display (CanonicalName) + default category, and aliases
        // link normalized names to it. Upsert by finding an existing product through a matching alias in
        // this space, otherwise create the product and its manual alias.
        var displayName = request.DisplayName.Trim();
        var now = DateTimeOffset.UtcNow;
        var product = await db.Set<ProductAlias>()
            .Where(alias => alias.NormalizedAlias == normalized && alias.Product.FullWorthSpaceId == fullWorthSpaceId)
            .Select(alias => alias.Product)
            .FirstOrDefaultAsync(ct);
        if (product is not null)
        {
            product.CanonicalName = displayName;
            product.DefaultCategoryId = request.CategoryId;
            product.UpdatedAt = now;
        }
        else
        {
            product = new Product
            {
                FullWorthSpaceId = fullWorthSpaceId,
                CanonicalName = displayName,
                DefaultCategoryId = request.CategoryId,
                CreatedAt = now,
                UpdatedAt = now
            };
            db.Add(product);
            db.Add(new ProductAlias
            {
                ProductId = product.Id,
                Alias = displayName,
                NormalizedAlias = normalized,
                AliasType = "manual",
                CreatedAt = now
            });
        }
        audit.Record(fullWorthSpaceId, userId, "product.alias.updated", "ProductAlias", product.Id);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> ListGrants(
        Guid fullWorthSpaceId, CurrentUserContext currentUser, FullWorthDbContext db, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await ParitySql.IsOwnerAsync(db, userId, fullWorthSpaceId, ct)) return Results.StatusCode(403);
        var connection = await ParitySql.OpenAsync(db, ct);
        await using var cmd = ParitySql.Command(connection, """
SELECT "UserId","Capability","IsAllowed","UpdatedAt"
FROM "FinanceCapabilityGrants"
WHERE "FullWorthSpaceId"=@space
ORDER BY "UserId","Capability"
""", ("@space", fullWorthSpaceId));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<object>();
        while (await reader.ReadAsync(ct)) rows.Add(new
        {
            userId = ParitySql.Guid(reader, "UserId"),
            capability = ParitySql.String(reader, "Capability"),
            isAllowed = ParitySql.Bool(reader, "IsAllowed"),
            updatedAt = ParitySql.Timestamp(reader, "UpdatedAt")
        });
        return Results.Ok(rows);
    }

    private static async Task<IResult> PutGrant(
        Guid fullWorthSpaceId, CapabilityGrantWrite request, CurrentUserContext currentUser,
        FullWorthDbContext db, AuditService audit, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await ParitySql.IsOwnerAsync(db, userId, fullWorthSpaceId, ct)) return Results.StatusCode(403);
        var capability = request.Capability.Trim().ToLowerInvariant();
        if (!Capabilities.Contains(capability) || !await db.FullWorthSpaceMembers.AsNoTracking().AnyAsync(member =>
                member.FullWorthSpaceId == fullWorthSpaceId && member.UserId == request.UserId, ct))
            return Results.BadRequest();

        var connection = await ParitySql.OpenAsync(db, ct);
        await using var cmd = ParitySql.Command(connection, """
INSERT INTO "FinanceCapabilityGrants" ("FullWorthSpaceId","UserId","Capability","IsAllowed","UpdatedAt")
VALUES (@space,@user,@capability,@allowed,@now)
ON CONFLICT ("FullWorthSpaceId","UserId","Capability") DO UPDATE SET
  "IsAllowed"=EXCLUDED."IsAllowed",
  "UpdatedAt"=EXCLUDED."UpdatedAt"
""",
            ("@space", fullWorthSpaceId), ("@user", request.UserId), ("@capability", capability),
            ("@allowed", request.IsAllowed), ("@now", DateTimeOffset.UtcNow));
        await cmd.ExecuteNonQueryAsync(ct);
        audit.Record(fullWorthSpaceId, userId, "sharing.capability.updated", "FullWorthUser", request.UserId);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> ExportXlsx(
        Guid fullWorthSpaceId, CurrentUserContext currentUser, FullWorthDbContext db, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await ParitySql.IsMemberAsync(db, userId, fullWorthSpaceId, ct)) return Results.NotFound();
        var visibleAccounts = await ParitySql.VisibleAccountIdsAsync(db, userId, fullWorthSpaceId, ct);

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

    private sealed record AliasRow(string? DisplayName, Guid? CategoryId);

    private static async Task<Dictionary<string, AliasRow>> LoadAliases(
        FullWorthDbContext db, Guid fullWorthSpaceId, CancellationToken ct)
    {
        var result = new Dictionary<string, AliasRow>(StringComparer.OrdinalIgnoreCase);
        var connection = await ParitySql.OpenAsync(db, ct);
        await using var cmd = ParitySql.Command(connection,
            "SELECT a.\"NormalizedAlias\" AS \"NormalizedName\", p.\"CanonicalName\" AS \"DisplayName\", p.\"DefaultCategoryId\" AS \"CategoryId\" " +
            "FROM \"ProductAliases\" a JOIN \"Products\" p ON p.\"Id\"=a.\"ProductId\" WHERE p.\"FullWorthSpaceId\"=@space",
            ("@space", fullWorthSpaceId));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result[ParitySql.String(reader, "NormalizedName")] = new(
                ParitySql.NullableString(reader, "DisplayName"), ParitySql.NullableGuid(reader, "CategoryId"));
        return result;
    }

    private static string NormalizeProduct(string? value) => MerchantNormalization.Normalize(value)?.ToLowerInvariant() ?? string.Empty;
    private static bool ValidColor(string? value) => string.IsNullOrWhiteSpace(value) ||
        (value.StartsWith('#') && (value.Length == 7 || value.Length == 9) && value.Skip(1).All(Uri.IsHexDigit));
    private static string? NormalizeColor(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();

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
