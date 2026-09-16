using System.Data;
using System.Globalization;
using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Audit;
using FullWorth.Backend.Security;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Portfolio;

/// <summary>Der Stand eines Importlaufs.</summary>
public sealed record ImportJobRow(
    string FileName, string Status, int SourceRowCount, int ReadyCount, int DuplicateCount, int ImportedCount,
    int ErrorCount, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt);

/// <summary>Ein Importlauf in der Liste, mit dem, was von ihm uebrig ist.</summary>
public sealed record ImportHistoryRow(
    Guid Id, string FileName, string Status, int SourceRowCount, int ReadyCount, int ImportedCount,
    int DuplicateCount, int ErrorCount, Guid? PortfolioId, string? PortfolioName, bool PortfolioCreated,
    int LinkedTrades, int CreatedSecurities, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt,
    DateTimeOffset? RolledBackAt);

/// <summary>Was ein Importlauf hinterlassen hat - die Frage vor jedem Ruecknehmen.</summary>
public sealed record ImportJobState(
    string Status, Guid? PortfolioId, bool PortfolioCreated, DateTimeOffset? RolledBackAt);

/// <summary>Ein Handel, so weit der Abgleich ihn nachrechnet.</summary>
public sealed record ImportLedgerTrade(
    string TradeType, Guid? SecurityId, string? SecurityName, string? Isin, decimal Quantity, decimal Amount,
    string Currency, decimal Fees, decimal Taxes, decimal WithholdingTax);

/// <summary>Was ein Einspielen bewirkt hat, und wie das Depot danach dasteht.</summary>
public sealed record ImportApplyResult(
    int Imported, int Duplicates, Guid PortfolioId, bool PortfolioCreated, string PortfolioName,
    string PortfolioCurrency, IReadOnlyList<ImportLedgerTrade> Trades);

/// <summary>Duplikatstatus einer Importzeile vor dem eigentlichen Einspielen.</summary>
public sealed record InvestmentImportDuplicatePreview(Guid Id, string Status, string? Reason);

/// <summary>Was ein Ruecknehmen entfernt hat.</summary>
public sealed record ImportRollbackResult(
    int RemovedTrades, int RemovedSecurities, int KeptSecurities, bool PortfolioRemoved);

/// <summary>
/// Importlaeufe, ihre Zeilen und das Einspielen selbst.
///
/// Der Kern sind zwei Methoden, die eine ganze Transaktion umschliessen:
/// <see cref="ApplyImportAsync"/> und <see cref="RollbackAsync"/>. Beide laufen auf
/// <c>Serializable</c>, weil sie den Bestand eines Depots fortschreiben - und ein Bestand, der
/// zwischen zwei Anweisungen von jemand anderem veraendert wird, ergibt eine Depothistorie, die
/// niemand mehr nachrechnen kann. Beide geben bei einem Fehler nichts halb Fertiges zurueck, sondern
/// nehmen alles zurueck.
///
/// Jeder eingespielte Handel und jedes dabei angelegte Wertpapier bekommt einen Eintrag in
/// <c>InvestmentImportTradeLinks</c> bzw. <c>InvestmentImportSecurityLinks</c>. Ohne diese Spur
/// waere ein Import nicht zurueckzunehmen - man wuesste nicht, was von ihm stammt.
/// </summary>
public sealed class InvestmentImportStore(FullWorthDbContext db, AuditService audit)
{
    private readonly record struct DuplicateClassification(string StableKey, string? Reason)
    {
        public bool IsDuplicate => Reason is not null;
    }

    public Task<bool> CanManageAsync(Guid userId, Guid space, CancellationToken ct) =>
        SpaceCapabilities.HasCapabilityAsync(db, userId, space, "investments.manage", ct);

    /// <summary>
    /// Ein Importlauf gehoert dem, der ihn hochgeladen hat - nicht dem Bereich. Ein abgeschlossener
    /// Lauf darf noch gelesen, aber nicht mehr eingespielt werden.
    /// </summary>
    public async Task<bool> OwnsJobAsync(
        Guid jobId, Guid space, Guid userId, bool includeCompleted, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        var sql = "SELECT EXISTS(SELECT 1 FROM \"InvestmentImportJobs\" WHERE \"Id\"=@id AND \"FullWorthSpaceId\"=@space AND \"UserId\"=@user"
            + (includeCompleted ? ")" : " AND \"Status\" NOT IN ('completed','cancelled'))");
        await using var command = RawSql.Command(connection, sql,
            ("@id", jobId), ("@space", space), ("@user", userId));
        return Convert.ToBoolean(await command.ExecuteScalarAsync(ct));
    }

    /// <summary>Lauf und Zeilen gehoeren zusammen: entweder beide da oder keiner.</summary>
    public async Task<Guid> SaveUploadAsync(
        Guid userId, Guid space, string fileName, string fileSha, IReadOnlyList<ImportCandidate> candidates,
        int errorCount, CancellationToken ct)
    {
        var jobId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var connection = await RawSql.OpenAsync(db, ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        await using (var command = RawSql.Command(connection, """
INSERT INTO "InvestmentImportJobs"
("Id","FullWorthSpaceId","UserId","FileName","FileSha256","Status","SourceRowCount","ReadyCount","DuplicateCount","ImportedCount","ErrorCount","CreatedAt","UpdatedAt")
VALUES (@id,@space,@user,@file,@sha,@status,@source,@ready,0,0,@errors,@now,@now)
""", ("@id", jobId), ("@space", space), ("@user", userId), ("@file", fileName), ("@sha", fileSha),
            ("@status", errorCount == candidates.Count ? "failed" : "review"),
            ("@source", candidates.Count), ("@ready", candidates.Count - errorCount),
            ("@errors", errorCount), ("@now", now)))
            await command.ExecuteNonQueryAsync(ct);

        foreach (var candidate in candidates)
        {
            await using var command = RawSql.Command(connection, """
INSERT INTO "InvestmentImportCandidates"
("Id","ImportJobId","RowNumber","TradeDate","SettlementDate","TradeType","SecurityName","Isin","Wkn","Ticker","AssetType",
 "Quantity","Price","GrossAmount","Amount","Currency","Fees","Taxes","WithholdingTax","ExternalKey","RowFingerprint",
 "ValidationStatus","DuplicateStatus","ValidationError","CreatedAt")
VALUES (@id,@job,@row,@tradeDate,@settlement,@type,@name,@isin,@wkn,@ticker,@assetType,@quantity,@price,@gross,@amount,@currency,@fees,@taxes,@withholding,@external,@fingerprint,@status,'new',@error,@now)
""", ("@id", candidate.Id), ("@job", jobId), ("@row", candidate.RowNumber),
                ("@tradeDate", candidate.TradeDate), ("@settlement", candidate.SettlementDate),
                ("@type", candidate.TradeType), ("@name", candidate.SecurityName), ("@isin", candidate.Isin),
                ("@wkn", candidate.Wkn), ("@ticker", candidate.Ticker), ("@assetType", candidate.AssetType),
                ("@quantity", candidate.Quantity), ("@price", candidate.Price), ("@gross", candidate.GrossAmount),
                ("@amount", candidate.Amount), ("@currency", candidate.Currency), ("@fees", candidate.Fees),
                ("@taxes", candidate.Taxes), ("@withholding", candidate.WithholdingTax),
                ("@external", candidate.ExternalKey), ("@fingerprint", candidate.Fingerprint),
                ("@status", candidate.Status), ("@error", candidate.Error), ("@now", now));
            await command.ExecuteNonQueryAsync(ct);
        }

        audit.Record(space, userId, "investment.import.uploaded", "InvestmentImportJob", jobId);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return jobId;
    }

    public async Task<ImportJobRow?> ReadJobAsync(Guid jobId, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        await using var command = RawSql.Command(connection, """
SELECT "FileName","Status","SourceRowCount","ReadyCount","DuplicateCount","ImportedCount","ErrorCount","CreatedAt","CompletedAt"
FROM "InvestmentImportJobs" WHERE "Id"=@id
""", ("@id", jobId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        return new ImportJobRow(
            RawSql.String(reader, "FileName"), RawSql.String(reader, "Status"),
            RawSql.Int(reader, "SourceRowCount"), RawSql.Int(reader, "ReadyCount"),
            RawSql.Int(reader, "DuplicateCount"), RawSql.Int(reader, "ImportedCount"),
            RawSql.Int(reader, "ErrorCount"), RawSql.Timestamp(reader, "CreatedAt"),
            RawSql.NullableTimestamp(reader, "CompletedAt"));
    }

    public async Task<List<ImportHistoryRow>> ListJobsAsync(
        Guid space, Guid userId, int take, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        await using var command = RawSql.Command(connection, """
SELECT j."Id",j."FileName",j."Status",j."SourceRowCount",j."ReadyCount",j."ImportedCount",j."DuplicateCount",j."ErrorCount",
       j."PortfolioId",j."PortfolioCreated",j."CreatedAt",j."CompletedAt",j."RolledBackAt",p."Name" AS "PortfolioName",
       (SELECT count(*) FROM "InvestmentImportTradeLinks" l WHERE l."ImportJobId"=j."Id") AS "LinkedTrades",
       (SELECT count(*) FROM "InvestmentImportSecurityLinks" l WHERE l."ImportJobId"=j."Id") AS "CreatedSecurities"
FROM "InvestmentImportJobs" j
LEFT JOIN "InvestmentPortfolios" p ON p."Id"=j."PortfolioId"
WHERE j."FullWorthSpaceId"=@space AND j."UserId"=@user
ORDER BY j."CreatedAt" DESC
LIMIT @limit
""", ("@space", space), ("@user", userId), ("@limit", take));
        await using var reader = await command.ExecuteReaderAsync(ct);

        var rows = new List<ImportHistoryRow>();
        while (await reader.ReadAsync(ct))
            rows.Add(new ImportHistoryRow(
                RawSql.Guid(reader, "Id"), RawSql.String(reader, "FileName"), RawSql.String(reader, "Status"),
                RawSql.Int(reader, "SourceRowCount"), RawSql.Int(reader, "ReadyCount"),
                RawSql.Int(reader, "ImportedCount"), RawSql.Int(reader, "DuplicateCount"),
                RawSql.Int(reader, "ErrorCount"), RawSql.NullableGuid(reader, "PortfolioId"),
                RawSql.NullableString(reader, "PortfolioName"),
                reader.GetBoolean(reader.GetOrdinal("PortfolioCreated")),
                Convert.ToInt32(reader["LinkedTrades"], CultureInfo.InvariantCulture),
                Convert.ToInt32(reader["CreatedSecurities"], CultureInfo.InvariantCulture),
                RawSql.Timestamp(reader, "CreatedAt"), RawSql.NullableTimestamp(reader, "CompletedAt"),
                RawSql.NullableTimestamp(reader, "RolledBackAt")));
        return rows;
    }

    public async Task<List<ImportCandidate>> CandidatesAsync(Guid jobId, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        await using var command = RawSql.Command(connection, """
SELECT "Id","RowNumber","TradeDate","SettlementDate","TradeType","SecurityName","Isin","Wkn","Ticker","AssetType","Quantity","Price",
 "GrossAmount","Amount","Currency","Fees","Taxes","WithholdingTax","ExternalKey","RowFingerprint","ValidationStatus","DuplicateStatus","ValidationError"
FROM "InvestmentImportCandidates" WHERE "ImportJobId"=@job ORDER BY "RowNumber"
""", ("@job", jobId));
        await using var reader = await command.ExecuteReaderAsync(ct);

        var rows = new List<ImportCandidate>();
        while (await reader.ReadAsync(ct))
            rows.Add(new ImportCandidate(
                RawSql.Guid(reader, "Id"), RawSql.Int(reader, "RowNumber"),
                RawSql.NullableDate(reader, "TradeDate"), RawSql.NullableDate(reader, "SettlementDate"),
                RawSql.NullableString(reader, "TradeType"), RawSql.NullableString(reader, "SecurityName"),
                RawSql.NullableString(reader, "Isin"), RawSql.NullableString(reader, "Wkn"),
                RawSql.NullableString(reader, "Ticker"), RawSql.NullableString(reader, "AssetType"),
                RawSql.NullableDecimal(reader, "Quantity"), RawSql.NullableDecimal(reader, "Price"),
                RawSql.NullableDecimal(reader, "GrossAmount"), RawSql.Decimal(reader, "Amount"),
                RawSql.String(reader, "Currency"), RawSql.Decimal(reader, "Fees"),
                RawSql.Decimal(reader, "Taxes"), RawSql.Decimal(reader, "WithholdingTax"),
                RawSql.NullableString(reader, "ExternalKey"), RawSql.String(reader, "RowFingerprint"),
                RawSql.String(reader, "ValidationStatus"), RawSql.NullableString(reader, "ValidationError"),
                RawSql.String(reader, "DuplicateStatus")));
        return rows;
    }

    public async Task<List<InvestmentImportDuplicatePreview>> DuplicatePreviewAsync(
        Guid? portfolioId, IReadOnlyList<ImportCandidate> candidates, CancellationToken ct)
    {
        var seenStableKeys = new HashSet<string>(StringComparer.Ordinal);
        var rows = new List<InvestmentImportDuplicatePreview>(candidates.Count);
        foreach (var candidate in candidates)
        {
            var duplicate = await ClassifyDuplicateAsync(portfolioId, candidate, seenStableKeys, ct);
            rows.Add(new InvestmentImportDuplicatePreview(
                candidate.Id, duplicate.IsDuplicate ? "duplicate" : "new", duplicate.Reason));
        }
        return rows;
    }

    public async Task<List<ImportSecurity>> SecuritiesAsync(Guid space, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        await using var command = RawSql.Command(connection, """
SELECT "Id","Name","Isin","Wkn","Ticker","Currency"
FROM "Securities" WHERE "FullWorthSpaceId"=@space AND "IsActive"=true
""", ("@space", space));
        await using var reader = await command.ExecuteReaderAsync(ct);

        var rows = new List<ImportSecurity>();
        while (await reader.ReadAsync(ct))
            rows.Add(new ImportSecurity(
                RawSql.Guid(reader, "Id"), RawSql.String(reader, "Name"), RawSql.NullableString(reader, "Isin"),
                RawSql.NullableString(reader, "Wkn"), RawSql.NullableString(reader, "Ticker"),
                RawSql.String(reader, "Currency")));
        return rows;
    }

    public async Task<bool> PortfolioExistsAsync(Guid space, Guid portfolioId, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        await using var command = RawSql.Command(connection,
            "SELECT EXISTS(SELECT 1 FROM \"InvestmentPortfolios\" WHERE \"Id\"=@portfolio AND \"FullWorthSpaceId\"=@space)",
            ("@portfolio", portfolioId), ("@space", space));
        return Convert.ToBoolean(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    /// <summary>Ein Depot ohne Konto steht allen offen; mit Konto entscheidet das Konto.</summary>
    public async Task<bool> CanWritePortfolioAsync(
        Guid userId, Guid space, Guid portfolioId, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        await using var command = RawSql.Command(connection,
            "SELECT \"AccountId\" FROM \"InvestmentPortfolios\" WHERE \"Id\"=@id AND \"FullWorthSpaceId\"=@space AND \"IsArchived\"=false",
            ("@id", portfolioId), ("@space", space));

        var account = await command.ExecuteScalarAsync(ct);
        if (account is null) return false;
        if (account is DBNull) return true;
        return (await RawSql.WritableAccountIdsAsync(db, userId, space, ct)).Contains((Guid)account);
    }

    public async Task<ImportJobState?> JobStateAsync(Guid jobId, Guid space, Guid userId, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        await using var command = RawSql.Command(connection, """
SELECT "Status","PortfolioId","PortfolioCreated","RolledBackAt"
FROM "InvestmentImportJobs" WHERE "Id"=@job AND "FullWorthSpaceId"=@space AND "UserId"=@user
""", ("@job", jobId), ("@space", space), ("@user", userId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        return new ImportJobState(
            RawSql.String(reader, "Status"), RawSql.NullableGuid(reader, "PortfolioId"),
            reader.GetBoolean(reader.GetOrdinal("PortfolioCreated")),
            RawSql.NullableTimestamp(reader, "RolledBackAt"));
    }

    public async Task<int> LinkedTradeCountAsync(Guid jobId, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        await using var command = RawSql.Command(connection,
            "SELECT count(*) FROM \"InvestmentImportTradeLinks\" WHERE \"ImportJobId\"=@job", ("@job", jobId));
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    public async Task<(string Name, string Currency)?> PortfolioIdentityAsync(
        Guid space, Guid portfolioId, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        await using var command = RawSql.Command(connection,
            "SELECT \"Name\",\"Currency\" FROM \"InvestmentPortfolios\" WHERE \"Id\"=@portfolio AND \"FullWorthSpaceId\"=@space",
            ("@portfolio", portfolioId), ("@space", space));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? (RawSql.String(reader, "Name"), RawSql.String(reader, "Currency"))
            : null;
    }

    /// <summary>Alle Handel eines Depots in Wirkungsreihenfolge - die Grundlage jedes Abgleichs.</summary>
    public Task<List<ImportLedgerTrade>> LedgerAsync(Guid space, Guid portfolioId, CancellationToken ct) =>
        ReadLedgerAsync(space, portfolioId, ct);

    /// <summary>
    /// Das Einspielen selbst. Gibt <c>null</c> zurueck, wenn nichts davon bestand haben konnte - dann
    /// ist auch nichts geschrieben worden.
    /// </summary>
    public async Task<ImportApplyResult?> ApplyImportAsync(
        Guid userId,
        Guid space,
        Guid jobId,
        Guid? existingPortfolioId,
        (string Name, string Currency, string? Provider)? newPortfolio,
        IReadOnlyList<ImportCandidate> candidates,
        Dictionary<string, ImportSecurity?> resolution,
        List<ImportSecurity> securities,
        bool createMissingSecurities,
        CancellationToken ct)
    {
        var imported = 0;
        var duplicates = 0;
        var seenStableKeys = new HashSet<string>(StringComparer.Ordinal);

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            var connection = await RawSql.OpenAsync(db, ct);
            var portfolioCreated = false;
            var portfolioId = existingPortfolioId ?? Guid.NewGuid();

            if (existingPortfolioId is null)
            {
                await using var createPortfolio = RawSql.Command(connection, """
INSERT INTO "InvestmentPortfolios"
("Id","FullWorthSpaceId","Name","Currency","AccountId","BenchmarkSecurityId","ProviderName","IsManual","IncludeInNetWorth","IsArchived","CreatedAt","UpdatedAt")
VALUES (@id,@space,@name,@currency,NULL,NULL,@provider,true,true,false,@now,@now)
""", ("@id", portfolioId), ("@space", space), ("@name", newPortfolio!.Value.Name),
                    ("@currency", newPortfolio.Value.Currency), ("@provider", newPortfolio.Value.Provider),
                    ("@now", DateTimeOffset.UtcNow));
                await createPortfolio.ExecuteNonQueryAsync(ct);
                portfolioCreated = true;
                audit.Record(space, userId, "investment.portfolio.created", "InvestmentPortfolio", portfolioId);
            }

            if (createMissingSecurities)
                foreach (var group in candidates
                    .Where(InvestmentImportCandidates.HasSecurityIdentity)
                    .GroupBy(InvestmentImportCandidates.SecurityKey))
                {
                    if (resolution.GetValueOrDefault(group.Key) is not null) continue;

                    var first = group.First();
                    var created = new ImportSecurity(
                        Guid.NewGuid(),
                        string.IsNullOrWhiteSpace(first.SecurityName)
                            ? first.Isin ?? first.Wkn ?? first.Ticker ?? "Imported security"
                            : first.SecurityName!,
                        first.Isin, first.Wkn, first.Ticker, first.Currency);

                    await using (var createSecurity = RawSql.Command(connection, """
INSERT INTO "Securities"
("Id","FullWorthSpaceId","Name","Isin","Wkn","Ticker","AssetType","Currency","ProviderKey","IsActive","CreatedAt","UpdatedAt")
VALUES (@id,@space,@name,@isin,@wkn,@ticker,@assetType,@currency,'investment-import',true,@now,@now)
""", ("@id", created.Id), ("@space", space), ("@name", created.Name), ("@isin", created.Isin),
                        ("@wkn", created.Wkn), ("@ticker", created.Ticker),
                        ("@assetType", first.AssetType ?? "other"), ("@currency", created.Currency),
                        ("@now", DateTimeOffset.UtcNow)))
                        await createSecurity.ExecuteNonQueryAsync(ct);

                    await using (var linkSecurity = RawSql.Command(connection, """
INSERT INTO "InvestmentImportSecurityLinks" ("ImportJobId","SecurityId","CreatedAt")
VALUES (@job,@security,@now)
ON CONFLICT DO NOTHING
""", ("@job", jobId), ("@security", created.Id), ("@now", DateTimeOffset.UtcNow)))
                        await linkSecurity.ExecuteNonQueryAsync(ct);

                    securities.Add(created);
                    resolution[group.Key] = created;
                }

            foreach (var candidate in candidates)
            {
                var duplicate = await ClassifyDuplicateAsync(portfolioId, candidate, seenStableKeys, ct);
                if (duplicate.IsDuplicate)
                {
                    duplicates++;
                    await MarkCandidateAsync(candidate.Id, "duplicate", ct);
                    continue;
                }

                Guid? securityId = null;
                if (InvestmentImportCandidates.HasSecurityIdentity(candidate))
                    securityId = resolution.GetValueOrDefault(
                        InvestmentImportCandidates.SecurityKey(candidate))?.Id;
                if (InvestmentImportCandidates.SecurityRequiredTypes.Contains(candidate.TradeType!)
                    && !securityId.HasValue)
                    throw new InvalidOperationException("Required security could not be resolved during commit.");

                var now = DateTimeOffset.UtcNow;
                var tradeId = Guid.NewGuid();
                await using (var insert = RawSql.Command(connection, """
INSERT INTO "InvestmentTrades"
("Id","FullWorthSpaceId","PortfolioId","SecurityId","TradeType","TradeDate","SettlementDate","Quantity","Price","GrossAmount","Amount","Currency","Fees","Taxes","WithholdingTax","Source","ExternalKey","Notes","CreatedAt","UpdatedAt")
VALUES (@id,@space,@portfolio,@security,@type,@tradeDate,@settlement,@quantity,@price,@gross,@amount,@currency,@fees,@taxes,@withholding,'import',@external,@notes,@now,@now)
""", ("@id", tradeId), ("@space", space), ("@portfolio", portfolioId), ("@security", securityId),
                    ("@type", candidate.TradeType), ("@tradeDate", candidate.TradeDate),
                    ("@settlement", candidate.SettlementDate), ("@quantity", candidate.Quantity),
                    ("@price", candidate.Price), ("@gross", candidate.GrossAmount),
                    ("@amount", candidate.Amount), ("@currency", candidate.Currency),
                    ("@fees", candidate.Fees), ("@taxes", candidate.Taxes),
                    ("@withholding", candidate.WithholdingTax), ("@external", duplicate.StableKey),
                    ("@notes", $"Imported row {candidate.RowNumber}"), ("@now", now)))
                    await insert.ExecuteNonQueryAsync(ct);

                await using (var linkTrade = RawSql.Command(connection, """
INSERT INTO "InvestmentImportTradeLinks" ("ImportJobId","TradeId","CreatedAt")
VALUES (@job,@trade,@now)
""", ("@job", jobId), ("@trade", tradeId), ("@now", now)))
                    await linkTrade.ExecuteNonQueryAsync(ct);

                imported++;
                await MarkCandidateAsync(candidate.Id, "imported", ct);
            }

            await using (var updateJob = RawSql.Command(connection, """
UPDATE "InvestmentImportJobs"
SET "Status"='completed',"ImportedCount"=@imported,"DuplicateCount"=@duplicates,
    "PortfolioId"=@portfolio,"PortfolioCreated"=@portfolioCreated,
    "UpdatedAt"=@now,"CompletedAt"=@now
WHERE "Id"=@id
""", ("@imported", imported), ("@duplicates", duplicates), ("@portfolio", portfolioId),
                ("@portfolioCreated", portfolioCreated), ("@now", DateTimeOffset.UtcNow), ("@id", jobId)))
                await updateJob.ExecuteNonQueryAsync(ct);

            audit.Record(space, userId, "investment.import.completed", "InvestmentImportJob", jobId);
            await db.SaveChangesAsync(ct);

            // Der Abgleich wird noch innerhalb der Transaktion gelesen: er beschreibt genau den Stand,
            // den dieser Import hergestellt hat.
            var identity = await PortfolioIdentityAsync(space, portfolioId, ct);
            var ledger = await ReadLedgerAsync(space, portfolioId, ct);
            await transaction.CommitAsync(ct);

            return new ImportApplyResult(imported, duplicates, portfolioId, portfolioCreated,
                identity?.Name ?? string.Empty, identity?.Currency ?? string.Empty, ledger);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            return null;
        }
    }

    /// <summary>
    /// Einen Import zuruecknehmen. Ein angelegtes Wertpapier oder Depot verschwindet nur, wenn
    /// nichts anderes mehr daran haengt - fremde Kurse, Merklisten oder Handel wiegen schwerer als
    /// die Sauberkeit. Gibt <c>null</c> zurueck, wenn nichts entfernt werden konnte.
    /// </summary>
    public async Task<ImportRollbackResult?> RollbackAsync(
        Guid userId, Guid space, Guid jobId, bool portfolioCreated, Guid? portfolioId, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            var connection = await RawSql.OpenAsync(db, ct);
            await using (var defer = RawSql.Command(connection, "SET CONSTRAINTS ALL DEFERRED"))
                await defer.ExecuteNonQueryAsync(ct);

            var createdSecurityIds = new List<Guid>();
            await using (var securities = RawSql.Command(connection,
                "SELECT \"SecurityId\" FROM \"InvestmentImportSecurityLinks\" WHERE \"ImportJobId\"=@job",
                ("@job", jobId)))
            await using (var securityReader = await securities.ExecuteReaderAsync(ct))
                while (await securityReader.ReadAsync(ct))
                    createdSecurityIds.Add(RawSql.Guid(securityReader, "SecurityId"));

            int removedTrades;
            await using (var removeTrades = RawSql.Command(connection, """
DELETE FROM "InvestmentTrades" t
USING "InvestmentImportTradeLinks" l
WHERE l."ImportJobId"=@job AND l."TradeId"=t."Id" AND t."FullWorthSpaceId"=@space
""", ("@job", jobId), ("@space", space)))
                removedTrades = await removeTrades.ExecuteNonQueryAsync(ct);

            var removedSecurities = 0;
            foreach (var securityId in createdSecurityIds)
            {
                await using var removeSecurity = RawSql.Command(connection, """
DELETE FROM "Securities" s
WHERE s."Id"=@security AND s."FullWorthSpaceId"=@space
  AND NOT EXISTS (SELECT 1 FROM "InvestmentTrades" t WHERE t."SecurityId"=s."Id")
  AND NOT EXISTS (SELECT 1 FROM "SecurityPrices" p WHERE p."SecurityId"=s."Id")
  AND NOT EXISTS (SELECT 1 FROM "WatchlistItems" w WHERE w."SecurityId"=s."Id")
  AND NOT EXISTS (SELECT 1 FROM "InvestmentPortfolios" p WHERE p."BenchmarkSecurityId"=s."Id")
""", ("@security", securityId), ("@space", space));
                removedSecurities += await removeSecurity.ExecuteNonQueryAsync(ct);
            }

            var portfolioRemoved = false;
            if (portfolioCreated && portfolioId.HasValue)
            {
                await using var removePortfolio = RawSql.Command(connection, """
DELETE FROM "InvestmentPortfolios" p
WHERE p."Id"=@portfolio AND p."FullWorthSpaceId"=@space
  AND NOT EXISTS (SELECT 1 FROM "InvestmentTrades" t WHERE t."PortfolioId"=p."Id")
""", ("@portfolio", portfolioId.Value), ("@space", space));
                portfolioRemoved = await removePortfolio.ExecuteNonQueryAsync(ct) > 0;
            }

            var now = DateTimeOffset.UtcNow;
            await using (var candidates = RawSql.Command(connection, """
UPDATE "InvestmentImportCandidates"
SET "DuplicateStatus"='rolled_back'
WHERE "ImportJobId"=@job AND "DuplicateStatus"='imported'
""", ("@job", jobId)))
                await candidates.ExecuteNonQueryAsync(ct);

            await using (var update = RawSql.Command(connection, """
UPDATE "InvestmentImportJobs"
SET "Status"='rolled_back',"RolledBackAt"=@now,"UpdatedAt"=@now
WHERE "Id"=@job
""", ("@job", jobId), ("@now", now)))
                await update.ExecuteNonQueryAsync(ct);

            audit.Record(space, userId, "investment.import.rolled_back", "InvestmentImportJob", jobId);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            return new ImportRollbackResult(removedTrades, removedSecurities,
                createdSecurityIds.Count - removedSecurities, portfolioRemoved);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
        catch
        {
            // The deferred ledger-integrity trigger (TR_InvestmentTrades_ValidateLedger) can only fail at
            // COMMIT, which already aborts the transaction server-side. Rolling it back again then throws a
            // secondary "transaction has completed" error that would surface as a 500, so swallow it and
            // still report failure cleanly — nothing was persisted.
            try { await transaction.RollbackAsync(ct); }
            catch { /* transaction already rolled back by the failed commit */ }
            return null;
        }
    }

    private async Task<List<ImportLedgerTrade>> ReadLedgerAsync(
        Guid space, Guid portfolioId, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        await using var command = RawSql.Command(connection, """
SELECT t."Id",t."SecurityId",t."TradeType",t."TradeDate",t."Quantity",t."Amount",t."Currency",
       t."Fees",t."Taxes",t."WithholdingTax",s."Name" AS "SecurityName",s."Isin"
FROM "InvestmentTrades" t
LEFT JOIN "Securities" s ON s."Id"=t."SecurityId"
WHERE t."PortfolioId"=@portfolio AND t."FullWorthSpaceId"=@space
ORDER BY t."TradeDate",t."CreatedAt",t."Id"
""", ("@portfolio", portfolioId), ("@space", space));
        await using var reader = await command.ExecuteReaderAsync(ct);

        var rows = new List<ImportLedgerTrade>();
        while (await reader.ReadAsync(ct))
            rows.Add(new ImportLedgerTrade(
                RawSql.String(reader, "TradeType"), RawSql.NullableGuid(reader, "SecurityId"),
                RawSql.NullableString(reader, "SecurityName"), RawSql.NullableString(reader, "Isin"),
                RawSql.NullableDecimal(reader, "Quantity") ?? 0m, RawSql.Decimal(reader, "Amount"),
                RawSql.String(reader, "Currency"), RawSql.Decimal(reader, "Fees"),
                RawSql.Decimal(reader, "Taxes"), RawSql.Decimal(reader, "WithholdingTax")));
        return rows;
    }

    private async Task<DuplicateClassification> ClassifyDuplicateAsync(
        Guid? portfolioId, ImportCandidate candidate, HashSet<string> seenStableKeys, CancellationToken ct)
    {
        var stableKey = InvestmentImportCandidates.StableExternalKey(candidate);
        if (!seenStableKeys.Add(stableKey))
            return new DuplicateClassification(stableKey, "in_file");
        if (portfolioId.HasValue && await TradeExistsAsync(portfolioId.Value, stableKey, ct))
            return new DuplicateClassification(stableKey,
                string.IsNullOrWhiteSpace(candidate.ExternalKey) ? "existing" : "external_key");
        return new DuplicateClassification(stableKey, null);
    }

    private async Task<bool> TradeExistsAsync(Guid portfolioId, string externalKey, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        await using var command = RawSql.Command(connection,
            "SELECT EXISTS(SELECT 1 FROM \"InvestmentTrades\" WHERE \"PortfolioId\"=@portfolio AND \"ExternalKey\"=@key)",
            ("@portfolio", portfolioId), ("@key", externalKey));
        return Convert.ToBoolean(await command.ExecuteScalarAsync(ct));
    }

    private async Task MarkCandidateAsync(Guid id, string state, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        await using var command = RawSql.Command(connection,
            "UPDATE \"InvestmentImportCandidates\" SET \"DuplicateStatus\"=@state WHERE \"Id\"=@id",
            ("@state", state), ("@id", id));
        await command.ExecuteNonQueryAsync(ct);
    }
}
