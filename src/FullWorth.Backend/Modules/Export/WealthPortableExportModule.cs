using System.Data;
using System.Data.Common;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Purchases;
using FullWorth.Backend.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FullWorth.Backend.Modules.Export;

public sealed record WealthPortableExport(
    string Format,
    int SchemaVersion,
    DateTimeOffset ExportedAt,
    Guid FullWorthSpaceId,
    ExportSnapshot Snapshot,
    IReadOnlyDictionary<string, object?> Wealth,
    IReadOnlyList<string> BackupWarnings,
    object InvestmentExport);

public sealed record WealthBackupResult(byte[] Bytes, string FileName, IReadOnlyList<string> Warnings);
public sealed record WealthBackupValidation(bool Valid, Guid? FullWorthSpaceId, int? SchemaVersion, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings, int DocumentsChecked);

public sealed class WealthPortableExportService(
    FullWorthDbContext db,
    ExportService baseExport,
    IOptions<PurchaseStorageOptions> storageOptions)
{
    private readonly PurchaseStorageOptions storage = storageOptions.Value;

    public async Task<WealthPortableExport?> BuildAsync(Guid userId, Guid fullWorthSpaceId, CancellationToken ct)
    {
        var snapshot = await baseExport.SnapshotForUserAsync(userId, fullWorthSpaceId, ct);
        if (snapshot is null) return null;

        var accessibleTransactionIds = snapshot.Transactions.Select(x => x.Id).ToHashSet();
        var accessibleAccountIds = snapshot.Accounts.Select(x => x.Id).ToHashSet();

        var wealth = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["assetValuations"] = await QueryAsync("SELECT * FROM \"AssetValuations\" WHERE \"FullWorthSpaceId\"=@space ORDER BY \"AssetId\",\"ValuedAt\",\"CreatedAt\"", ct, ("@space", fullWorthSpaceId)),
            ["realEstateDetails"] = await QueryAssetScopedAsync("RealEstateAssetDetails", fullWorthSpaceId, ct),
            ["realEstateAcquisitionCosts"] = await QueryAssetScopedAsync("RealEstateAcquisitionCosts", fullWorthSpaceId, ct),
            ["assetDebtLinks"] = await QueryAsync("SELECT * FROM \"AssetDebtLinks\" WHERE \"FullWorthSpaceId\"=@space ORDER BY \"AssetId\",\"CreatedAt\"", ct, ("@space", fullWorthSpaceId)),
            ["propertyUnits"] = await QueryAsync("SELECT * FROM \"PropertyUnits\" WHERE \"FullWorthSpaceId\"=@space ORDER BY \"AssetId\",\"CreatedAt\"", ct, ("@space", fullWorthSpaceId)),
            ["rentalLeases"] = await QueryAsync("SELECT * FROM \"RentalLeases\" WHERE \"FullWorthSpaceId\"=@space ORDER BY \"AssetId\",\"StartDate\"", ct, ("@space", fullWorthSpaceId)),
            ["propertyImprovements"] = await QueryAssetScopedAsync("PropertyImprovements", fullWorthSpaceId, ct),
            ["propertyImprovementCashflows"] = await QueryAsync("SELECT l.* FROM \"PropertyImprovementCashflows\" l JOIN \"PropertyImprovements\" i ON i.\"Id\"=l.\"ImprovementId\" JOIN \"Assets\" a ON a.\"Id\"=i.\"AssetId\" WHERE a.\"FullWorthSpaceId\"=@space ORDER BY l.\"ImprovementId\",l.\"CashflowEntryId\"", ct, ("@space", fullWorthSpaceId)),
            ["assetRecurringContractLinks"] = await QueryAsync("SELECT * FROM \"AssetRecurringContractLinks\" WHERE \"FullWorthSpaceId\"=@space ORDER BY \"AssetId\",\"RecurringContractId\"", ct, ("@space", fullWorthSpaceId)),
            ["propertyEnergyCertificates"] = await QueryAsync("SELECT e.* FROM \"PropertyEnergyCertificates\" e JOIN \"Assets\" a ON a.\"Id\"=e.\"AssetId\" WHERE a.\"FullWorthSpaceId\"=@space ORDER BY e.\"AssetId\",e.\"CreatedAt\"", ct, ("@space", fullWorthSpaceId)),
            ["assetDocuments"] = await QueryAsync("SELECT \"Id\",\"FullWorthSpaceId\",\"AssetId\",\"Category\",\"OriginalFileName\",\"MediaType\",\"Sha256\",\"SizeBytes\",\"Notes\",\"CreatedByUserId\",\"CreatedAt\" FROM \"AssetDocuments\" WHERE \"FullWorthSpaceId\"=@space ORDER BY \"AssetId\",\"CreatedAt\"", ct, ("@space", fullWorthSpaceId)),
            ["vehicleDetails"] = await QueryAssetScopedAsync("VehicleAssetDetails", fullWorthSpaceId, ct),
            ["preciousMetalDetails"] = await QueryAssetScopedAsync("PreciousMetalAssetDetails", fullWorthSpaceId, ct),
            ["collectibleDetails"] = await QueryAssetScopedAsync("CollectibleAssetDetails", fullWorthSpaceId, ct),
            ["receivableDetails"] = await QueryAssetScopedAsync("ReceivableAssetDetails", fullWorthSpaceId, ct),
            ["businessInterestDetails"] = await QueryAssetScopedAsync("BusinessInterestAssetDetails", fullWorthSpaceId, ct),
            ["insurancePensionDetails"] = await QueryAssetScopedAsync("InsurancePensionAssetDetails", fullWorthSpaceId, ct),
            ["loans"] = await QueryAsync("SELECT * FROM \"Loans\" WHERE \"FullWorthSpaceId\"=@space ORDER BY \"Name\",\"Id\"", ct, ("@space", fullWorthSpaceId))
        };

        var cashflows = await QueryAsync("SELECT * FROM \"AssetCashflowEntries\" WHERE \"FullWorthSpaceId\"=@space ORDER BY \"AssetId\",\"Date\",\"CreatedAt\"", ct, ("@space", fullWorthSpaceId));
        MaskTransactionReferences(cashflows, accessibleTransactionIds);
        wealth["assetCashflows"] = cashflows;

        var payments = await QueryAsync("SELECT p.* FROM \"ReceivablePayments\" p JOIN \"Assets\" a ON a.\"Id\"=p.\"AssetId\" WHERE a.\"FullWorthSpaceId\"=@space ORDER BY p.\"AssetId\",p.\"Date\",p.\"CreatedAt\"", ct, ("@space", fullWorthSpaceId));
        MaskTransactionReferences(payments, accessibleTransactionIds);
        wealth["receivablePayments"] = payments;

        // Investments remain the existing canonical ledger. These rows are included for JSON/backup
        // portability; the existing XLSX export remains the canonical spreadsheet path.
        var portfolios = await QueryAsync("SELECT * FROM \"InvestmentPortfolios\" WHERE \"FullWorthSpaceId\"=@space ORDER BY \"Name\",\"Id\"", ct, ("@space", fullWorthSpaceId));
        portfolios = portfolios.Where(row => !TryGuid(row, "AccountId", out var accountId) || accessibleAccountIds.Contains(accountId)).ToList();
        var portfolioIds = portfolios.Select(row => TryGuid(row, "Id", out var id) ? id : Guid.Empty).Where(id => id != Guid.Empty).ToHashSet();
        wealth["investmentPortfolios"] = portfolios;
        var trades = await QueryForGuidSetAsync("InvestmentTrades", "PortfolioId", portfolioIds, ct);
        wealth["investmentTrades"] = trades;
        var securityIds = trades.Select(row => TryGuid(row, "SecurityId", out var id) ? id : Guid.Empty).Where(id => id != Guid.Empty).ToHashSet();
        foreach (var portfolio in portfolios)
            if (TryGuid(portfolio, "BenchmarkSecurityId", out var benchmark)) securityIds.Add(benchmark);
        wealth["securities"] = await QueryForGuidSetAsync("Securities", "Id", securityIds, ct);
        wealth["securityPrices"] = await QueryForGuidSetAsync("SecurityPrices", "SecurityId", securityIds, ct);

        return new WealthPortableExport(
            "fullworth-wealth-export-v1",
            1,
            DateTimeOffset.UtcNow,
            fullWorthSpaceId,
            snapshot,
            wealth,
            [],
            new { canonical = true, xlsx = "/api/export/xlsx-v2?includeInvestments=true", note = "Investment rows are exported from the existing canonical investment ledger." });
    }

    public async Task<WealthBackupResult?> BackupAsync(Guid userId, Guid fullWorthSpaceId, CancellationToken ct)
    {
        var export = await BuildAsync(userId, fullWorthSpaceId, ct);
        if (export is null) return null;
        var warnings = new List<string>();
        var documents = await ReadDocumentStorageRowsAsync(fullWorthSpaceId, ct);

        await using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var document in documents)
            {
                string absolute;
                try { absolute = SafeAbsolute(document.StoragePath); }
                catch
                {
                    warnings.Add($"Document {document.Id:D} has an invalid storage path and was not included.");
                    continue;
                }
                if (!File.Exists(absolute))
                {
                    warnings.Add($"Document {document.Id:D} is missing from storage and was not included.");
                    continue;
                }

                var extension = SafeExtension(document.MediaType);
                var entry = zip.CreateEntry($"documents/{document.AssetId:D}/{document.Id:D}{extension}", CompressionLevel.Optimal);
                await using var target = entry.Open();
                await using var source = File.OpenRead(absolute);
                await source.CopyToAsync(target, ct);
            }

            var manifest = export with { BackupWarnings = warnings };
            var jsonEntry = zip.CreateEntry("fullworth-wealth-export.json", CompressionLevel.Optimal);
            await using var jsonTarget = jsonEntry.Open();
            await JsonSerializer.SerializeAsync(jsonTarget, manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }, ct);
        }

        return new WealthBackupResult(output.ToArray(), $"fullworth-backup-{fullWorthSpaceId:D}-{DateTime.UtcNow:yyyy-MM-dd}.zip", warnings);
    }

    public async Task<WealthBackupValidation?> ValidateBackupAsync(Guid userId, Guid fullWorthSpaceId, Stream zipStream, CancellationToken ct)
    {
        if (await baseExport.SnapshotForUserAsync(userId, fullWorthSpaceId, ct) is null) return null;
        var errors = new List<string>();
        var warnings = new List<string>();
        Guid? manifestSpace = null;
        int? schemaVersion = null;
        var checkedDocuments = 0;

        try
        {
            using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);
            var manifestEntry = zip.GetEntry("fullworth-wealth-export.json");
            if (manifestEntry is null)
                return new(false, null, null, ["Backup manifest fullworth-wealth-export.json is missing."], [], 0);

            using var manifest = await JsonDocument.ParseAsync(manifestEntry.Open(), cancellationToken: ct);
            var root = manifest.RootElement;
            var format = JsonString(root, "format");
            schemaVersion = JsonInt(root, "schemaVersion");
            manifestSpace = JsonGuid(root, "fullWorthSpaceId");
            if (!string.Equals(format, "fullworth-wealth-export-v1", StringComparison.Ordinal)) errors.Add("Unsupported backup format.");
            if (schemaVersion != 1) errors.Add("Unsupported backup schema version.");
            if (manifestSpace != fullWorthSpaceId) errors.Add("Backup belongs to a different FullWorth Space.");

            if (!TryProperty(root, "wealth", out var wealth) || !TryProperty(wealth, "assetDocuments", out var documents) || documents.ValueKind != JsonValueKind.Array)
            {
                warnings.Add("Backup manifest does not contain asset-document metadata.");
            }
            else
            {
                foreach (var document in documents.EnumerateArray())
                {
                    var id = JsonGuid(document, "Id") ?? JsonGuid(document, "id");
                    var assetId = JsonGuid(document, "AssetId") ?? JsonGuid(document, "assetId");
                    var mediaType = JsonString(document, "MediaType") ?? JsonString(document, "mediaType");
                    var expectedSha = JsonString(document, "Sha256") ?? JsonString(document, "sha256");
                    if (!id.HasValue || !assetId.HasValue || string.IsNullOrWhiteSpace(mediaType) || string.IsNullOrWhiteSpace(expectedSha))
                    {
                        errors.Add("Asset-document metadata is incomplete.");
                        continue;
                    }
                    var path = $"documents/{assetId.Value:D}/{id.Value:D}{SafeExtension(mediaType)}";
                    var entry = zip.GetEntry(path);
                    if (entry is null)
                    {
                        errors.Add($"Document {id.Value:D} is missing from the backup archive.");
                        continue;
                    }
                    await using var documentStream = entry.Open();
                    var actualSha = Convert.ToHexString(await SHA256.HashDataAsync(documentStream, ct)).ToLowerInvariant();
                    if (!string.Equals(actualSha, expectedSha, StringComparison.OrdinalIgnoreCase))
                        errors.Add($"Document {id.Value:D} failed SHA-256 validation.");
                    checkedDocuments++;
                }
            }
        }
        catch (InvalidDataException)
        {
            errors.Add("Backup is not a valid ZIP archive.");
        }
        catch (JsonException)
        {
            errors.Add("Backup manifest is not valid JSON.");
        }

        return new(errors.Count == 0, manifestSpace, schemaVersion, errors, warnings, checkedDocuments);
    }

    private Task<List<Dictionary<string, object?>>> QueryAssetScopedAsync(string table, Guid fullWorthSpaceId, CancellationToken ct) =>
        QueryAsync($"SELECT t.* FROM \"{table}\" t JOIN \"Assets\" a ON a.\"Id\"=t.\"AssetId\" WHERE a.\"FullWorthSpaceId\"=@space ORDER BY t.\"AssetId\"", ct, ("@space", fullWorthSpaceId));

    private Task<List<Dictionary<string, object?>>> QueryForGuidSetAsync(string table, string column, IReadOnlyCollection<Guid> ids, CancellationToken ct) =>
        ids.Count == 0
            ? Task.FromResult(new List<Dictionary<string, object?>>())
            : QueryAsync($"SELECT * FROM \"{table}\" WHERE \"{column}\"=ANY(@ids) ORDER BY \"{column}\"", ct, ("@ids", ids.ToArray()));

    private async Task<List<Dictionary<string, object?>>> QueryAsync(string sql, CancellationToken ct, params (string Name, object? Value)[] parameters)
    {
        var result = new List<Dictionary<string, object?>>();
        var connection = db.Database.GetDbConnection();
        var closeWhenDone = connection.State != ConnectionState.Open;
        if (closeWhenDone) await connection.OpenAsync(ct);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var parameter in parameters) AddParameter(command, parameter.Name, parameter.Value);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var row = new Dictionary<string, object?>(StringComparer.Ordinal);
                for (var i = 0; i < reader.FieldCount; i++) row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                result.Add(row);
            }
        }
        finally { if (closeWhenDone) await connection.CloseAsync(); }
        return result;
    }

    private async Task<List<DocumentStorageRow>> ReadDocumentStorageRowsAsync(Guid fullWorthSpaceId, CancellationToken ct)
    {
        var rows = new List<DocumentStorageRow>();
        var connection = db.Database.GetDbConnection();
        var closeWhenDone = connection.State != ConnectionState.Open;
        if (closeWhenDone) await connection.OpenAsync(ct);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT \"Id\",\"AssetId\",\"StoragePath\",\"MediaType\" FROM \"AssetDocuments\" WHERE \"FullWorthSpaceId\"=@space ORDER BY \"AssetId\",\"CreatedAt\"";
            AddParameter(command, "@space", fullWorthSpaceId);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) rows.Add(new(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3)));
        }
        finally { if (closeWhenDone) await connection.CloseAsync(); }
        return rows;
    }

    private static void MaskTransactionReferences(IEnumerable<Dictionary<string, object?>> rows, IReadOnlySet<Guid> accessibleTransactions)
    {
        foreach (var row in rows)
            if (!TryGuid(row, "TransactionId", out var transactionId) || !accessibleTransactions.Contains(transactionId)) row["TransactionId"] = null;
    }

    private static bool TryGuid(IReadOnlyDictionary<string, object?> row, string key, out Guid id)
    {
        id = Guid.Empty;
        if (!row.TryGetValue(key, out var value)) return false;
        if (value is Guid guid) { id = guid; return id != Guid.Empty; }
        if (value is string text && Guid.TryParse(text, out var parsed)) { id = parsed; return id != Guid.Empty; }
        return false;
    }

    private string SafeAbsolute(string relative)
    {
        var root = Path.GetFullPath(storage.RootPath);
        var candidate = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.Ordinal)) throw new InvalidOperationException("Invalid document storage path.");
        return candidate;
    }

    private static string SafeExtension(string mediaType) => mediaType switch
    {
        "image/jpeg" => ".jpg",
        "image/png" => ".png",
        "image/webp" => ".webp",
        "application/pdf" => ".pdf",
        _ => ".bin"
    };

    private static bool TryProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject())
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) { value = property.Value; return true; }
        value = default;
        return false;
    }

    private static string? JsonString(JsonElement element, string name) =>
        TryProperty(element, name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static int? JsonInt(JsonElement element, string name) =>
        TryProperty(element, name, out var value) && value.TryGetInt32(out var number) ? number : null;
    private static Guid? JsonGuid(JsonElement element, string name) =>
        TryProperty(element, name, out var value) && value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out var guid) ? guid : null;

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private sealed record DocumentStorageRow(Guid Id, Guid AssetId, string StoragePath, string MediaType);
}

public static class WealthPortableExportEndpoints
{
    private const long MaxValidationBytes = 1L * 1024 * 1024 * 1024;

    public static IEndpointRouteBuilder MapWealthPortableExportEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/export/wealth-full", async (
            Guid fullWorthSpaceId,
            CurrentUserContext currentUser,
            WealthPortableExportService service,
            CancellationToken ct) =>
        {
            var export = await service.BuildAsync(currentUser.RequireUserId(), fullWorthSpaceId, ct);
            return export is null ? Results.NotFound() : Results.Ok(export);
        }).WithTags("Export");

        app.MapGet("/api/export/wealth-backup", async (
            Guid fullWorthSpaceId,
            CurrentUserContext currentUser,
            WealthPortableExportService service,
            CancellationToken ct) =>
        {
            var backup = await service.BackupAsync(currentUser.RequireUserId(), fullWorthSpaceId, ct);
            return backup is null ? Results.NotFound() : Results.File(backup.Bytes, "application/zip", backup.FileName);
        }).WithTags("Export");

        app.MapPost("/api/export/wealth-backup/validate", async (
            Guid fullWorthSpaceId,
            HttpRequest request,
            CurrentUserContext currentUser,
            WealthPortableExportService service,
            CancellationToken ct) =>
        {
            if (request.ContentLength is > MaxValidationBytes)
                return Results.BadRequest(new { error = "Backup is too large for in-app validation." });
            await using var buffer = new MemoryStream();
            await request.Body.CopyToAsync(buffer, ct);
            if (buffer.Length == 0) return Results.BadRequest(new { error = "Backup ZIP body is required." });
            buffer.Position = 0;
            var result = await service.ValidateBackupAsync(currentUser.RequireUserId(), fullWorthSpaceId, buffer, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        }).WithTags("Export");

        return app;
    }
}
