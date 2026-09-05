using System.Data;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Audit;
using FullWorth.Backend.Security;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Parity;

public sealed record InvestmentImportColumnMapping(
    string TradeDate,
    string TradeType,
    string? SettlementDate,
    string? SecurityName,
    string? Isin,
    string? Wkn,
    string? Ticker,
    string? Quantity,
    string? Price,
    string? GrossAmount,
    string? Amount,
    string? Currency,
    string? Fees,
    string? Taxes,
    string? WithholdingTax,
    string? ExternalKey);

public sealed record InvestmentImportPortfolioCreate(
    string Name,
    string Currency = "EUR",
    string? ProviderName = null);

public sealed record InvestmentImportCommitWrite(
    Guid? PortfolioId,
    IReadOnlyDictionary<string, Guid?>? SecurityMappings,
    bool CreateMissingSecurities = false,
    IReadOnlyList<Guid>? CandidateIds = null,
    InvestmentImportPortfolioCreate? CreatePortfolio = null);

public static class InvestmentImportParityEndpoints
{
    private const long MaxUploadBytes = 25L * 1024 * 1024;
    private static readonly HashSet<string> AllowedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "buy", "sell", "dividend", "interest", "fee", "tax", "deposit", "withdrawal",
        "security_transfer_in", "security_transfer_out", "split", "other"
    };
    private static readonly HashSet<string> SecurityRequiredTypes = new(StringComparer.OrdinalIgnoreCase)
    { "buy", "sell", "security_transfer_in", "security_transfer_out", "split" };

    public static IEndpointRouteBuilder MapInvestmentImportParityEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/investment-import").WithTags("Investments", "Import");
        group.MapPost("/detect", Detect);
        group.MapPost("/upload", Upload);
        group.MapGet("/jobs/{jobId:guid}", GetJob);
        group.MapGet("/jobs/{jobId:guid}/summary", Summary);
        group.MapPost("/jobs/{jobId:guid}/commit", Commit);
        return app;
    }

    private static async Task<IResult> Detect(
        Guid fullWorthSpaceId,
        HttpRequest request,
        CurrentUserContext currentUser,
        FullWorthDbContext db,
        CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await CanManageInvestments(db, userId, fullWorthSpaceId, ct))
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        var file = await ReadFile(request, ct);
        if (file.Error is not null) return Results.BadRequest(new { error = file.Error });
        try
        {
            var rows = Parse(file.FileName!, file.Bytes!);
            if (rows.Count == 0) return Results.BadRequest(new { error = "No data rows found." });
            var headers = rows[0].Keys.ToArray();
            return Results.Ok(new
            {
                fileName = file.FileName,
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

    private static async Task<IResult> Upload(
        Guid fullWorthSpaceId,
        HttpRequest request,
        CurrentUserContext currentUser,
        FullWorthDbContext db,
        AuditService audit,
        CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await CanManageInvestments(db, userId, fullWorthSpaceId, ct))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (!request.HasFormContentType) return Results.BadRequest(new { error = "Expected multipart/form-data." });

        var form = await request.ReadFormAsync(ct);
        var file = form.Files.GetFile("file");
        if (file is null || file.Length == 0 || file.Length > MaxUploadBytes)
            return Results.BadRequest(new { error = "Invalid investment import file." });
        if (Path.GetExtension(file.FileName).ToLowerInvariant() is not (".csv" or ".xlsx"))
            return Results.BadRequest(new { error = "Supported formats are CSV and XLSX." });

        InvestmentImportColumnMapping? mapping;
        try
        {
            mapping = JsonSerializer.Deserialize<InvestmentImportColumnMapping>(
                form["mapping"].ToString(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch
        {
            return Results.BadRequest(new { error = "Invalid mapping JSON." });
        }
        if (mapping is null || string.IsNullOrWhiteSpace(mapping.TradeDate) || string.IsNullOrWhiteSpace(mapping.TradeType))
            return Results.BadRequest(new { error = "Trade date and transaction type columns are required." });

        await using var stream = new MemoryStream(checked((int)file.Length));
        await file.CopyToAsync(stream, ct);
        var bytes = stream.ToArray();
        List<Dictionary<string, string>> rows;
        try { rows = Parse(file.FileName, bytes); }
        catch (Exception exception) when (exception is InvalidDataException or FormatException)
        { return Results.BadRequest(new { error = exception.Message }); }
        if (rows.Count == 0) return Results.BadRequest(new { error = "No data rows found." });

        var headers = rows[0].Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var mappedColumns = new[]
        {
            mapping.TradeDate, mapping.TradeType, mapping.SettlementDate, mapping.SecurityName,
            mapping.Isin, mapping.Wkn, mapping.Ticker, mapping.Quantity, mapping.Price, mapping.GrossAmount,
            mapping.Amount, mapping.Currency, mapping.Fees, mapping.Taxes, mapping.WithholdingTax, mapping.ExternalKey
        }.Where(value => !string.IsNullOrWhiteSpace(value));
        if (mappedColumns.Any(column => !headers.Contains(column!)))
            return Results.BadRequest(new { error = "Mapping references an unknown column." });

        var candidates = new List<Candidate>();
        var occurrenceBySemantic = new Dictionary<string, int>(StringComparer.Ordinal);
        var errorCount = 0;
        for (var index = 0; index < rows.Count; index++)
        {
            try
            {
                var candidate = ParseCandidate(index + 1, rows[index], mapping);
                var semantic = SemanticFingerprint(candidate);
                var occurrence = occurrenceBySemantic.GetValueOrDefault(semantic) + 1;
                occurrenceBySemantic[semantic] = occurrence;
                candidate = candidate with { Fingerprint = Sha256($"{semantic}|{occurrence}") };
                var validationError = ValidateCandidate(candidate);
                if (validationError is not null)
                {
                    errorCount++;
                    candidate = candidate with { Status = "error", Error = validationError };
                }
                candidates.Add(candidate);
            }
            catch (Exception exception) when (exception is FormatException or ArgumentException)
            {
                errorCount++;
                candidates.Add(new Candidate(
                    Guid.NewGuid(), index + 1, null, null, null, null, null, null, null,
                    null, null, null, 0m, "EUR", 0m, 0m, 0m, null,
                    Sha256($"invalid|{index + 1}|{rows[index].Count}"), "error", exception.Message, "new"));
            }
        }

        var jobId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var connection = await ParitySql.OpenAsync(db, ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await using (var command = ParitySql.Command(connection, """
INSERT INTO "InvestmentImportJobs"
("Id","FullWorthSpaceId","UserId","FileName","FileSha256","Status","SourceRowCount","ReadyCount","DuplicateCount","ImportedCount","ErrorCount","CreatedAt","UpdatedAt")
VALUES (@id,@space,@user,@file,@sha,@status,@source,@ready,0,0,@errors,@now,@now)
""", ("@id", jobId), ("@space", fullWorthSpaceId), ("@user", userId),
            ("@file", Path.GetFileName(file.FileName)), ("@sha", Sha256Bytes(bytes)),
            ("@status", errorCount == candidates.Count ? "failed" : "review"),
            ("@source", candidates.Count), ("@ready", candidates.Count - errorCount),
            ("@errors", errorCount), ("@now", now)))
            await command.ExecuteNonQueryAsync(ct);

        foreach (var candidate in candidates)
        {
            await using var command = ParitySql.Command(connection, """
INSERT INTO "InvestmentImportCandidates"
("Id","ImportJobId","RowNumber","TradeDate","SettlementDate","TradeType","SecurityName","Isin","Wkn","Ticker",
 "Quantity","Price","GrossAmount","Amount","Currency","Fees","Taxes","WithholdingTax","ExternalKey","RowFingerprint",
 "ValidationStatus","DuplicateStatus","ValidationError","CreatedAt")
VALUES (@id,@job,@row,@tradeDate,@settlement,@type,@name,@isin,@wkn,@ticker,@quantity,@price,@gross,@amount,@currency,@fees,@taxes,@withholding,@external,@fingerprint,@status,'new',@error,@now)
""", ("@id", candidate.Id), ("@job", jobId), ("@row", candidate.RowNumber),
                ("@tradeDate", candidate.TradeDate), ("@settlement", candidate.SettlementDate),
                ("@type", candidate.TradeType), ("@name", candidate.SecurityName), ("@isin", candidate.Isin),
                ("@wkn", candidate.Wkn), ("@ticker", candidate.Ticker), ("@quantity", candidate.Quantity),
                ("@price", candidate.Price), ("@gross", candidate.GrossAmount), ("@amount", candidate.Amount),
                ("@currency", candidate.Currency), ("@fees", candidate.Fees), ("@taxes", candidate.Taxes),
                ("@withholding", candidate.WithholdingTax), ("@external", candidate.ExternalKey),
                ("@fingerprint", candidate.Fingerprint), ("@status", candidate.Status),
                ("@error", candidate.Error), ("@now", now));
            await command.ExecuteNonQueryAsync(ct);
        }

        audit.Record(fullWorthSpaceId, userId, "investment.import.uploaded", "InvestmentImportJob", jobId);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.Ok(new { jobId, sourceRows = candidates.Count, ready = candidates.Count - errorCount, errors = errorCount });
    }

    private static async Task<IResult> GetJob(
        Guid jobId,
        Guid fullWorthSpaceId,
        CurrentUserContext currentUser,
        FullWorthDbContext db,
        CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await OwnJobAsync(db, jobId, fullWorthSpaceId, userId, includeCompleted: true, ct)) return Results.NotFound();
        var connection = await ParitySql.OpenAsync(db, ct);
        await using var command = ParitySql.Command(connection, """
SELECT "FileName","Status","SourceRowCount","ReadyCount","DuplicateCount","ImportedCount","ErrorCount","CreatedAt","CompletedAt"
FROM "InvestmentImportJobs" WHERE "Id"=@id
""", ("@id", jobId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return Results.NotFound();
        return Results.Ok(new
        {
            jobId,
            fileName = ParitySql.String(reader, "FileName"),
            status = ParitySql.String(reader, "Status"),
            sourceRows = ParitySql.Int(reader, "SourceRowCount"),
            ready = ParitySql.Int(reader, "ReadyCount"),
            duplicates = ParitySql.Int(reader, "DuplicateCount"),
            imported = ParitySql.Int(reader, "ImportedCount"),
            errors = ParitySql.Int(reader, "ErrorCount"),
            createdAt = ParitySql.Timestamp(reader, "CreatedAt"),
            completedAt = ParitySql.NullableTimestamp(reader, "CompletedAt")
        });
    }

    private static async Task<IResult> Summary(
        Guid jobId,
        Guid fullWorthSpaceId,
        CurrentUserContext currentUser,
        FullWorthDbContext db,
        CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await OwnJobAsync(db, jobId, fullWorthSpaceId, userId, includeCompleted: true, ct)) return Results.NotFound();
        var candidates = await ReadCandidatesAsync(db, jobId, ct);
        var securities = await ReadSecuritiesAsync(db, fullWorthSpaceId, ct);
        var groups = candidates.Where(candidate => candidate.Status == "ready" && HasSecurityIdentity(candidate))
            .GroupBy(SecurityKey)
            .Select(group =>
            {
                var first = group.First();
                var match = AutoMatch(first, securities);
                return new
                {
                    key = group.Key,
                    name = first.SecurityName,
                    isin = first.Isin,
                    wkn = first.Wkn,
                    ticker = first.Ticker,
                    currency = first.Currency,
                    count = group.Count(),
                    autoMatchId = match?.Id,
                    autoMatchName = match?.Name
                };
            })
            .OrderBy(group => group.name ?? group.isin ?? group.ticker ?? group.key)
            .ToArray();

        var preview = candidates.OrderBy(candidate => candidate.RowNumber).Take(100).Select(candidate => new
        {
            candidate.Id,
            candidate.RowNumber,
            candidate.TradeDate,
            candidate.SettlementDate,
            candidate.TradeType,
            candidate.SecurityName,
            candidate.Isin,
            candidate.Wkn,
            candidate.Ticker,
            candidate.Quantity,
            candidate.Price,
            candidate.GrossAmount,
            candidate.Amount,
            candidate.Currency,
            candidate.Fees,
            candidate.Taxes,
            candidate.WithholdingTax,
            validationStatus = candidate.Status,
            duplicateStatus = candidate.DuplicateStatus,
            validationError = candidate.Error,
            securityKey = HasSecurityIdentity(candidate) ? SecurityKey(candidate) : null
        });

        return Results.Ok(new
        {
            securities = groups,
            ready = candidates.Count(candidate => candidate.Status == "ready"),
            errors = candidates.Count(candidate => candidate.Status == "error"),
            preview
        });
    }

    private static async Task<IResult> Commit(
        Guid jobId,
        Guid fullWorthSpaceId,
        InvestmentImportCommitWrite request,
        CurrentUserContext currentUser,
        FullWorthDbContext db,
        AuditService audit,
        CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await OwnJobAsync(db, jobId, fullWorthSpaceId, userId, includeCompleted: false, ct)) return Results.NotFound();
        if (!await CanManageInvestments(db, userId, fullWorthSpaceId, ct))
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        if (request.PortfolioId.HasValue && request.CreatePortfolio is not null)
            return Results.BadRequest(new { error = "Choose either an existing portfolio or create a new one, not both." });
        if (!request.PortfolioId.HasValue && request.CreatePortfolio is null)
            return Results.BadRequest(new { error = "Choose an existing portfolio or provide a new portfolio." });

        var targetPortfolioId = request.PortfolioId;
        var portfolioName = request.CreatePortfolio?.Name?.Trim();
        var portfolioCurrency = request.CreatePortfolio?.Currency?.Trim().ToUpperInvariant();
        var portfolioProvider = request.CreatePortfolio?.ProviderName?.Trim();
        if (targetPortfolioId.HasValue)
        {
            if (!await CanWritePortfolioAsync(db, userId, fullWorthSpaceId, targetPortfolioId.Value, ct))
                return Results.BadRequest(new { error = "Target portfolio is inaccessible or not writable." });
        }
        else if (string.IsNullOrWhiteSpace(portfolioName) ||
                 string.IsNullOrWhiteSpace(portfolioCurrency) ||
                 portfolioCurrency.Length != 3 ||
                 !portfolioCurrency.All(char.IsLetter))
        {
            return Results.BadRequest(new { error = "New portfolio requires a name and a three-letter currency." });
        }

        var allCandidates = await ReadCandidatesAsync(db, jobId, ct);
        var selectedIds = request.CandidateIds?.ToHashSet();
        var candidates = allCandidates
            .Where(candidate => candidate.Status == "ready" && (selectedIds is null || selectedIds.Contains(candidate.Id)))
            .OrderBy(candidate => candidate.TradeDate)
            .ThenBy(candidate => candidate.RowNumber)
            .ToList();
        if (candidates.Count == 0) return Results.BadRequest(new { error = "No ready investment rows selected." });

        var securities = await ReadSecuritiesAsync(db, fullWorthSpaceId, ct);
        var mappings = request.SecurityMappings ?? new Dictionary<string, Guid?>();
        var mappedIds = mappings.Values.Where(value => value.HasValue).Select(value => value!.Value).Distinct().ToArray();
        if (mappedIds.Any(id => securities.All(security => security.Id != id)))
            return Results.BadRequest(new { error = "A security mapping belongs to another FullWorth Space or does not exist." });

        var resolution = new Dictionary<string, ExistingSecurity?>(StringComparer.Ordinal);
        foreach (var group in candidates.Where(HasSecurityIdentity).GroupBy(SecurityKey))
        {
            ExistingSecurity? resolved = null;
            if (mappings.TryGetValue(group.Key, out var mapped) && mapped.HasValue)
                resolved = securities.SingleOrDefault(security => security.Id == mapped.Value);
            resolved ??= AutoMatch(group.First(), securities);
            resolution[group.Key] = resolved;
        }

        var unresolved = new List<object>();
        foreach (var candidate in candidates.Where(candidate => SecurityRequiredTypes.Contains(candidate.TradeType!)))
        {
            var key = HasSecurityIdentity(candidate) ? SecurityKey(candidate) : null;
            var resolved = key is null ? null : resolution.GetValueOrDefault(key);
            if (resolved is null && (!request.CreateMissingSecurities || key is null))
            {
                unresolved.Add(new
                {
                    candidate.Id,
                    candidate.RowNumber,
                    candidate.SecurityName,
                    candidate.Isin,
                    candidate.Wkn,
                    candidate.Ticker
                });
            }
        }
        if (unresolved.Count > 0)
            return Results.BadRequest(new
            {
                error = "Some investment rows require an unresolved security.",
                unresolvedSecurities = unresolved
            });

        var imported = 0;
        var duplicates = 0;
        var seenStableKeys = new HashSet<string>(StringComparer.Ordinal);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            var connection = await ParitySql.OpenAsync(db, ct);
            var portfolioCreated = false;
            if (!targetPortfolioId.HasValue)
            {
                targetPortfolioId = Guid.NewGuid();
                await using var createPortfolio = ParitySql.Command(connection, """
INSERT INTO "InvestmentPortfolios"
("Id","FullWorthSpaceId","Name","Currency","AccountId","BenchmarkSecurityId","ProviderName","IsManual","IncludeInNetWorth","IsArchived","CreatedAt","UpdatedAt")
VALUES (@id,@space,@name,@currency,NULL,NULL,@provider,true,true,false,@now,@now)
""", ("@id", targetPortfolioId.Value), ("@space", fullWorthSpaceId), ("@name", portfolioName!),
                    ("@currency", portfolioCurrency!), ("@provider", string.IsNullOrWhiteSpace(portfolioProvider) ? null : portfolioProvider),
                    ("@now", DateTimeOffset.UtcNow));
                await createPortfolio.ExecuteNonQueryAsync(ct);
                portfolioCreated = true;
                audit.Record(fullWorthSpaceId, userId, "investment.portfolio.created", "InvestmentPortfolio", targetPortfolioId.Value);
            }
            var portfolioId = targetPortfolioId.Value;

            if (request.CreateMissingSecurities)
            {
                foreach (var group in candidates.Where(HasSecurityIdentity).GroupBy(SecurityKey))
                {
                    if (resolution.GetValueOrDefault(group.Key) is not null) continue;
                    var first = group.First();
                    var created = new ExistingSecurity(
                        Guid.NewGuid(),
                        string.IsNullOrWhiteSpace(first.SecurityName)
                            ? first.Isin ?? first.Wkn ?? first.Ticker ?? "Imported security"
                            : first.SecurityName!,
                        first.Isin,
                        first.Wkn,
                        first.Ticker,
                        first.Currency);
                    await using var createSecurity = ParitySql.Command(connection, """
INSERT INTO "Securities"
("Id","FullWorthSpaceId","Name","Isin","Wkn","Ticker","AssetType","Currency","ProviderKey","IsActive","CreatedAt","UpdatedAt")
VALUES (@id,@space,@name,@isin,@wkn,@ticker,'other',@currency,'investment-import',true,@now,@now)
""", ("@id", created.Id), ("@space", fullWorthSpaceId), ("@name", created.Name),
                        ("@isin", created.Isin), ("@wkn", created.Wkn), ("@ticker", created.Ticker),
                        ("@currency", created.Currency), ("@now", DateTimeOffset.UtcNow));
                    await createSecurity.ExecuteNonQueryAsync(ct);
                    securities.Add(created);
                    resolution[group.Key] = created;
                }
            }

            foreach (var candidate in candidates)
            {
                var stableKey = StableExternalKey(candidate);
                if (!seenStableKeys.Add(stableKey) || await InvestmentTradeExistsAsync(db, portfolioId, stableKey, ct))
                {
                    duplicates++;
                    await MarkCandidateAsync(db, candidate.Id, "duplicate", ct);
                    continue;
                }

                Guid? securityId = null;
                if (HasSecurityIdentity(candidate))
                    securityId = resolution.GetValueOrDefault(SecurityKey(candidate))?.Id;
                if (SecurityRequiredTypes.Contains(candidate.TradeType!) && !securityId.HasValue)
                    throw new InvalidOperationException("Required security could not be resolved during commit.");

                var now = DateTimeOffset.UtcNow;
                await using var insert = ParitySql.Command(connection, """
INSERT INTO "InvestmentTrades"
("Id","FullWorthSpaceId","PortfolioId","SecurityId","TradeType","TradeDate","SettlementDate","Quantity","Price","GrossAmount","Amount","Currency","Fees","Taxes","WithholdingTax","Source","ExternalKey","Notes","CreatedAt","UpdatedAt")
VALUES (@id,@space,@portfolio,@security,@type,@tradeDate,@settlement,@quantity,@price,@gross,@amount,@currency,@fees,@taxes,@withholding,'import',@external,@notes,@now,@now)
""", ("@id", Guid.NewGuid()), ("@space", fullWorthSpaceId), ("@portfolio", portfolioId),
                    ("@security", securityId), ("@type", candidate.TradeType), ("@tradeDate", candidate.TradeDate),
                    ("@settlement", candidate.SettlementDate), ("@quantity", candidate.Quantity),
                    ("@price", candidate.Price), ("@gross", candidate.GrossAmount), ("@amount", candidate.Amount),
                    ("@currency", candidate.Currency), ("@fees", candidate.Fees), ("@taxes", candidate.Taxes),
                    ("@withholding", candidate.WithholdingTax), ("@external", stableKey),
                    ("@notes", $"Imported row {candidate.RowNumber}"), ("@now", now));
                await insert.ExecuteNonQueryAsync(ct);
                imported++;
                await MarkCandidateAsync(db, candidate.Id, "imported", ct);
            }

            await using (var updateJob = ParitySql.Command(connection, """
UPDATE "InvestmentImportJobs"
SET "Status"='completed',"ImportedCount"=@imported,"DuplicateCount"=@duplicates,"UpdatedAt"=@now,"CompletedAt"=@now
WHERE "Id"=@id
""", ("@imported", imported), ("@duplicates", duplicates), ("@now", DateTimeOffset.UtcNow), ("@id", jobId)))
                await updateJob.ExecuteNonQueryAsync(ct);

            audit.Record(fullWorthSpaceId, userId, "investment.import.completed", "InvestmentImportJob", jobId);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Results.Ok(new { imported, duplicates, total = candidates.Count, portfolioId, portfolioCreated });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            return Results.Conflict(new
            {
                error = "Investment import could not be applied. No investment transactions were imported. Check trade order, quantities and mappings."
            });
        }
    }

    private static Candidate ParseCandidate(
        int rowNumber,
        IReadOnlyDictionary<string, string> row,
        InvestmentImportColumnMapping mapping)
    {
        string? Cell(string? column) => string.IsNullOrWhiteSpace(column) ? null : row.GetValueOrDefault(column!);
        var type = NormalizeTradeType(Cell(mapping.TradeType));
        var tradeDate = ParseDate(Cell(mapping.TradeDate));
        var settlementDate = ParseOptionalDate(Cell(mapping.SettlementDate));
        var securityName = Clean(Cell(mapping.SecurityName));
        var isin = Clean(Cell(mapping.Isin))?.ToUpperInvariant();
        var wkn = Clean(Cell(mapping.Wkn))?.ToUpperInvariant();
        var ticker = Clean(Cell(mapping.Ticker))?.ToUpperInvariant();
        var quantity = AbsNullable(ParseOptionalAmount(Cell(mapping.Quantity)));
        var price = AbsNullable(ParseOptionalAmount(Cell(mapping.Price)));
        var gross = AbsNullable(ParseOptionalAmount(Cell(mapping.GrossAmount)));
        var amount = Math.Abs(ParseOptionalAmount(Cell(mapping.Amount)) ?? gross ??
                              (price.HasValue && quantity.HasValue ? price.Value * quantity.Value : 0m));
        var currency = ParseCurrency(Cell(mapping.Currency));
        var fees = Math.Abs(ParseOptionalAmount(Cell(mapping.Fees)) ?? 0m);
        var taxes = Math.Abs(ParseOptionalAmount(Cell(mapping.Taxes)) ?? 0m);
        var withholding = Math.Abs(ParseOptionalAmount(Cell(mapping.WithholdingTax)) ?? 0m);
        return new Candidate(
            Guid.NewGuid(), rowNumber, tradeDate, settlementDate, type, securityName, isin, wkn, ticker,
            quantity, price, gross, amount, currency, fees, taxes, withholding,
            Clean(Cell(mapping.ExternalKey)), "", "ready", null, "new");
    }

    private static string? ValidateCandidate(Candidate candidate)
    {
        if (!candidate.TradeDate.HasValue) return "Trade date is required.";
        if (string.IsNullOrWhiteSpace(candidate.TradeType) || !AllowedTypes.Contains(candidate.TradeType))
            return "Unsupported investment transaction type.";
        if (candidate.Currency.Length != 3 || !candidate.Currency.All(char.IsLetter))
            return "Currency must contain three letters.";
        if (candidate.Isin is { Length: > 0 } && candidate.Isin.Length != 12)
            return "ISIN must contain 12 characters.";
        if (SecurityRequiredTypes.Contains(candidate.TradeType))
        {
            if (!HasSecurityIdentity(candidate)) return "This transaction type requires a security identifier or name.";
            if (candidate.Quantity is null or <= 0) return "This transaction type requires a positive quantity or split ratio.";
        }
        if (candidate.TradeType is "buy" or "sell" &&
            candidate.Price is null or <= 0 && candidate.GrossAmount is null or <= 0)
            return "Buy/sell requires a positive price or gross amount.";
        return null;
    }

    private static bool HasSecurityIdentity(Candidate candidate) =>
        !string.IsNullOrWhiteSpace(candidate.Isin) || !string.IsNullOrWhiteSpace(candidate.Wkn) ||
        !string.IsNullOrWhiteSpace(candidate.Ticker) || !string.IsNullOrWhiteSpace(candidate.SecurityName);

    private static string SecurityKey(Candidate candidate)
    {
        if (!string.IsNullOrWhiteSpace(candidate.Isin)) return $"isin:{candidate.Isin}";
        if (!string.IsNullOrWhiteSpace(candidate.Wkn)) return $"wkn:{candidate.Wkn}";
        if (!string.IsNullOrWhiteSpace(candidate.Ticker)) return $"ticker:{candidate.Ticker}|{candidate.Currency}";
        return $"name:{Normalize(candidate.SecurityName!)}|{candidate.Currency}";
    }

    private static ExistingSecurity? AutoMatch(Candidate candidate, IReadOnlyList<ExistingSecurity> securities)
    {
        if (!string.IsNullOrWhiteSpace(candidate.Isin))
            return securities.SingleOrDefault(security => string.Equals(security.Isin, candidate.Isin, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(candidate.Wkn))
        {
            var matches = securities.Where(security => string.Equals(security.Wkn, candidate.Wkn, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length == 1) return matches[0];
        }
        if (!string.IsNullOrWhiteSpace(candidate.Ticker))
        {
            var matches = securities.Where(security =>
                string.Equals(security.Ticker, candidate.Ticker, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(security.Currency, candidate.Currency, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length == 1) return matches[0];
        }
        if (!string.IsNullOrWhiteSpace(candidate.SecurityName))
        {
            var name = Normalize(candidate.SecurityName);
            var matches = securities.Where(security =>
                Normalize(security.Name) == name &&
                string.Equals(security.Currency, candidate.Currency, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length == 1) return matches[0];
        }
        return null;
    }

    private static async Task<List<ExistingSecurity>> ReadSecuritiesAsync(FullWorthDbContext db, Guid space, CancellationToken ct)
    {
        var connection = await ParitySql.OpenAsync(db, ct);
        await using var command = ParitySql.Command(connection, """
SELECT "Id","Name","Isin","Wkn","Ticker","Currency"
FROM "Securities" WHERE "FullWorthSpaceId"=@space AND "IsActive"=true
""", ("@space", space));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<ExistingSecurity>();
        while (await reader.ReadAsync(ct))
            rows.Add(new ExistingSecurity(
                ParitySql.Guid(reader, "Id"), ParitySql.String(reader, "Name"),
                ParitySql.NullableString(reader, "Isin"), ParitySql.NullableString(reader, "Wkn"),
                ParitySql.NullableString(reader, "Ticker"), ParitySql.String(reader, "Currency")));
        return rows;
    }

    private static async Task<List<Candidate>> ReadCandidatesAsync(FullWorthDbContext db, Guid jobId, CancellationToken ct)
    {
        var connection = await ParitySql.OpenAsync(db, ct);
        await using var command = ParitySql.Command(connection, """
SELECT "Id","RowNumber","TradeDate","SettlementDate","TradeType","SecurityName","Isin","Wkn","Ticker","Quantity","Price",
 "GrossAmount","Amount","Currency","Fees","Taxes","WithholdingTax","ExternalKey","RowFingerprint","ValidationStatus","DuplicateStatus","ValidationError"
FROM "InvestmentImportCandidates" WHERE "ImportJobId"=@job ORDER BY "RowNumber"
""", ("@job", jobId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<Candidate>();
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new Candidate(
                ParitySql.Guid(reader, "Id"), ParitySql.Int(reader, "RowNumber"),
                ParitySql.NullableDate(reader, "TradeDate"), ParitySql.NullableDate(reader, "SettlementDate"),
                ParitySql.NullableString(reader, "TradeType"), ParitySql.NullableString(reader, "SecurityName"),
                ParitySql.NullableString(reader, "Isin"), ParitySql.NullableString(reader, "Wkn"),
                ParitySql.NullableString(reader, "Ticker"), ParitySql.NullableDecimal(reader, "Quantity"),
                ParitySql.NullableDecimal(reader, "Price"), ParitySql.NullableDecimal(reader, "GrossAmount"),
                ParitySql.Decimal(reader, "Amount"), ParitySql.String(reader, "Currency"),
                ParitySql.Decimal(reader, "Fees"), ParitySql.Decimal(reader, "Taxes"),
                ParitySql.Decimal(reader, "WithholdingTax"), ParitySql.NullableString(reader, "ExternalKey"),
                ParitySql.String(reader, "RowFingerprint"), ParitySql.String(reader, "ValidationStatus"),
                ParitySql.NullableString(reader, "ValidationError"), ParitySql.String(reader, "DuplicateStatus")));
        }
        return rows;
    }

    private static async Task<bool> OwnJobAsync(
        FullWorthDbContext db,
        Guid jobId,
        Guid space,
        Guid userId,
        bool includeCompleted,
        CancellationToken ct)
    {
        var connection = await ParitySql.OpenAsync(db, ct);
        var sql = "SELECT EXISTS(SELECT 1 FROM \"InvestmentImportJobs\" WHERE \"Id\"=@id AND \"FullWorthSpaceId\"=@space AND \"UserId\"=@user" +
                  (includeCompleted ? ")" : " AND \"Status\" NOT IN ('completed','cancelled'))");
        await using var command = ParitySql.Command(connection, sql, ("@id", jobId), ("@space", space), ("@user", userId));
        return Convert.ToBoolean(await command.ExecuteScalarAsync(ct));
    }

    private static async Task<bool> CanWritePortfolioAsync(
        FullWorthDbContext db,
        Guid userId,
        Guid space,
        Guid portfolioId,
        CancellationToken ct)
    {
        var connection = await ParitySql.OpenAsync(db, ct);
        await using var command = ParitySql.Command(connection,
            "SELECT \"AccountId\" FROM \"InvestmentPortfolios\" WHERE \"Id\"=@id AND \"FullWorthSpaceId\"=@space AND \"IsArchived\"=false",
            ("@id", portfolioId), ("@space", space));
        var account = await command.ExecuteScalarAsync(ct);
        if (account is null) return false;
        if (account is DBNull) return true;
        var writable = await ParitySql.WritableAccountIdsAsync(db, userId, space, ct);
        return writable.Contains((Guid)account);
    }

    private static Task<bool> CanManageInvestments(
        FullWorthDbContext db, Guid userId, Guid space, CancellationToken ct) =>
        PermissionsErgonomicsParityEndpoints.HasCapabilityAsync(db, userId, space, "investments.manage", ct);

    private static async Task<bool> InvestmentTradeExistsAsync(
        FullWorthDbContext db, Guid portfolioId, string externalKey, CancellationToken ct)
    {
        var connection = await ParitySql.OpenAsync(db, ct);
        await using var command = ParitySql.Command(connection,
            "SELECT EXISTS(SELECT 1 FROM \"InvestmentTrades\" WHERE \"PortfolioId\"=@portfolio AND \"ExternalKey\"=@key)",
            ("@portfolio", portfolioId), ("@key", externalKey));
        return Convert.ToBoolean(await command.ExecuteScalarAsync(ct));
    }

    private static async Task MarkCandidateAsync(FullWorthDbContext db, Guid id, string state, CancellationToken ct)
    {
        var connection = await ParitySql.OpenAsync(db, ct);
        await using var command = ParitySql.Command(connection,
            "UPDATE \"InvestmentImportCandidates\" SET \"DuplicateStatus\"=@state WHERE \"Id\"=@id",
            ("@state", state), ("@id", id));
        await command.ExecuteNonQueryAsync(ct);
    }

    private static string StableExternalKey(Candidate candidate) =>
        !string.IsNullOrWhiteSpace(candidate.ExternalKey)
            ? $"investment-import:external:{Sha256(candidate.ExternalKey.Trim())}"
            : $"investment-import:fingerprint:{candidate.Fingerprint}";

    private static string SemanticFingerprint(Candidate candidate) => Sha256(string.Join('|',
        candidate.TradeDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "",
        candidate.SettlementDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "",
        candidate.TradeType ?? "",
        candidate.Isin ?? "",
        candidate.Wkn ?? "",
        candidate.Ticker ?? "",
        Normalize(candidate.SecurityName ?? ""),
        candidate.Quantity?.ToString(CultureInfo.InvariantCulture) ?? "",
        candidate.Price?.ToString(CultureInfo.InvariantCulture) ?? "",
        candidate.GrossAmount?.ToString(CultureInfo.InvariantCulture) ?? "",
        candidate.Amount.ToString(CultureInfo.InvariantCulture),
        candidate.Currency,
        candidate.Fees.ToString(CultureInfo.InvariantCulture),
        candidate.Taxes.ToString(CultureInfo.InvariantCulture),
        candidate.WithholdingTax.ToString(CultureInfo.InvariantCulture)));

    private static InvestmentImportColumnMapping Suggest(IEnumerable<string> headers)
    {
        var all = headers.ToArray();
        string? Find(params string[] names) => all.FirstOrDefault(header => names.Any(name => Normalize(header) == Normalize(name)));
        return new InvestmentImportColumnMapping(
            Find("trade date", "datum", "date", "handelsdatum") ?? "",
            Find("type", "typ", "transaction type", "art", "transaktion") ?? "",
            Find("settlement date", "valuta", "wertstellung"),
            Find("security", "wertpapier", "name", "security name"),
            Find("isin"), Find("wkn"), Find("ticker", "symbol"),
            Find("quantity", "stück", "stueck", "anzahl", "shares"),
            Find("price", "kurs"), Find("gross", "brutto", "gross amount"),
            Find("amount", "betrag", "net", "netto"), Find("currency", "währung", "waehrung"),
            Find("fees", "gebühren", "gebuehren", "fee"), Find("taxes", "steuern", "tax"),
            Find("withholding tax", "quellensteuer"), Find("id", "transaction id", "order id", "external id"));
    }

    private static string NormalizeTradeType(string? value)
    {
        var normalized = Normalize(value ?? "");
        return normalized switch
        {
            "buy" or "kauf" or "kaufen" => "buy",
            "sell" or "verkauf" or "verkaufen" => "sell",
            "dividend" or "dividende" or "ausschuettung" or "ausschüttung" => "dividend",
            "interest" or "zins" or "zinsen" or "interestpayment" => "interest",
            "fee" or "fees" or "gebuehr" or "gebühr" or "gebuehren" or "gebühren" => "fee",
            "tax" or "taxes" or "steuer" or "steuern" or "taxoptimization" or "secaccount" => "tax",
            "deposit" or "einzahlung" or "customerinbound" or "customerinpayment" or "transferinbound" or "transferinstantinbound" => "deposit",
            "withdrawal" or "auszahlung" or "customeroutboundrequest" or "transferoutbound" or "transferinstantoutbound" or "cardtransaction" => "withdrawal",
            "securitytransferin" or "transferin" or "eingang" => "security_transfer_in",
            "securitytransferout" or "transferout" or "ausgang" => "security_transfer_out",
            "redemption" => "sell",
            "split" or "aktiensplit" => "split",
            "other" or "sonstiges" or "compensation" or "buycancelled" => "other",
            _ => value?.Trim().ToLowerInvariant() ?? ""
        };
    }

    private static async Task<(byte[]? Bytes, string? FileName, string? Error)> ReadFile(HttpRequest request, CancellationToken ct)
    {
        if (!request.HasFormContentType) return (null, null, "Expected multipart/form-data.");
        var form = await request.ReadFormAsync(ct);
        var file = form.Files.GetFile("file");
        if (file is null || file.Length == 0) return (null, null, "No file uploaded.");
        if (file.Length > MaxUploadBytes) return (null, null, "Maximum file size is 25 MB.");
        if (Path.GetExtension(file.FileName).ToLowerInvariant() is not (".csv" or ".xlsx"))
            return (null, null, "Supported formats are CSV and XLSX.");
        await using var stream = new MemoryStream(checked((int)file.Length));
        await file.CopyToAsync(stream, ct);
        return (stream.ToArray(), Path.GetFileName(file.FileName), null);
    }

    private static List<Dictionary<string, string>> Parse(string fileName, byte[] bytes) =>
        Path.GetExtension(fileName).Equals(".csv", StringComparison.OrdinalIgnoreCase) ? ParseCsv(bytes) : ParseXlsx(bytes);

    private static List<Dictionary<string, string>> ParseCsv(byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
        var records = SplitCsvRecords(text);
        if (records.Count < 2) return [];
        var delimiter = new[] { ';', ',', '\t' }.OrderByDescending(character => records[0].Count(value => value == character)).First();
        var header = ParseCsvLine(records[0], delimiter);
        return records.Skip(1).Where(line => !string.IsNullOrWhiteSpace(line)).Select(line =>
        {
            var cells = ParseCsvLine(line, delimiter);
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < header.Count; index++) row[header[index]] = index < cells.Count ? cells[index] : "";
            return row;
        }).ToList();
    }

    private static List<string> SplitCsvRecords(string text)
    {
        var rows = new List<string>();
        var builder = new StringBuilder();
        var quoted = false;
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (character == '"')
            {
                if (quoted && index + 1 < text.Length && text[index + 1] == '"')
                {
                    builder.Append("\"\"");
                    index++;
                    continue;
                }
                quoted = !quoted;
                builder.Append(character);
            }
            else if ((character == '\n' || character == '\r') && !quoted)
            {
                if (character == '\r' && index + 1 < text.Length && text[index + 1] == '\n') index++;
                rows.Add(builder.ToString());
                builder.Clear();
            }
            else builder.Append(character);
        }
        if (builder.Length > 0) rows.Add(builder.ToString());
        return rows;
    }

    private static List<string> ParseCsvLine(string line, char delimiter)
    {
        var cells = new List<string>();
        var builder = new StringBuilder();
        var quoted = false;
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (character == '"')
            {
                if (quoted && index + 1 < line.Length && line[index + 1] == '"')
                {
                    builder.Append('"');
                    index++;
                }
                else quoted = !quoted;
            }
            else if (character == delimiter && !quoted)
            {
                cells.Add(builder.ToString());
                builder.Clear();
            }
            else builder.Append(character);
        }
        cells.Add(builder.ToString());
        return cells;
    }

    private static List<Dictionary<string, string>> ParseXlsx(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        var sharedStrings = ReadSharedStrings(zip);
        var sheet = zip.GetEntry("xl/worksheets/sheet1.xml") ?? throw new InvalidDataException("XLSX has no first worksheet.");
        using var sheetStream = sheet.Open();
        var document = XDocument.Load(sheetStream);
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var rows = document.Descendants(ns + "row").Select(row => ReadXlsxRow(row, ns, sharedStrings)).ToList();
        if (rows.Count < 2) return [];
        var header = rows[0];
        var result = new List<Dictionary<string, string>>();
        foreach (var cells in rows.Skip(1))
        {
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < header.Count; index++) row[header[index]] = index < cells.Count ? cells[index] : "";
            result.Add(row);
        }
        return result;
    }

    private static List<string> ReadSharedStrings(ZipArchive zip)
    {
        var entry = zip.GetEntry("xl/sharedStrings.xml");
        if (entry is null) return [];
        using var stream = entry.Open();
        var document = XDocument.Load(stream);
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        return document.Descendants(ns + "si")
            .Select(item => string.Concat(item.Descendants(ns + "t").Select(text => text.Value))).ToList();
    }

    private static List<string> ReadXlsxRow(XElement row, XNamespace ns, IReadOnlyList<string> sharedStrings)
    {
        var values = new SortedDictionary<int, string>();
        foreach (var cell in row.Elements(ns + "c"))
        {
            var reference = (string?)cell.Attribute("r") ?? "A1";
            var column = ColumnIndex(reference);
            var type = (string?)cell.Attribute("t");
            var value = type == "inlineStr"
                ? string.Concat(cell.Descendants(ns + "t").Select(text => text.Value))
                : cell.Element(ns + "v")?.Value ?? "";
            if (type == "s" && int.TryParse(value, out var sharedIndex) && sharedIndex >= 0 && sharedIndex < sharedStrings.Count)
                value = sharedStrings[sharedIndex];
            values[column] = value;
        }
        var max = values.Count == 0 ? -1 : values.Keys.Max();
        return Enumerable.Range(0, max + 1).Select(index => values.GetValueOrDefault(index, "")).ToList();
    }

    private static int ColumnIndex(string reference)
    {
        var letters = new string(reference.TakeWhile(char.IsLetter).ToArray()).ToUpperInvariant();
        var value = 0;
        foreach (var character in letters) value = value * 26 + (character - 'A' + 1);
        return Math.Max(0, value - 1);
    }

    private static DateOnly ParseDate(string? value)
    {
        var parsed = ParseOptionalDate(value);
        return parsed ?? throw new FormatException("Trade date is missing.");
    }

    private static DateOnly? ParseOptionalDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial) && serial is > 20000 and < 100000)
            return DateOnly.FromDateTime(new DateTime(1899, 12, 30).AddDays(serial));
        var formats = new[] { "yyyy-MM-dd", "dd.MM.yyyy", "d.M.yyyy", "dd/MM/yyyy", "MM/dd/yyyy", "yyyy/MM/dd" };
        foreach (var format in formats)
            if (DateOnly.TryParseExact(text, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) return date;
        if (DateOnly.TryParse(text, CultureInfo.CurrentCulture, out var current)) return current;
        throw new FormatException($"Invalid date '{value}'.");
    }

    private static decimal? ParseOptionalAmount(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim().Replace("€", "").Replace("$", "").Replace("£", "").Replace(" ", "").Replace("'", "");
        if (text.Length == 0) return null;

        var commaCount = text.Count(character => character == ',');
        var dotCount = text.Count(character => character == '.');
        string normalized;
        if (commaCount > 0 && dotCount > 0)
        {
            var comma = text.LastIndexOf(',');
            var dot = text.LastIndexOf('.');
            var decimalSeparator = comma > dot ? ',' : '.';
            var groupingSeparator = decimalSeparator == ',' ? '.' : ',';
            normalized = text.Replace(groupingSeparator.ToString(), string.Empty);
            if (decimalSeparator == ',') normalized = normalized.Replace(',', '.');
        }
        else if (commaCount == 1 || dotCount == 1)
        {
            // A single separator is treated as the decimal separator. This is the only safe way to
            // accept both broker styles 12,50 and 12.50 without making the current server locale decide.
            normalized = text.Replace(',', '.');
        }
        else if (commaCount > 1 || dotCount > 1)
        {
            var separator = commaCount > 1 ? ',' : '.';
            var parts = text.Split(separator);
            if (parts.Skip(1).All(part => part.Length == 3 && part.All(char.IsDigit)))
            {
                // Unambiguous repeated thousands grouping: 1.234.567 / 1,234,567.
                normalized = string.Concat(parts);
            }
            else if (parts.Length > 2 && parts.Skip(1).Take(parts.Length - 2).All(part => part.Length == 3 && part.All(char.IsDigit)) &&
                     parts[^1].Length is > 0 and <= 10 && parts[^1].All(char.IsDigit))
            {
                // Grouping plus a final decimal part using the same separator is unusual but can be
                // represented deterministically, e.g. 1.234.567.89 -> 1234567.89.
                normalized = string.Concat(parts.Take(parts.Length - 1)) + "." + parts[^1];
            }
            else
            {
                throw new FormatException($"Ambiguous number '{value}'. Use an explicit decimal format such as 1234.56 or 1234,56.");
            }
        }
        else normalized = text;

        if (decimal.TryParse(normalized, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var amount)) return amount;
        throw new FormatException($"Invalid number '{value}'.");
    }

    private static decimal? AbsNullable(decimal? value) => value.HasValue ? Math.Abs(value.Value) : null;

    private static string ParseCurrency(string? value)
    {
        var currency = Clean(value)?.ToUpperInvariant();
        return currency is { Length: 3 } && currency.All(char.IsLetter) ? currency : "EUR";
    }

    private static string Normalize(string value) =>
        new(value.Trim().ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string Sha256Bytes(byte[] value) =>
        Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    private sealed record Candidate(
        Guid Id,
        int RowNumber,
        DateOnly? TradeDate,
        DateOnly? SettlementDate,
        string? TradeType,
        string? SecurityName,
        string? Isin,
        string? Wkn,
        string? Ticker,
        decimal? Quantity,
        decimal? Price,
        decimal? GrossAmount,
        decimal Amount,
        string Currency,
        decimal Fees,
        decimal Taxes,
        decimal WithholdingTax,
        string? ExternalKey,
        string Fingerprint,
        string Status,
        string? Error,
        string DuplicateStatus);

    private sealed record ExistingSecurity(
        Guid Id,
        string Name,
        string? Isin,
        string? Wkn,
        string? Ticker,
        string Currency);
}
