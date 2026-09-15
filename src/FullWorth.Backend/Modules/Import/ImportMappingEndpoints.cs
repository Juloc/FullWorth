using FullWorth.Backend.Validation;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Audit;
using FullWorth.Backend.Modules.Categories;
using FullWorth.Backend.Modules.Merchants;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Security;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Import;

public sealed record ImportColumnMapping(
    string Date, string Amount, string? Currency, string? Counterparty, string? Description,
    string? Account, string? Category, string? ExternalKey);
public sealed record ImportMappedCommitWrite(
    IReadOnlyDictionary<string, Guid?>? SourceAccountMappings,
    Guid? DefaultAccountId,
    IReadOnlyDictionary<string, Guid?>? CategoryMappings,
    bool CreateMissingCategories = false,
    bool RunFullWorthCategorization = true,
    IReadOnlyList<Guid>? CandidateIds = null);
// The duplicate check is per target account, so it can only run once the account mapping is known -
// i.e. not at upload time. This is the same input the commit takes, minus everything that writes.
public sealed record ImportDuplicatePreviewWrite(
    IReadOnlyDictionary<string, Guid?>? SourceAccountMappings,
    Guid? DefaultAccountId);

public static class ImportMappingEndpoints
{
    private const long MaxUploadBytes = 25L * 1024 * 1024;

    public static IEndpointRouteBuilder MapImportMappingEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/import-mapping").WithTags("Import");
        group.MapPost("/detect", Detect);
        group.MapPost("/upload", UploadMapped);
        group.MapGet("/jobs/{jobId:guid}/summary", MappingSummary);
        group.MapPost("/jobs/{jobId:guid}/duplicate-preview", PreviewDuplicates);
        group.MapPost("/jobs/{jobId:guid}/commit", CommitMapped);
        return app;
    }

    private static async Task<IResult> Detect(
        Guid fullWorthSpaceId, HttpRequest request, CurrentUserContext currentUser, SpaceAccess space, ImportMappingStore store, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await space.HasCapabilityAsync(userId, fullWorthSpaceId, "transactions.write", ct))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        var fileResult = await ReadFile(request, ct);
        if (fileResult.Error is not null) return Results.BadRequest(new { error = fileResult.Error });
        try
        {
            var rows = Parse(fileResult.FileName!, fileResult.Bytes!);
            if (rows.Count == 0) return Results.BadRequest(new { error = "No data rows found." });
            var headers = rows[0].Keys.ToArray();
            return Results.Ok(new
            {
                fileName = fileResult.FileName,
                headers,
                suggestedMapping = Suggest(headers),
                preview = rows.Take(10),
                rowCount = rows.Count
            });
        }
        catch (Exception exception) when (exception is InvalidDataException or FormatException)
        {
            return Results.BadRequest(new { error = exception.Message });
        }
    }

    private static async Task<IResult> UploadMapped(
        Guid fullWorthSpaceId, HttpRequest request, CurrentUserContext currentUser,
        SpaceAccess space, ImportMappingStore store, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await space.HasCapabilityAsync(userId, fullWorthSpaceId, "transactions.write", ct))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (!request.HasFormContentType) return Results.BadRequest(new { error = "Expected multipart/form-data." });
        var form = await request.ReadFormAsync(ct);
        var file = form.Files.GetFile("file");
        if (file is null || file.Length == 0 || file.Length > MaxUploadBytes) return Results.BadRequest(new { error = "Invalid import file." });
        ImportColumnMapping? mapping;
        try { mapping = JsonSerializer.Deserialize<ImportColumnMapping>(form["mapping"].ToString(), new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
        catch { return Results.BadRequest(new { error = "Invalid mapping JSON." }); }
        if (mapping is null || string.IsNullOrWhiteSpace(mapping.Date) || string.IsNullOrWhiteSpace(mapping.Amount))
            return Results.BadRequest(new { error = "Date and amount columns are required." });

        await using var stream = new MemoryStream(checked((int)file.Length));
        await file.CopyToAsync(stream, ct);
        var bytes = stream.ToArray();
        List<Dictionary<string,string>> rows;
        try { rows = Parse(file.FileName, bytes); }
        catch (Exception exception) when (exception is InvalidDataException or FormatException) { return Results.BadRequest(new { error = exception.Message }); }
        if (rows.Count == 0) return Results.BadRequest(new { error = "No data rows found." });
        var headers = rows[0].Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var referenced = new[] { mapping.Date, mapping.Amount, mapping.Currency, mapping.Counterparty, mapping.Description, mapping.Account, mapping.Category, mapping.ExternalKey }
            .Where(value => !string.IsNullOrWhiteSpace(value));
        if (referenced.Any(column => !headers.Contains(column!))) return Results.BadRequest(new { error = "Mapping references an unknown column." });

        var jobId = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        // A file without a currency column states no currency; the space's own base currency is the
        // honest reading of that, not a hardcoded EUR.
        var spaceCurrency = await store.BaseCurrencyAsync(fullWorthSpaceId, ct);
        var candidates = new List<MappedCandidate>(); var errorCount = 0;
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            try
            {
                var date = ParseDate(row.GetValueOrDefault(mapping.Date));
                var amount = ParseAmount(row.GetValueOrDefault(mapping.Amount));
                var currency = ParseCurrency(mapping.Currency is null ? null : row.GetValueOrDefault(mapping.Currency), spaceCurrency);
                var party = Clean(mapping.Counterparty is null ? null : row.GetValueOrDefault(mapping.Counterparty));
                var description = Clean(mapping.Description is null ? null : row.GetValueOrDefault(mapping.Description));
                var account = Clean(mapping.Account is null ? null : row.GetValueOrDefault(mapping.Account));
                var category = Clean(mapping.Category is null ? null : row.GetValueOrDefault(mapping.Category));
                var external = Clean(mapping.ExternalKey is null ? null : row.GetValueOrDefault(mapping.ExternalKey));
                candidates.Add(new(Guid.NewGuid(), account, date, amount, currency, party, description, category, external,
                    Fingerprint(date, amount, currency, party, description, external), "ready", null));
            }
            catch (Exception exception)
            {
                errorCount++;
                candidates.Add(new(Guid.NewGuid(), null, null, 0, spaceCurrency, null, null, null, null,
                    Fingerprint(null, 0, spaceCurrency, null, $"row-{index}", null), "error", exception.Message));
            }
        }

        await store.CreateJobAsync(
            userId, fullWorthSpaceId, jobId, Path.GetFileName(file.FileName), sha,
            Path.GetExtension(file.FileName).Equals(".csv", StringComparison.OrdinalIgnoreCase) ? "mapped_csv" : "mapped_xlsx",
            candidates, errorCount, ct);

        return Results.Ok(new { jobId, sourceRows = candidates.Count, ready = candidates.Count-errorCount, errors = errorCount });
    }

    private static async Task<IResult> MappingSummary(
        Guid jobId, Guid fullWorthSpaceId, CurrentUserContext currentUser, SpaceAccess space, ImportMappingStore store, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await store.OwnsOpenJobAsync(jobId, fullWorthSpaceId, userId, ct)) return Results.NotFound();
        var accounts = await store.SourceAccountCountsAsync(jobId, ct);
        var categories = await store.SourceCategoryCountsAsync(jobId, ct);
        return Results.Ok(new
        {
            sourceAccounts = accounts.Select(row => new { source = row.Source, count = row.Count }),
            sourceCategories = categories.Select(row => new { source = row.Source, count = row.Count })
        });
    }

    private static async Task<IResult> CommitMapped(
        Guid jobId, Guid fullWorthSpaceId, ImportMappedCommitWrite request, CurrentUserContext currentUser,
        SpaceAccess space, ImportMappingStore store, ImportMappingCommitService commit, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await store.OwnsOpenJobAsync(jobId, fullWorthSpaceId, userId, ct)) return Results.NotFound();
        if (!await space.HasCapabilityAsync(userId, fullWorthSpaceId, "transactions.write", ct))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        var writable = await space.WritableAccountIdsAsync(userId, fullWorthSpaceId, ct);
        var accountMap = request.SourceAccountMappings ?? new Dictionary<string, Guid?>();
        var allMappedIds = accountMap.Values.Where(value => value.HasValue).Select(value => value!.Value)
            .Concat(request.DefaultAccountId.HasValue ? [request.DefaultAccountId.Value] : []).Distinct().ToArray();
        if (allMappedIds.Any(id => !writable.Contains(id))) return Results.BadRequest(new { error = "An account mapping is inaccessible." });
        var categoryMap = request.CategoryMappings ?? new Dictionary<string, Guid?>();
        var mappedCategories = categoryMap.Values.Where(value => value.HasValue).Select(value => value!.Value).Distinct().ToArray();
        if (mappedCategories.Length > 0 && await store.CountCategoriesAsync(fullWorthSpaceId, mappedCategories, ct) != mappedCategories.Length)
            return Results.BadRequest(new { error = "A category mapping is invalid." });

        var selected = request.CandidateIds?.ToHashSet();
        var candidates = await store.ReadCandidatesAsync(jobId, ct);
        if (selected is not null) candidates = candidates.Where(candidate => selected.Contains(candidate.Id)).ToList();
        candidates = candidates.Where(candidate => candidate.Status == "ready" && candidate.Date.HasValue).ToList();
        var roleOwner = await space.IsOwnerAsync(userId, fullWorthSpaceId, ct);
        if (request.CreateMissingCategories && !roleOwner) return Results.StatusCode(StatusCodes.Status403Forbidden);
        // Derselbe Klassifizierer wie in der Vorschau, damit die Pruefliste und das, was der
        // Import tatsaechlich ueberspringt, nie auseinanderlaufen koennen.
        var classifications = (await ClassifyAsync(store, candidates, accountMap, request.DefaultAccountId, ct))
            .ToDictionary(entry => entry.CandidateId);

        var outcome = await commit.CommitAsync(
            userId, fullWorthSpaceId, jobId, candidates, classifications, categoryMap,
            request.CreateMissingCategories, request.RunFullWorthCategorization, ct);

        return Results.Ok(new
        {
            imported = outcome.Imported,
            duplicates = outcome.Duplicates,
            skipped = outcome.Skipped,
            total = outcome.Total
        });
    }


    private static async Task<IResult> PreviewDuplicates(
        Guid jobId, Guid fullWorthSpaceId, ImportDuplicatePreviewWrite request, CurrentUserContext currentUser,
        SpaceAccess space, ImportMappingStore store, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await store.OwnsOpenJobAsync(jobId, fullWorthSpaceId, userId, ct)) return Results.NotFound();
        if (!await space.HasCapabilityAsync(userId, fullWorthSpaceId, "transactions.write", ct))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        var writable = await space.WritableAccountIdsAsync(userId, fullWorthSpaceId, ct);
        var accountMap = request.SourceAccountMappings ?? new Dictionary<string, Guid?>();
        var allMappedIds = accountMap.Values.Where(value => value.HasValue).Select(value => value!.Value)
            .Concat(request.DefaultAccountId.HasValue ? [request.DefaultAccountId.Value] : []).Distinct().ToArray();
        if (allMappedIds.Any(id => !writable.Contains(id))) return Results.BadRequest(new { error = "An account mapping is inaccessible." });

        var candidates = (await store.ReadCandidatesAsync(jobId, ct))
            .Where(candidate => candidate.Status == "ready" && candidate.Date.HasValue).ToList();
        var classifications = await ClassifyAsync(store, candidates, accountMap, request.DefaultAccountId, ct);
        // Nothing is written here: the same rows classified against a different account mapping get a
        // different answer, so persisting this would make the stored status a guess about the future.
        return Results.Ok(new
        {
            candidates = classifications.Select(entry => new { id = entry.CandidateId, status = entry.Status, reason = entry.Reason }),
            duplicates = classifications.Count(entry => entry.Status == "duplicate"),
            unmapped = classifications.Count(entry => entry.Status == "unmapped"),
            fresh = classifications.Count(entry => entry.Status == "new")
        });
    }

    // "in_file": an earlier row of this very file already carries the key.
    // "external_key": the source system's own booking id is already stored on that account.
    // "existing": same account, date, amount, currency and normalised counterparty.
    private static async Task<List<CandidateClassification>> ClassifyAsync(
        ImportMappingStore store, IReadOnlyList<MappedCandidate> candidates,
        IReadOnlyDictionary<string, Guid?> accountMap, Guid? defaultAccountId, CancellationToken ct)
    {
        var result = new List<CandidateClassification>(candidates.Count);
        var seenImportKeys = new HashSet<(Guid AccountId, string ExternalKey)>();
        var seenSemanticKeys = new HashSet<string>(StringComparer.Ordinal);

        // Einmal fuer alle betroffenen Konten laden statt zweimal je Zeile: bei einer Datei mit 5000
        // Zeilen waren das 10 000 Runden zur Datenbank.
        var targetAccounts = candidates
            .Select(candidate => accountMap.TryGetValue(candidate.SourceAccount ?? "", out var mapped) ? mapped : defaultAccountId)
            .Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToArray();
        var (existingExternalKeys, existingSemantic) = await store.ExistingKeysAsync(targetAccounts, ct);
        foreach (var candidate in candidates)
        {
            var sourceKey = candidate.SourceAccount ?? "";
            Guid? accountId = accountMap.TryGetValue(sourceKey, out var explicitAccount) ? explicitAccount : defaultAccountId;
            if (!accountId.HasValue || candidate.Status != "ready" || !candidate.Date.HasValue)
            {
                result.Add(new(candidate.Id, null, "", null, "unmapped", null));
                continue;
            }
            var normalized = MerchantNormalization.Normalize(candidate.Counterparty);
            var external = StableExternalKey(candidate);
            var semanticKey = SemanticKey(accountId.Value, candidate.Date.Value, candidate.Amount, candidate.Currency, normalized);
            var reason = !seenImportKeys.Add((accountId.Value, external)) || !seenSemanticKeys.Add(semanticKey) ? "in_file" : null;
            if (reason is null && existingExternalKeys.Contains((accountId.Value, external))) reason = "external_key";
            if (reason is null && existingSemantic.Contains(
                    (accountId.Value, candidate.Date.Value, candidate.Amount, candidate.Currency, normalized)))
                reason = "existing";
            result.Add(new(candidate.Id, accountId, external, normalized, reason is null ? "new" : "duplicate", reason));
        }
        return result;
    }

    private static async Task<(byte[]? Bytes,string? FileName,string? Error)> ReadFile(HttpRequest request,CancellationToken ct)
    {if(!request.HasFormContentType)return(null,null,"Expected multipart/form-data.");var form=await request.ReadFormAsync(ct);var file=form.Files.GetFile("file");if(file is null||file.Length==0)return(null,null,"No file uploaded.");if(file.Length>MaxUploadBytes)return(null,null,"Maximum file size is 25 MB.");if(Path.GetExtension(file.FileName).ToLowerInvariant() is not(".csv" or ".xlsx"))return(null,null,"Supported formats are CSV and XLSX.");await using var ms=new MemoryStream(checked((int)file.Length));await file.CopyToAsync(ms,ct);return(ms.ToArray(),Path.GetFileName(file.FileName),null);}
    private static List<Dictionary<string,string>> Parse(string fileName,byte[] bytes)=>Path.GetExtension(fileName).Equals(".csv",StringComparison.OrdinalIgnoreCase)?ParseCsv(bytes):ParseXlsx(bytes);
    private static ImportColumnMapping Suggest(IEnumerable<string> headers){var h=headers.ToArray();string? Find(params string[] names)=>h.FirstOrDefault(x=>names.Any(n=>Norm(x)==Norm(n)));return new(Find("date","datum","booking date","buchungsdatum")??"",Find("amount","betrag","value","umsatz")??"",Find("currency","währung","waehrung"),Find("counterparty","empfänger","empfaenger","payee","merchant","gegenpartei"),Find("description","verwendungszweck","text","purpose","memo"),Find("account","konto","account name","referenzkonto"),Find("category","kategorie"),Find("id","booking id","transaction id","buchungs-id"));}
    private static string Norm(string value)=>new(value.Trim().ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
    private static string? Clean(string? value)=>string.IsNullOrWhiteSpace(value)?null:value.Trim();
    /// <summary>
    /// Same rule as ImportParityModule.RowCurrency: a mapped currency column that IS present but
    /// unreadable makes the row an error the user can see, instead of being relabelled to a currency the
    /// file never stated. A file with no currency column falls back to the space base currency.
    /// </summary>
    private static string ParseCurrency(string? value, string fallback)
    {
        var v = Clean(value)?.ToUpperInvariant();
        if (v is null) return fallback;
        if (v is { Length: 3 } && v.All(char.IsAsciiLetterUpper)) return v;
        throw new FormatException($"Unknown currency '{value!.Trim()}'.");
    }
    // The culture-dependent fallback this used to end with read a German 03.04.2026 as 4 March on any
    // host that was not de-DE - including the invariant culture a container runs with. Spreadsheets also
    // store a date as a day count, which stays supported here. See ImportDate.
    private static DateOnly ParseDate(string? value)=>ImportDate.Parse(value,allowExcelSerial:true);
    // Statement amounts, so three trailing digits after a single separator mean grouping - see ImportNumber.
    private static decimal ParseAmount(string? value)=>ImportNumber.Parse(value,ImportNumber.ThreeDigitTail.Grouping);
    private static string Fingerprint(DateOnly? date,decimal amount,string currency,string? party,string? description,string? external)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{date:yyyy-MM-dd}|{amount}|{currency}|{party}|{description}|{external}"))).ToLowerInvariant();
    private static string StableExternalKey(MappedCandidate candidate)=>!string.IsNullOrWhiteSpace(candidate.ExternalKey)?$"mapped-import:external:{Sha256(candidate.ExternalKey.Trim())}":$"mapped-import:fingerprint:{candidate.Fingerprint}";
    private static string SemanticKey(Guid accountId,DateOnly date,decimal amount,string currency,string? normalizedParty)=>$"{accountId:N}|{date:yyyy-MM-dd}|{amount.ToString(CultureInfo.InvariantCulture)}|{currency.ToUpperInvariant()}|{normalizedParty}";
    private static string Sha256(string value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static List<Dictionary<string,string>> ParseCsv(byte[] bytes){var text=Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');var records=SplitCsvRecords(text);if(records.Count<2)return[];var delimiter=GuessDelimiter(records[0]);var header=ParseCsvLine(records[0],delimiter);return records.Skip(1).Where(line=>!string.IsNullOrWhiteSpace(line)).Select(line=>{var cells=ParseCsvLine(line,delimiter);var row=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);for(var i=0;i<header.Count;i++)row[header[i]]=i<cells.Count?cells[i]:"";return row;}).ToList();}
    private static char GuessDelimiter(string line)=>new[]{';',',','\t'}.OrderByDescending(c=>line.Count(x=>x==c)).First();
    private static List<string> SplitCsvRecords(string text){var rows=new List<string>();var sb=new StringBuilder();var quoted=false;for(var i=0;i<text.Length;i++){var ch=text[i];if(ch=='\"'){if(quoted&&i+1<text.Length&&text[i+1]=='\"'){sb.Append("\"\"");i++;continue;}quoted=!quoted;sb.Append(ch);}else if((ch=='\n'||ch=='\r')&&!quoted){if(ch=='\r'&&i+1<text.Length&&text[i+1]=='\n')i++;rows.Add(sb.ToString());sb.Clear();}else sb.Append(ch);}if(sb.Length>0)rows.Add(sb.ToString());return rows;}
    private static List<string> ParseCsvLine(string line,char delimiter){var cells=new List<string>();var sb=new StringBuilder();var quoted=false;for(var i=0;i<line.Length;i++){var ch=line[i];if(ch=='\"'){if(quoted&&i+1<line.Length&&line[i+1]=='\"'){sb.Append('\"');i++;}else quoted=!quoted;}else if(ch==delimiter&&!quoted){cells.Add(sb.ToString());sb.Clear();}else sb.Append(ch);}cells.Add(sb.ToString());return cells;}
    private static List<Dictionary<string,string>> ParseXlsx(byte[] bytes){using var ms=new MemoryStream(bytes);using var zip=new ZipArchive(ms,ZipArchiveMode.Read);var shared=ReadSharedStrings(zip);var sheet=zip.GetEntry("xl/worksheets/sheet1.xml")??throw new InvalidDataException("XLSX has no first worksheet.");using var stream=sheet.Open();var doc=XDocument.Load(stream);XNamespace ns="http://schemas.openxmlformats.org/spreadsheetml/2006/main";var rows=doc.Descendants(ns+"row").Select(r=>ReadXlsxRow(r,ns,shared)).ToList();if(rows.Count<2)return[];var header=rows[0];var result=new List<Dictionary<string,string>>();foreach(var cells in rows.Skip(1)){var row=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);for(var i=0;i<header.Count;i++)row[header[i]]=i<cells.Count?cells[i]:"";result.Add(row);}return result;}
    private static List<string> ReadSharedStrings(ZipArchive zip){var entry=zip.GetEntry("xl/sharedStrings.xml");if(entry is null)return[];using var s=entry.Open();var doc=XDocument.Load(s);XNamespace ns="http://schemas.openxmlformats.org/spreadsheetml/2006/main";return doc.Descendants(ns+"si").Select(si=>string.Concat(si.Descendants(ns+"t").Select(t=>t.Value))).ToList();}
    private static List<string> ReadXlsxRow(XElement row,XNamespace ns,IReadOnlyList<string> shared){var values=new SortedDictionary<int,string>();foreach(var cell in row.Elements(ns+"c")){var reference=(string?)cell.Attribute("r")??"A1";var column=ColumnIndex(reference);var type=(string?)cell.Attribute("t");var value=type=="inlineStr"?string.Concat(cell.Descendants(ns+"t").Select(t=>t.Value)):cell.Element(ns+"v")?.Value??"";if(type=="s"&&int.TryParse(value,out var si)&&si>=0&&si<shared.Count)value=shared[si];values[column]=value;}var max=values.Count==0?-1:values.Keys.Max();return Enumerable.Range(0,max+1).Select(i=>values.GetValueOrDefault(i,"")).ToList();}
    private static int ColumnIndex(string reference){var letters=new string(reference.TakeWhile(char.IsLetter).ToArray()).ToUpperInvariant();var n=0;foreach(var ch in letters)n=n*26+(ch-'A'+1);return Math.Max(0,n-1);}
}