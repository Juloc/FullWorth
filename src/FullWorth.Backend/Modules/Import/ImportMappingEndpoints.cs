using FullWorth.Backend.Validation;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FullWorth.Backend.Modules.Merchants;
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
    IReadOnlyList<Guid>? CandidateIds = null,
    // Quellkonto aus der Datei -> Name eines Kontos, das dieser Import erst anlegen soll. Vorher gab
    // es nur "auf welches bestehende Konto?", und eine Zeile ohne Antwort darauf wurde stillschweigend
    // uebersprungen - eine Datei mit einem noch unbekannten Konto importierte also lautlos nichts.
    IReadOnlyDictionary<string, string>? NewAccountNames = null);
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
            var rows = ImportTabularFile.Read(fileResult.FileName!, fileResult.Bytes!);
            if (rows.Count == 0) return Results.BadRequest(new { error = "No data rows found." });
            var headers = rows[0].Keys.ToArray();
            return Results.Ok(new
            {
                fileName = fileResult.FileName,
                headers,
                suggestedMapping = ImportTabularFile.SuggestColumns(headers),
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
        try { rows = ImportTabularFile.Read(file.FileName, bytes); }
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
        Guid jobId, Guid fullWorthSpaceId, CurrentUserContext currentUser, SpaceAccess space,
        ImportMappingStore store, ImportSourceAccountStore sourceAccounts, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await store.OwnsOpenJobAsync(jobId, fullWorthSpaceId, userId, ct)) return Results.NotFound();
        var accounts = await store.SourceAccountCountsAsync(jobId, ct);
        var categories = await store.SourceCategoryCountsAsync(jobId, ct);

        // Was beim letzten Mal zugeordnet wurde (#131, Abschnitt 3). Nur was der Nutzer noch
        // erreichen darf: eine Erinnerung an ein Konto, das inzwischen jemand anderem gehoert, waere
        // ein Vorschlag, den der Commit danach ablehnt.
        var remembered = await sourceAccounts.ForSpaceAsync(fullWorthSpaceId, ct);
        var writable = await space.WritableAccountIdsAsync(userId, fullWorthSpaceId, ct);
        Guid? RememberedFor(string? source) =>
            ImportSourceAccountStore.Normalize(source) is { } key
            && remembered.TryGetValue(key, out var accountId)
            && writable.Contains(accountId)
                ? accountId
                : null;

        return Results.Ok(new
        {
            sourceAccounts = accounts.Select(row => new
            {
                source = row.Source,
                count = row.Count,
                rememberedAccountId = RememberedFor(row.Source)
            }),
            sourceCategories = categories.Select(row => new { source = row.Source, count = row.Count })
        });
    }

    private static async Task<IResult> CommitMapped(
        Guid jobId, Guid fullWorthSpaceId, ImportMappedCommitWrite request, CurrentUserContext currentUser,
        SpaceAccess space, ImportMappingStore store, ImportMappingCommitService commit,
        ImportSourceAccountStore sourceAccounts, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await store.OwnsOpenJobAsync(jobId, fullWorthSpaceId, userId, ct)) return Results.NotFound();
        if (!await space.HasCapabilityAsync(userId, fullWorthSpaceId, "transactions.write", ct))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        var writable = await space.WritableAccountIdsAsync(userId, fullWorthSpaceId, ct);
        var accountMap = new Dictionary<string, Guid?>(
            request.SourceAccountMappings ?? new Dictionary<string, Guid?>());
        var allMappedIds = accountMap.Values.Where(value => value.HasValue).Select(value => value!.Value)
            .Concat(request.DefaultAccountId.HasValue ? [request.DefaultAccountId.Value] : []).Distinct().ToArray();
        if (allMappedIds.Any(id => !writable.Contains(id))) return Results.BadRequest(new { error = "An account mapping is inaccessible." });

        // Ein Quellkonto, fuer das ein neues Konto entstehen soll, bekommt eine Platzhalter-Kennung:
        // die Klassifizierung unten braucht eine, und gegen ein Konto, das es noch nicht gibt, kann es
        // ohnehin keine Dublette geben. Der Commit tauscht sie gegen die echte.
        var newAccounts = new Dictionary<Guid, (string Name, string Currency)>();
        var spaceCurrency = await store.BaseCurrencyAsync(fullWorthSpaceId, ct);
        foreach (var (source, rawName) in request.NewAccountNames ?? new Dictionary<string, string>())
        {
            var name = rawName?.Trim();
            if (string.IsNullOrWhiteSpace(name)) return Results.BadRequest(new { error = "A new account needs a name." });
            if (accountMap.TryGetValue(source, out var already) && already.HasValue) continue;
            var placeholder = Guid.NewGuid();
            accountMap[source] = placeholder;
            newAccounts[placeholder] = (name, spaceCurrency);
        }

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
            request.CreateMissingCategories, request.RunFullWorthCategorization, newAccounts, ct);

        // Erst jetzt gemerkt, nicht beim Zuordnen (#131, Abschnitt 3): bis hierher war die Auswahl
        // ein Entwurf, und ein abgebrochener Import soll die naechste Vorauswahl nicht praegen.
        // Ein eben angelegtes Konto wird mit seiner echten Kennung gemerkt, nicht mit dem Platzhalter:
        // beim naechsten Import derselben Datei soll es dort stehen, wo es entstanden ist.
        await sourceAccounts.RememberAsync(
            fullWorthSpaceId,
            accountMap
                // Ein Platzhalter ohne angelegtes Konto zeigt auf nichts - ihn zu merken liefe gegen den
                // Fremdschluessel und machte aus einem gelungenen Import einen Fehler.
                .Where(entry => entry.Value is not { } id || !newAccounts.ContainsKey(id) || outcome.CreatedAccounts.ContainsKey(id))
                .ToDictionary(
                    entry => entry.Key,
                    entry => entry.Value is { } id && outcome.CreatedAccounts.TryGetValue(id, out var real) ? real : entry.Value),
            ct);

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
    private static string? Clean(string? value)=>string.IsNullOrWhiteSpace(value)?null:value.Trim();
    /// <summary>
    /// Same rule as ImportJobEndpoints.RowCurrency: a mapped currency column that IS present but
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

}