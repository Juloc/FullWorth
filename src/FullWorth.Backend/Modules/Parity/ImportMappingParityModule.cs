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

namespace FullWorth.Backend.Modules.Parity;

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

public static class ImportMappingParityEndpoints
{
    private const long MaxUploadBytes = 25L * 1024 * 1024;

    public static IEndpointRouteBuilder MapImportMappingParityEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/import-mapping").WithTags("Import");
        group.MapPost("/detect", Detect);
        group.MapPost("/upload", UploadMapped);
        group.MapGet("/jobs/{jobId:guid}/summary", MappingSummary);
        group.MapPost("/jobs/{jobId:guid}/commit", CommitMapped);
        return app;
    }

    private static async Task<IResult> Detect(
        Guid fullWorthSpaceId, HttpRequest request, CurrentUserContext currentUser, FullWorthDbContext db, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await PermissionsErgonomicsParityEndpoints.HasCapabilityAsync(db, userId, fullWorthSpaceId, "transactions.write", ct))
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
        FullWorthDbContext db, AuditService audit, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await PermissionsErgonomicsParityEndpoints.HasCapabilityAsync(db, userId, fullWorthSpaceId, "transactions.write", ct))
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
        var candidates = new List<MappedCandidate>(); var errorCount = 0;
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            try
            {
                var date = ParseDate(row.GetValueOrDefault(mapping.Date));
                var amount = ParseAmount(row.GetValueOrDefault(mapping.Amount));
                var currency = ParseCurrency(mapping.Currency is null ? null : row.GetValueOrDefault(mapping.Currency));
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
                candidates.Add(new(Guid.NewGuid(), null, null, 0, "EUR", null, null, null, null,
                    Fingerprint(null, 0, "EUR", null, $"row-{index}", null), "error", exception.Message));
            }
        }

        var connection = await ParitySql.OpenAsync(db, ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await using (var command = ParitySql.Command(connection, """
INSERT INTO "ImportJobs" ("Id","FullWorthSpaceId","UserId","FileName","FileSha256","AdapterKey","Status","SourceRowCount","ReadyCount","DuplicateCount","ImportedCount","ErrorCount","CreatedAt","UpdatedAt")
VALUES (@id,@space,@user,@file,@sha,@adapter,@status,@source,@ready,0,0,@errors,@now,@now)
""", ("@id", jobId), ("@space", fullWorthSpaceId), ("@user", userId), ("@file", Path.GetFileName(file.FileName)),
            ("@sha", sha), ("@adapter", Path.GetExtension(file.FileName).Equals(".csv", StringComparison.OrdinalIgnoreCase) ? "mapped_csv" : "mapped_xlsx"),
            ("@status", errorCount == candidates.Count ? "failed" : "mapping_required"), ("@source", candidates.Count),
            ("@ready", candidates.Count-errorCount), ("@errors", errorCount), ("@now", now))) await command.ExecuteNonQueryAsync(ct);
        foreach (var candidate in candidates)
        {
            await using var command = ParitySql.Command(connection, """
INSERT INTO "ImportCandidates" ("Id","ImportJobId","SourceAccount","BookingDate","Amount","Currency","Counterparty","Description","CategoryText","ExternalKey","RowFingerprint","DuplicateStatus","ValidationStatus","ValidationError")
VALUES (@id,@job,@account,@date,@amount,@currency,@party,@description,@category,@external,@fingerprint,'new',@status,@error)
""", ("@id", candidate.Id), ("@job", jobId), ("@account", candidate.SourceAccount), ("@date", candidate.Date),
                ("@amount", candidate.Amount), ("@currency", candidate.Currency), ("@party", candidate.Counterparty),
                ("@description", candidate.Description), ("@category", candidate.Category), ("@external", candidate.ExternalKey),
                ("@fingerprint", candidate.Fingerprint), ("@status", candidate.Status), ("@error", candidate.Error));
            await command.ExecuteNonQueryAsync(ct);
        }
        audit.Record(fullWorthSpaceId, userId, "import.mapped.uploaded", "ImportJob", jobId);
        await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
        return Results.Ok(new { jobId, sourceRows = candidates.Count, ready = candidates.Count-errorCount, errors = errorCount });
    }

    private static async Task<IResult> MappingSummary(
        Guid jobId, Guid fullWorthSpaceId, CurrentUserContext currentUser, FullWorthDbContext db, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await OwnJob(db, jobId, fullWorthSpaceId, userId, ct)) return Results.NotFound();
        var connection = await ParitySql.OpenAsync(db, ct);
        var accounts = new List<object>();
        await using (var command = ParitySql.Command(connection, """
SELECT COALESCE("SourceAccount",''),count(*) FROM "ImportCandidates"
WHERE "ImportJobId"=@job AND "ValidationStatus"='ready' GROUP BY COALESCE("SourceAccount",'') ORDER BY count(*) DESC
""", ("@job", jobId)))
        { await using var reader = await command.ExecuteReaderAsync(ct); while(await reader.ReadAsync(ct)) accounts.Add(new { source = reader.GetString(0), count = reader.GetInt64(1) }); }
        var categories = new List<object>();
        await using (var command = ParitySql.Command(connection, """
SELECT COALESCE("CategoryText",''),count(*) FROM "ImportCandidates"
WHERE "ImportJobId"=@job AND "ValidationStatus"='ready' GROUP BY COALESCE("CategoryText",'') ORDER BY count(*) DESC
""", ("@job", jobId)))
        { await using var reader = await command.ExecuteReaderAsync(ct); while(await reader.ReadAsync(ct)) categories.Add(new { source = reader.GetString(0), count = reader.GetInt64(1) }); }
        return Results.Ok(new { sourceAccounts = accounts, sourceCategories = categories });
    }

    private static async Task<IResult> CommitMapped(
        Guid jobId, Guid fullWorthSpaceId, ImportMappedCommitWrite request, CurrentUserContext currentUser,
        FullWorthDbContext db, FieldCipher cipher, AuditService audit, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await OwnJob(db, jobId, fullWorthSpaceId, userId, ct)) return Results.NotFound();
        if (!await PermissionsErgonomicsParityEndpoints.HasCapabilityAsync(db, userId, fullWorthSpaceId, "transactions.write", ct))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        var writable = await ParitySql.WritableAccountIdsAsync(db, userId, fullWorthSpaceId, ct);
        var accountMap = request.SourceAccountMappings ?? new Dictionary<string, Guid?>();
        var allMappedIds = accountMap.Values.Where(value => value.HasValue).Select(value => value!.Value)
            .Concat(request.DefaultAccountId.HasValue ? [request.DefaultAccountId.Value] : []).Distinct().ToArray();
        if (allMappedIds.Any(id => !writable.Contains(id))) return Results.BadRequest(new { error = "An account mapping is inaccessible." });
        var categoryMap = request.CategoryMappings ?? new Dictionary<string, Guid?>();
        var mappedCategories = categoryMap.Values.Where(value => value.HasValue).Select(value => value!.Value).Distinct().ToArray();
        if (mappedCategories.Length > 0 && await db.Categories.AsNoTracking().CountAsync(c => c.FullWorthSpaceId == fullWorthSpaceId && mappedCategories.Contains(c.Id), ct) != mappedCategories.Length)
            return Results.BadRequest(new { error = "A category mapping is invalid." });

        var selected = request.CandidateIds?.ToHashSet();
        var candidates = await ReadCandidates(db, jobId, ct);
        if (selected is not null) candidates = candidates.Where(candidate => selected.Contains(candidate.Id)).ToList();
        candidates = candidates.Where(candidate => candidate.Status == "ready" && candidate.Date.HasValue).ToList();
        var roleOwner = await ParitySql.IsOwnerAsync(db, userId, fullWorthSpaceId, ct);
        if (request.CreateMissingCategories && !roleOwner) return Results.StatusCode(StatusCodes.Status403Forbidden);
        var existingCategories = await db.Categories.Where(c => c.FullWorthSpaceId == fullWorthSpaceId).ToListAsync(ct);
        var ruleList = request.RunFullWorthCategorization
            ? await db.CategorizationRules.AsNoTracking().Where(r => r.FullWorthSpaceId == fullWorthSpaceId && r.IsEnabled && r.Target == "transaction").OrderBy(r => r.Priority).ToListAsync(ct)
            : [];
        var categoryIdsByKey = existingCategories.Where(c => !c.IsArchived).ToDictionary(c => c.Key, c => c.Id, StringComparer.OrdinalIgnoreCase);

        var imported=0;var duplicates=0;var skipped=0;
        var seenImportKeys = new HashSet<(Guid AccountId,string ExternalKey)>();
        var seenSemanticKeys = new HashSet<string>(StringComparer.Ordinal);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        foreach (var candidate in candidates)
        {
            var sourceKey = candidate.SourceAccount ?? "";
            Guid? accountId = accountMap.TryGetValue(sourceKey, out var explicitAccount) ? explicitAccount : request.DefaultAccountId;
            if (!accountId.HasValue) { skipped++; continue; }
            var account = await db.Accounts.AsNoTracking().SingleAsync(a => a.Id == accountId.Value, ct);
            var normalized = MerchantNormalization.Normalize(candidate.Counterparty);
            var external = StableExternalKey(candidate);
            var semanticKey = SemanticKey(account.Id,candidate.Date!.Value,candidate.Amount,candidate.Currency,normalized);
            var duplicateInBatch = !seenImportKeys.Add((account.Id,external)) || !seenSemanticKeys.Add(semanticKey);
            var stableDuplicate = duplicateInBatch || await db.Transactions.AsNoTracking().AnyAsync(t =>
                t.AccountId == account.Id && t.ExternalKey == external, ct);
            var semanticDuplicate = stableDuplicate || await db.Transactions.AsNoTracking().AnyAsync(t => t.AccountId == account.Id &&
                (t.BookingDate ?? t.ValueDate) == candidate.Date && t.Amount == candidate.Amount && t.Currency == candidate.Currency &&
                t.NormalizedCounterparty == normalized, ct);
            if (semanticDuplicate) { duplicates++; await MarkCandidate(db,candidate.Id,"duplicate",ct); continue; }

            Guid? categoryId = null;
            var categorySource = "none";
            if (!string.IsNullOrWhiteSpace(candidate.Category))
            {
                if (categoryMap.TryGetValue(candidate.Category, out var mapped)) categoryId = mapped;
                else categoryId = existingCategories.FirstOrDefault(c => string.Equals(c.Name, candidate.Category, StringComparison.OrdinalIgnoreCase))?.Id;
                if (!categoryId.HasValue && request.CreateMissingCategories)
                {
                    var hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{fullWorthSpaceId:N}|{candidate.Category.ToUpperInvariant()}"))).ToLowerInvariant();
                    var created=new FinanceCategory{FullWorthSpaceId=fullWorthSpaceId,Key=$"import-{hash[..24]}",Name=candidate.Category.Trim(),IsSystem=false,IsArchived=false,SortOrder=0,CreatedAt=DateTimeOffset.UtcNow};
                    db.Categories.Add(created);existingCategories.Add(created);categoryIdsByKey[created.Key]=created.Id;categoryId=created.Id;
                }
                if(categoryId.HasValue)categorySource="import";
            }
            var entity=new FinanceTransaction{AccountId=account.Id,CategoryId=categoryId,ExternalKey=external,Status="BOOK",BookingDate=candidate.Date,ValueDate=candidate.Date,Amount=candidate.Amount,Currency=candidate.Currency,Counterparty=candidate.Counterparty,NormalizedCounterparty=normalized,Description=candidate.Description,CategorizationSource=categorySource,RawJson=cipher.Protect("{\"source\":\"mapped-import\"}")??"{}",FirstSeenAt=DateTimeOffset.UtcNow,UpdatedAt=DateTimeOffset.UtcNow};
            if(!categoryId.HasValue&&request.RunFullWorthCategorization)
            {
                var evaluation=TransactionRuleEngine.EvaluateWithGermanyCatalog(entity,ruleList,categoryIdsByKey);
                entity.CategoryId=evaluation.CategoryId;entity.IsTransfer=evaluation.MarkAsTransfer;entity.CategorizationSource=evaluation.CategoryId.HasValue?evaluation.Source:"none";
            }
            db.Transactions.Add(entity);imported++;await MarkCandidate(db,candidate.Id,"imported",ct);
        }
        await db.SaveChangesAsync(ct);
        var connection=await ParitySql.OpenAsync(db,ct);await using(var command=ParitySql.Command(connection,"UPDATE \"ImportJobs\" SET \"Status\"='completed',\"ImportedCount\"=@imported,\"DuplicateCount\"=@duplicates,\"UpdatedAt\"=@now,\"CompletedAt\"=@now WHERE \"Id\"=@id",("@imported",imported),("@duplicates",duplicates),("@now",DateTimeOffset.UtcNow),("@id",jobId)))await command.ExecuteNonQueryAsync(ct);
        audit.Record(fullWorthSpaceId,userId,"import.mapped.completed","ImportJob",jobId);await db.SaveChangesAsync(ct);await transaction.CommitAsync(ct);
        return Results.Ok(new{imported,duplicates,skipped,total=candidates.Count});
    }

    private static async Task<(byte[]? Bytes,string? FileName,string? Error)> ReadFile(HttpRequest request,CancellationToken ct)
    {if(!request.HasFormContentType)return(null,null,"Expected multipart/form-data.");var form=await request.ReadFormAsync(ct);var file=form.Files.GetFile("file");if(file is null||file.Length==0)return(null,null,"No file uploaded.");if(file.Length>MaxUploadBytes)return(null,null,"Maximum file size is 25 MB.");if(Path.GetExtension(file.FileName).ToLowerInvariant() is not(".csv" or ".xlsx"))return(null,null,"Supported formats are CSV and XLSX.");await using var ms=new MemoryStream(checked((int)file.Length));await file.CopyToAsync(ms,ct);return(ms.ToArray(),Path.GetFileName(file.FileName),null);}
    private static List<Dictionary<string,string>> Parse(string fileName,byte[] bytes)=>Path.GetExtension(fileName).Equals(".csv",StringComparison.OrdinalIgnoreCase)?ParseCsv(bytes):ParseXlsx(bytes);
    private static ImportColumnMapping Suggest(IEnumerable<string> headers){var h=headers.ToArray();string? Find(params string[] names)=>h.FirstOrDefault(x=>names.Any(n=>Norm(x)==Norm(n)));return new(Find("date","datum","booking date","buchungsdatum")??"",Find("amount","betrag","value","umsatz")??"",Find("currency","währung","waehrung"),Find("counterparty","empfänger","empfaenger","payee","merchant","gegenpartei"),Find("description","verwendungszweck","text","purpose","memo"),Find("account","konto","account name","referenzkonto"),Find("category","kategorie"),Find("id","booking id","transaction id","buchungs-id"));}
    private static string Norm(string value)=>new(value.Trim().ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
    private static string? Clean(string? value)=>string.IsNullOrWhiteSpace(value)?null:value.Trim();
    private static string ParseCurrency(string? value){var v=Clean(value)?.ToUpperInvariant();return v is{Length:3}&&v.All(char.IsLetter)?v:"EUR";}
    private static DateOnly ParseDate(string? value){if(string.IsNullOrWhiteSpace(value))throw new FormatException("Date is missing.");var s=value.Trim();if(double.TryParse(s,NumberStyles.Float,CultureInfo.InvariantCulture,out var serial)&&serial is >20000 and <100000)return DateOnly.FromDateTime(new DateTime(1899,12,30).AddDays(serial));var formats=new[]{"yyyy-MM-dd","dd.MM.yyyy","d.M.yyyy","dd/MM/yyyy","MM/dd/yyyy","yyyy/MM/dd"};foreach(var f in formats)if(DateOnly.TryParseExact(s,f,CultureInfo.InvariantCulture,DateTimeStyles.None,out var d))return d;if(DateOnly.TryParse(s,CultureInfo.CurrentCulture,out var parsed))return parsed;throw new FormatException($"Invalid date '{value}'.");}
    private static decimal ParseAmount(string? value){if(string.IsNullOrWhiteSpace(value))throw new FormatException("Amount is missing.");var s=value.Trim().Replace("€","").Replace(" ","").Replace("'","");if(decimal.TryParse(s,NumberStyles.Number|NumberStyles.AllowLeadingSign,CultureInfo.GetCultureInfo("de-DE"),out var de))return de;if(decimal.TryParse(s,NumberStyles.Number|NumberStyles.AllowLeadingSign,CultureInfo.InvariantCulture,out var invariant))return invariant;throw new FormatException($"Invalid amount '{value}'.");}
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

    private sealed record MappedCandidate(Guid Id,string? SourceAccount,DateOnly? Date,decimal Amount,string Currency,string? Counterparty,string? Description,string? Category,string? ExternalKey,string Fingerprint,string Status,string? Error);
    private static async Task<bool> OwnJob(FullWorthDbContext db,Guid jobId,Guid space,Guid user,CancellationToken ct){var c=await ParitySql.OpenAsync(db,ct);await using var cmd=ParitySql.Command(c,"SELECT EXISTS(SELECT 1 FROM \"ImportJobs\" WHERE \"Id\"=@id AND \"FullWorthSpaceId\"=@space AND \"UserId\"=@user AND \"Status\" NOT IN ('completed','cancelled'))",("@id",jobId),("@space",space),("@user",user));return Convert.ToBoolean(await cmd.ExecuteScalarAsync(ct));}
    private static async Task<List<MappedCandidate>> ReadCandidates(FullWorthDbContext db,Guid jobId,CancellationToken ct){var c=await ParitySql.OpenAsync(db,ct);await using var cmd=ParitySql.Command(c,"SELECT \"Id\",\"SourceAccount\",\"BookingDate\",\"Amount\",\"Currency\",\"Counterparty\",\"Description\",\"CategoryText\",\"ExternalKey\",\"RowFingerprint\",\"ValidationStatus\",\"ValidationError\" FROM \"ImportCandidates\" WHERE \"ImportJobId\"=@job",("@job",jobId));await using var r=await cmd.ExecuteReaderAsync(ct);var rows=new List<MappedCandidate>();while(await r.ReadAsync(ct))rows.Add(new(ParitySql.Guid(r,"Id"),ParitySql.NullableString(r,"SourceAccount"),ParitySql.NullableDate(r,"BookingDate"),ParitySql.Decimal(r,"Amount"),ParitySql.String(r,"Currency"),ParitySql.NullableString(r,"Counterparty"),ParitySql.NullableString(r,"Description"),ParitySql.NullableString(r,"CategoryText"),ParitySql.NullableString(r,"ExternalKey"),ParitySql.String(r,"RowFingerprint"),ParitySql.String(r,"ValidationStatus"),ParitySql.NullableString(r,"ValidationError")));return rows;}
    private static async Task MarkCandidate(FullWorthDbContext db,Guid id,string state,CancellationToken ct){var c=await ParitySql.OpenAsync(db,ct);await using var cmd=ParitySql.Command(c,"UPDATE \"ImportCandidates\" SET \"DuplicateStatus\"=@state WHERE \"Id\"=@id",("@state",state),("@id",id));await cmd.ExecuteNonQueryAsync(ct);}
}