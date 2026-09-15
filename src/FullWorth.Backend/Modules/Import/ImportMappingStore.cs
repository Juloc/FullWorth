using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.Audit;
using FullWorth.Backend.Modules.Categories;
using FullWorth.Backend.Modules.Transactions;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Import;

/// <summary>Eine Zeile aus der hochgeladenen Datei, so wie sie zwischengespeichert wird.</summary>
public sealed record MappedCandidate(
    Guid Id, string? SourceAccount, DateOnly? Date, decimal Amount, string Currency, string? Counterparty,
    string? Description, string? Category, string? ExternalKey, string Fingerprint, string Status, string? Error);

/// <summary>Wie oft ein Wert in der Datei vorkommt - die Grundlage der Zuordnungsvorschlaege.</summary>
public sealed record SourceValueCount(string Source, long Count);

/// <summary>
/// Der Zwischenspeicher eines Imports mit eigener Spaltenzuordnung: der Auftrag, seine Zeilen, und
/// was daraus an Buchungen wird.
///
/// Der Import laeuft in zwei Schritten ueber zwei Anfragen - hochladen, dann festschreiben - und
/// zwischendurch liegen die Zeilen in <c>ImportCandidates</c>. Darum haben sie eine eigene Tabelle
/// und nicht nur eine Variable: der Benutzer soll sehen, was ankommen wuerde, bevor es ankommt.
/// </summary>
public sealed class ImportMappingStore(FullWorthDbContext db, AuditService audit)
{
    /// <summary>
    /// Gehoert dieser Auftrag ihm, und ist er noch offen? Ein abgeschlossener Auftrag darf nicht ein
    /// zweites Mal festgeschrieben werden.
    /// </summary>
    public async Task<bool> OwnsOpenJobAsync(Guid jobId, Guid fullWorthSpaceId, Guid userId, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        await using var cmd = RawSql.Command(connection,
            "SELECT EXISTS(SELECT 1 FROM \"ImportJobs\" WHERE \"Id\"=@id AND \"FullWorthSpaceId\"=@space AND \"UserId\"=@user AND \"Status\" NOT IN ('completed','cancelled'))",
            ("@id", jobId), ("@space", fullWorthSpaceId), ("@user", userId));
        return Convert.ToBoolean(await cmd.ExecuteScalarAsync(ct));
    }

    /// <summary>
    /// Eine Datei ohne Waehrungsspalte nennt keine Waehrung; die Basiswaehrung des Space ist die
    /// ehrliche Lesart davon, nicht ein fest eingebautes EUR.
    /// </summary>
    public async Task<string> BaseCurrencyAsync(Guid fullWorthSpaceId, CancellationToken ct)
    {
        var currency = await db.FullWorthSpaces.AsNoTracking()
            .Where(space => space.Id == fullWorthSpaceId)
            .Select(space => space.BaseCurrency)
            .SingleOrDefaultAsync(ct);
        return string.IsNullOrWhiteSpace(currency) ? "EUR" : currency;
    }

    public async Task CreateJobAsync(
        Guid userId, Guid fullWorthSpaceId, Guid jobId, string fileName, string sha, string adapterKey,
        IReadOnlyList<MappedCandidate> candidates, int errorCount, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var connection = await RawSql.OpenAsync(db, ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        await using (var command = RawSql.Command(connection, """
INSERT INTO "ImportJobs" ("Id","FullWorthSpaceId","UserId","FileName","FileSha256","AdapterKey","Status","SourceRowCount","ReadyCount","DuplicateCount","ImportedCount","ErrorCount","CreatedAt","UpdatedAt")
VALUES (@id,@space,@user,@file,@sha,@adapter,@status,@source,@ready,0,0,@errors,@now,@now)
""", ("@id", jobId), ("@space", fullWorthSpaceId), ("@user", userId), ("@file", fileName),
            ("@sha", sha), ("@adapter", adapterKey),
            // Nur wenn KEINE Zeile lesbar war ist der Auftrag gescheitert; sonst wartet er auf die Zuordnung.
            ("@status", errorCount == candidates.Count ? "failed" : "mapping_required"),
            ("@source", candidates.Count), ("@ready", candidates.Count - errorCount), ("@errors", errorCount),
            ("@now", now)))
            await command.ExecuteNonQueryAsync(ct);

        foreach (var candidate in candidates)
        {
            await using var command = RawSql.Command(connection, """
INSERT INTO "ImportCandidates" ("Id","ImportJobId","SourceAccount","BookingDate","Amount","Currency","Counterparty","Description","CategoryText","ExternalKey","RowFingerprint","DuplicateStatus","ValidationStatus","ValidationError")
VALUES (@id,@job,@account,@date,@amount,@currency,@party,@description,@category,@external,@fingerprint,'new',@status,@error)
""", ("@id", candidate.Id), ("@job", jobId), ("@account", candidate.SourceAccount), ("@date", candidate.Date),
                ("@amount", candidate.Amount), ("@currency", candidate.Currency), ("@party", candidate.Counterparty),
                ("@description", candidate.Description), ("@category", candidate.Category),
                ("@external", candidate.ExternalKey), ("@fingerprint", candidate.Fingerprint),
                ("@status", candidate.Status), ("@error", candidate.Error));
            await command.ExecuteNonQueryAsync(ct);
        }

        audit.Record(fullWorthSpaceId, userId, "import.mapped.uploaded", "ImportJob", jobId);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    public async Task<List<MappedCandidate>> ReadCandidatesAsync(Guid jobId, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        await using var cmd = RawSql.Command(connection,
            "SELECT \"Id\",\"SourceAccount\",\"BookingDate\",\"Amount\",\"Currency\",\"Counterparty\",\"Description\",\"CategoryText\",\"ExternalKey\",\"RowFingerprint\",\"ValidationStatus\",\"ValidationError\" FROM \"ImportCandidates\" WHERE \"ImportJobId\"=@job ORDER BY \"BookingDate\",\"Id\"",
            ("@job", jobId));
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var rows = new List<MappedCandidate>();
        while (await reader.ReadAsync(ct))
            rows.Add(new MappedCandidate(
                RawSql.Guid(reader, "Id"), RawSql.NullableString(reader, "SourceAccount"),
                RawSql.NullableDate(reader, "BookingDate"), RawSql.Decimal(reader, "Amount"),
                RawSql.String(reader, "Currency"), RawSql.NullableString(reader, "Counterparty"),
                RawSql.NullableString(reader, "Description"), RawSql.NullableString(reader, "CategoryText"),
                RawSql.NullableString(reader, "ExternalKey"), RawSql.String(reader, "RowFingerprint"),
                RawSql.String(reader, "ValidationStatus"), RawSql.NullableString(reader, "ValidationError")));
        return rows;
    }

    /// <summary>Welche Quellwerte in der Datei stehen und wie oft - nur aus lesbaren Zeilen.</summary>
    public Task<List<SourceValueCount>> SourceAccountCountsAsync(Guid jobId, CancellationToken ct) =>
        CountsAsync(jobId, "SourceAccount", ct);

    public Task<List<SourceValueCount>> SourceCategoryCountsAsync(Guid jobId, CancellationToken ct) =>
        CountsAsync(jobId, "CategoryText", ct);

    private async Task<List<SourceValueCount>> CountsAsync(Guid jobId, string column, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        await using var cmd = RawSql.Command(connection, $"""
SELECT COALESCE("{column}",''),count(*) FROM "ImportCandidates"
WHERE "ImportJobId"=@job AND "ValidationStatus"='ready' GROUP BY COALESCE("{column}",'') ORDER BY count(*) DESC
""", ("@job", jobId));
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var rows = new List<SourceValueCount>();
        while (await reader.ReadAsync(ct)) rows.Add(new SourceValueCount(reader.GetString(0), reader.GetInt64(1)));
        return rows;
    }

    /// <summary>
    /// Die Buchungen, gegen die auf Dubletten geprueft wird - eine Abfrage je Konto statt zwei je
    /// Zeile. Bei einer Datei mit 5000 Zeilen waren das 10 000 Runden zur Datenbank.
    /// </summary>
    public async Task<(HashSet<(Guid, string)> ExternalKeys, HashSet<(Guid, DateOnly, decimal, string, string?)> Semantic)>
        ExistingKeysAsync(IReadOnlyCollection<Guid> accountIds, CancellationToken ct)
    {
        var externalKeys = new HashSet<(Guid, string)>();
        var semantic = new HashSet<(Guid, DateOnly, decimal, string, string?)>();
        if (accountIds.Count == 0) return (externalKeys, semantic);

        var rows = await db.Transactions.AsNoTracking()
            .Where(transaction => accountIds.Contains(transaction.AccountId))
            .Select(transaction => new
            {
                transaction.AccountId,
                transaction.ExternalKey,
                Date = transaction.BookingDate ?? transaction.ValueDate,
                transaction.Amount,
                transaction.Currency,
                transaction.NormalizedCounterparty
            })
            .ToListAsync(ct);

        foreach (var row in rows)
        {
            if (!string.IsNullOrEmpty(row.ExternalKey)) externalKeys.Add((row.AccountId, row.ExternalKey));
            if (row.Date.HasValue)
                semantic.Add((row.AccountId, row.Date.Value, row.Amount, row.Currency, row.NormalizedCounterparty));
        }
        return (externalKeys, semantic);
    }

    public Task<int> CountCategoriesAsync(Guid fullWorthSpaceId, Guid[] categoryIds, CancellationToken ct) =>
        db.Categories.AsNoTracking()
            .CountAsync(category => category.FullWorthSpaceId == fullWorthSpaceId && categoryIds.Contains(category.Id), ct);

    public Task<List<FinanceCategory>> ListCategoriesAsync(Guid fullWorthSpaceId, CancellationToken ct) =>
        db.Categories.Where(category => category.FullWorthSpaceId == fullWorthSpaceId).ToListAsync(ct);

    public Task<List<CategorizationRule>> ActiveTransactionRulesAsync(Guid fullWorthSpaceId, CancellationToken ct) =>
        db.CategorizationRules.AsNoTracking()
            .Where(rule => rule.FullWorthSpaceId == fullWorthSpaceId && rule.IsEnabled && rule.Target == "transaction")
            .OrderBy(rule => rule.Priority)
            .ToListAsync(ct);

    public Task<FinanceAccount> AccountAsync(Guid accountId, CancellationToken ct) =>
        db.Accounts.AsNoTracking().SingleAsync(account => account.Id == accountId, ct);

    public Task<bool> IsOwnerAsync(Guid userId, Guid fullWorthSpaceId, CancellationToken ct) =>
        RawSql.IsOwnerAsync(db, userId, fullWorthSpaceId, ct);
}
