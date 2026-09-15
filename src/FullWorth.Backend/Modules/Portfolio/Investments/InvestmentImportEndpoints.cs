using System.Text.Json;
using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Portfolio;

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
    string? AssetClass,
    string? SourceProvider,
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

public static class InvestmentImportEndpoints
{
    private const long MaxUploadBytes = 25L * 1024 * 1024;

    public static IEndpointRouteBuilder MapInvestmentImportEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/investment-import").WithTags("Investments", "Import");
        group.MapPost("/detect", Detect);
        group.MapPost("/upload", Upload);
        group.MapGet("/jobs/{jobId:guid}", GetJob);
        group.MapGet("/jobs/{jobId:guid}/summary", Summary);
        group.MapGet("/history", History);
        group.MapGet("/portfolios/{portfolioId:guid}/reconciliation", Reconciliation);
        group.MapPost("/jobs/{jobId:guid}/commit", Commit);
        group.MapPost("/jobs/{jobId:guid}/rollback", Rollback);
        return app;
    }

    private static async Task<IResult> Detect(
        Guid fullWorthSpaceId, HttpRequest request, CurrentUserContext currentUser, InvestmentImportStore store,
        CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await store.CanManageAsync(userId, fullWorthSpaceId, ct))
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        var file = await ReadFileAsync(request, ct);
        if (file.Error is not null) return Results.BadRequest(new { error = file.Error });

        try
        {
            var rows = InvestmentImportFile.Parse(file.FileName!, file.Bytes!);
            if (rows.Count == 0) return Results.BadRequest(new { error = "No data rows found." });

            var headers = rows[0].Keys.ToArray();
            return Results.Ok(new
            {
                fileName = file.FileName,
                headers,
                suggestedMapping = InvestmentImportFile.Suggest(headers),
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
        Guid fullWorthSpaceId, HttpRequest request, CurrentUserContext currentUser, InvestmentImportStore store,
        CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await store.CanManageAsync(userId, fullWorthSpaceId, ct))
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
        if (mapping is null || string.IsNullOrWhiteSpace(mapping.TradeDate)
            || string.IsNullOrWhiteSpace(mapping.TradeType))
            return Results.BadRequest(new { error = "Trade date and transaction type columns are required." });

        await using var stream = new MemoryStream(checked((int)file.Length));
        await file.CopyToAsync(stream, ct);
        var bytes = stream.ToArray();

        List<Dictionary<string, string>> rows;
        try { rows = InvestmentImportFile.Parse(file.FileName, bytes); }
        catch (Exception exception) when (exception is InvalidDataException or FormatException)
        { return Results.BadRequest(new { error = exception.Message }); }
        if (rows.Count == 0) return Results.BadRequest(new { error = "No data rows found." });

        var headers = rows[0].Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var mappedColumns = new[]
        {
            mapping.TradeDate, mapping.TradeType, mapping.SettlementDate, mapping.SecurityName,
            mapping.Isin, mapping.Wkn, mapping.Ticker, mapping.Quantity, mapping.Price, mapping.GrossAmount,
            mapping.Amount, mapping.Currency, mapping.Fees, mapping.Taxes, mapping.WithholdingTax,
            mapping.AssetClass, mapping.ExternalKey
        }.Where(value => !string.IsNullOrWhiteSpace(value));
        if (mappedColumns.Any(column => !headers.Contains(column!)))
            return Results.BadRequest(new { error = "Mapping references an unknown column." });

        var (candidates, errorCount) = InvestmentImportCandidates.Build(rows, mapping);
        var jobId = await store.SaveUploadAsync(userId, fullWorthSpaceId, Path.GetFileName(file.FileName),
            InvestmentImportFile.Sha256Bytes(bytes), candidates, errorCount, ct);

        return Results.Ok(new
        {
            jobId,
            sourceRows = candidates.Count,
            ready = candidates.Count - errorCount,
            errors = errorCount
        });
    }

    private static async Task<IResult> GetJob(
        Guid jobId, Guid fullWorthSpaceId, CurrentUserContext currentUser, InvestmentImportStore store,
        CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await store.OwnsJobAsync(jobId, fullWorthSpaceId, userId, includeCompleted: true, ct))
            return Results.NotFound();
        if (await store.ReadJobAsync(jobId, ct) is not { } job) return Results.NotFound();

        return Results.Ok(new
        {
            jobId,
            fileName = job.FileName,
            status = job.Status,
            sourceRows = job.SourceRowCount,
            ready = job.ReadyCount,
            duplicates = job.DuplicateCount,
            imported = job.ImportedCount,
            errors = job.ErrorCount,
            createdAt = job.CreatedAt,
            completedAt = job.CompletedAt
        });
    }

    private static async Task<IResult> Summary(
        Guid jobId, Guid fullWorthSpaceId, CurrentUserContext currentUser, InvestmentImportStore store,
        CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await store.OwnsJobAsync(jobId, fullWorthSpaceId, userId, includeCompleted: true, ct))
            return Results.NotFound();

        var candidates = await store.CandidatesAsync(jobId, ct);
        var securities = await store.SecuritiesAsync(fullWorthSpaceId, ct);

        var groups = candidates
            .Where(candidate => candidate.Status == "ready"
                && InvestmentImportCandidates.HasSecurityIdentity(candidate))
            .GroupBy(InvestmentImportCandidates.SecurityKey)
            .Select(group =>
            {
                var first = group.First();
                var match = InvestmentImportCandidates.AutoMatch(first, securities);
                return new
                {
                    key = group.Key,
                    name = first.SecurityName,
                    isin = first.Isin,
                    wkn = first.Wkn,
                    ticker = first.Ticker,
                    assetType = first.AssetType,
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
            candidate.AssetType,
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
            securityKey = InvestmentImportCandidates.HasSecurityIdentity(candidate)
                ? InvestmentImportCandidates.SecurityKey(candidate)
                : null
        });

        var transactionTypes = candidates
            .Where(candidate => candidate.Status == "ready" && !string.IsNullOrWhiteSpace(candidate.TradeType))
            .GroupBy(candidate => candidate.TradeType!, StringComparer.OrdinalIgnoreCase)
            .Select(group => new { type = group.Key, count = group.Count(), amount = group.Sum(c => c.Amount) })
            .OrderByDescending(item => item.count)
            .ThenBy(item => item.type)
            .ToArray();

        return Results.Ok(new
        {
            securities = groups,
            transactionTypes,
            ready = candidates.Count(candidate => candidate.Status == "ready"),
            errors = candidates.Count(candidate => candidate.Status == "error"),
            preview
        });
    }

    private static async Task<IResult> History(
        Guid fullWorthSpaceId, int? limit, CurrentUserContext currentUser, InvestmentImportStore store,
        CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await store.CanManageAsync(userId, fullWorthSpaceId, ct))
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        var rows = (await store.ListJobsAsync(fullWorthSpaceId, userId, Math.Clamp(limit ?? 25, 1, 100), ct))
            .Select(row => new
            {
                id = row.Id,
                fileName = row.FileName,
                status = row.Status,
                sourceRows = row.SourceRowCount,
                ready = row.ReadyCount,
                imported = row.ImportedCount,
                duplicates = row.DuplicateCount,
                errors = row.ErrorCount,
                portfolioId = row.PortfolioId,
                portfolioName = row.PortfolioName,
                portfolioCreated = row.PortfolioCreated,
                linkedTrades = row.LinkedTrades,
                createdSecurities = row.CreatedSecurities,
                // Zurueckzunehmen ist nur, was abgeschlossen ist, noch nicht zurueckgenommen wurde und
                // eine Spur hinterlassen hat.
                rollbackAvailable = row.Status == "completed" && row.RolledBackAt is null && row.LinkedTrades > 0,
                createdAt = row.CreatedAt,
                completedAt = row.CompletedAt,
                rolledBackAt = row.RolledBackAt
            });
        return Results.Ok(rows);
    }

    private static async Task<IResult> Reconciliation(
        Guid portfolioId, Guid fullWorthSpaceId, CurrentUserContext currentUser, InvestmentImportStore store,
        CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await store.CanManageAsync(userId, fullWorthSpaceId, ct))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (!await store.PortfolioExistsAsync(fullWorthSpaceId, portfolioId, ct)) return Results.NotFound();

        if (await store.PortfolioIdentityAsync(fullWorthSpaceId, portfolioId, ct) is not { } identity)
            return Results.Ok(InvestmentImportReconciliation.Missing(portfolioId));

        return Results.Ok(InvestmentImportReconciliation.Build(portfolioId, identity.Name, identity.Currency,
            await store.LedgerAsync(fullWorthSpaceId, portfolioId, ct)));
    }

    private static async Task<IResult> Commit(
        Guid jobId, Guid fullWorthSpaceId, InvestmentImportCommitWrite request, CurrentUserContext currentUser,
        InvestmentImportStore store, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await store.OwnsJobAsync(jobId, fullWorthSpaceId, userId, includeCompleted: false, ct))
            return Results.NotFound();
        if (!await store.CanManageAsync(userId, fullWorthSpaceId, ct))
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        if (request.PortfolioId.HasValue && request.CreatePortfolio is not null)
            return Results.BadRequest(new { error = "Choose either an existing portfolio or create a new one, not both." });
        if (!request.PortfolioId.HasValue && request.CreatePortfolio is null)
            return Results.BadRequest(new { error = "Choose an existing portfolio or provide a new portfolio." });

        (string Name, string Currency, string? Provider)? newPortfolio = null;
        if (request.PortfolioId.HasValue)
        {
            if (!await store.CanWritePortfolioAsync(userId, fullWorthSpaceId, request.PortfolioId.Value, ct))
                return Results.BadRequest(new { error = "Target portfolio is inaccessible or not writable." });
        }
        else
        {
            var name = request.CreatePortfolio!.Name?.Trim();
            var currency = request.CreatePortfolio.Currency?.Trim().ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(currency)
                || currency.Length != 3 || !currency.All(char.IsLetter))
                return Results.BadRequest(new { error = "New portfolio requires a name and a three-letter currency." });

            var provider = request.CreatePortfolio.ProviderName?.Trim();
            newPortfolio = (name, currency, string.IsNullOrWhiteSpace(provider) ? null : provider);
        }

        var selectedIds = request.CandidateIds?.ToHashSet();
        var candidates = (await store.CandidatesAsync(jobId, ct))
            .Where(candidate => candidate.Status == "ready"
                && (selectedIds is null || selectedIds.Contains(candidate.Id)))
            // Die Reihenfolge ist Teil der Richtigkeit: erst kaufen, dann splitten, dann verkaufen.
            .OrderBy(candidate => candidate.TradeDate)
            .ThenBy(candidate => InvestmentImportCandidates.TradeOrderPriority(candidate.TradeType))
            .ThenBy(candidate => candidate.RowNumber)
            .ToList();
        if (candidates.Count == 0) return Results.BadRequest(new { error = "No ready investment rows selected." });

        var securities = await store.SecuritiesAsync(fullWorthSpaceId, ct);
        var mappings = request.SecurityMappings ?? new Dictionary<string, Guid?>();
        var mappedIds = mappings.Values.Where(value => value.HasValue).Select(value => value!.Value).Distinct();
        if (mappedIds.Any(id => securities.All(security => security.Id != id)))
            return Results.BadRequest(new { error = "A security mapping belongs to another FullWorth Space or does not exist." });

        // Die Zuordnung des Benutzers gewinnt; wo keine steht, wird geraten - und zwar nur eindeutig.
        var resolution = new Dictionary<string, ImportSecurity?>(StringComparer.Ordinal);
        foreach (var group in candidates
            .Where(InvestmentImportCandidates.HasSecurityIdentity)
            .GroupBy(InvestmentImportCandidates.SecurityKey))
        {
            ImportSecurity? resolved = null;
            if (mappings.TryGetValue(group.Key, out var mapped) && mapped.HasValue)
                resolved = securities.SingleOrDefault(security => security.Id == mapped.Value);
            resolution[group.Key] = resolved ?? InvestmentImportCandidates.AutoMatch(group.First(), securities);
        }

        var unresolved = candidates
            .Where(candidate => InvestmentImportCandidates.SecurityRequiredTypes.Contains(candidate.TradeType!))
            .Where(candidate =>
            {
                var key = InvestmentImportCandidates.HasSecurityIdentity(candidate)
                    ? InvestmentImportCandidates.SecurityKey(candidate)
                    : null;
                return (key is null ? null : resolution.GetValueOrDefault(key)) is null
                    && (!request.CreateMissingSecurities || key is null);
            })
            .Select(candidate => new
            {
                candidate.Id, candidate.RowNumber, candidate.SecurityName,
                candidate.Isin, candidate.Wkn, candidate.Ticker
            })
            .ToArray();
        if (unresolved.Length > 0)
            return Results.BadRequest(new
            {
                error = "Some investment rows require an unresolved security.",
                unresolvedSecurities = unresolved
            });

        var result = await store.ApplyImportAsync(userId, fullWorthSpaceId, jobId, request.PortfolioId,
            newPortfolio, candidates, resolution, securities, request.CreateMissingSecurities, ct);
        if (result is null)
            return Results.Conflict(new
            {
                error = "Investment import could not be applied. No investment transactions were imported. Check trade order, quantities and mappings."
            });

        return Results.Ok(new
        {
            imported = result.Imported,
            duplicates = result.Duplicates,
            total = candidates.Count,
            portfolioId = result.PortfolioId,
            portfolioCreated = result.PortfolioCreated,
            reconciliation = InvestmentImportReconciliation.Build(
                result.PortfolioId, result.PortfolioName, result.PortfolioCurrency, result.Trades)
        });
    }

    private static async Task<IResult> Rollback(
        Guid jobId, Guid fullWorthSpaceId, CurrentUserContext currentUser, InvestmentImportStore store,
        CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await store.CanManageAsync(userId, fullWorthSpaceId, ct))
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        if (await store.JobStateAsync(jobId, fullWorthSpaceId, userId, ct) is not { } job)
            return Results.NotFound();
        if (job.RolledBackAt is not null || job.Status == "rolled_back")
            return Results.BadRequest(new { error = "This investment import has already been rolled back." });
        if (job.Status != "completed")
            return Results.BadRequest(new { error = "Only completed investment imports can be rolled back." });
        if (await store.LinkedTradeCountAsync(jobId, ct) == 0)
            return Results.BadRequest(new { error = "This import predates exact provenance tracking or created no trades, so automatic rollback is not available." });

        var result = await store.RollbackAsync(
            userId, fullWorthSpaceId, jobId, job.PortfolioCreated, job.PortfolioId, ct);
        if (result is null)
            return Results.Conflict(new
            {
                error = "Rollback would leave the investment ledger inconsistent or a linked resource is still required. Nothing was removed."
            });

        return Results.Ok(new
        {
            jobId,
            removedTrades = result.RemovedTrades,
            removedSecurities = result.RemovedSecurities,
            keptSecurities = result.KeptSecurities,
            portfolioRemoved = result.PortfolioRemoved
        });
    }

    private static async Task<(byte[]? Bytes, string? FileName, string? Error)> ReadFileAsync(
        HttpRequest request, CancellationToken ct)
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
}
