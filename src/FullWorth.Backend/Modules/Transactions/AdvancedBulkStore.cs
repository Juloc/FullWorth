using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Contracts;
using FullWorth.Backend.Modules.Audit;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Transactions;

/// <summary>
/// Die Datenseite der erweiterten Massenaenderung: welche Buchungen der Filter trifft, was an ihnen
/// haengt, und wie eine Aenderung geschrieben wird.
///
/// Geschrieben wird in einer Transaktion ueber alle Teile - Felder, Pruefstatus, Etiketten,
/// Vertragsverknuepfung. Der Aufrufer bekommt entweder alles oder nichts: eine halb angewandte
/// Massenaenderung ist schlimmer als eine abgelehnte, weil niemand sieht, wo sie aufgehoert hat.
/// </summary>
public sealed class AdvancedBulkStore(FullWorthDbContext db, AuditService audit)
{
    public Task<List<FinanceTransaction>> ByIdsAsync(
        IReadOnlySet<Guid> writableAccountIds, Guid[] ids, CancellationToken ct) =>
        Writable(writableAccountIds).Where(transaction => ids.Contains(transaction.Id)).ToListAsync(ct);

    /// <summary>
    /// Die gefilterten Buchungen, neueste zuerst - einer mehr als erlaubt, damit der Aufrufer
    /// "zu viele" erkennt, ohne noch einmal zu zaehlen.
    /// </summary>
    public Task<List<FinanceTransaction>> ByFilterAsync(
        IReadOnlySet<Guid> writableAccountIds, AdvancedTransactionBulkFilter filter, int limit,
        CancellationToken ct)
    {
        var query = Writable(writableAccountIds);

        if (filter.AccountId.HasValue) query = query.Where(transaction => transaction.AccountId == filter.AccountId.Value);
        if (filter.CategoryId.HasValue) query = query.Where(transaction => transaction.CategoryId == filter.CategoryId.Value);
        if (filter.From.HasValue) query = query.Where(transaction => (transaction.BookingDate ?? transaction.ValueDate) >= filter.From.Value);
        if (filter.To.HasValue) query = query.Where(transaction => (transaction.BookingDate ?? transaction.ValueDate) <= filter.To.Value);
        if (string.Equals(filter.Direction, "income", StringComparison.OrdinalIgnoreCase)) query = query.Where(transaction => transaction.Amount > 0);
        if (string.Equals(filter.Direction, "expense", StringComparison.OrdinalIgnoreCase)) query = query.Where(transaction => transaction.Amount < 0);
        if (!filter.IncludeIgnored) query = query.Where(transaction => !transaction.IsIgnored);
        if (filter.IsIgnored.HasValue) query = query.Where(transaction => transaction.IsIgnored == filter.IsIgnored.Value);
        if (filter.TransfersOnly) query = query.Where(transaction => transaction.IsTransfer);
        if (!string.IsNullOrWhiteSpace(filter.Status)) query = query.Where(transaction => transaction.Status == filter.Status);
        if (!string.IsNullOrWhiteSpace(filter.Query))
        {
            var pattern = $"%{filter.Query.Trim()}%";
            query = query.Where(transaction =>
                (transaction.Counterparty != null && EF.Functions.ILike(transaction.Counterparty, pattern)) ||
                (transaction.NormalizedCounterparty != null && EF.Functions.ILike(transaction.NormalizedCounterparty, pattern)) ||
                (transaction.Description != null && EF.Functions.ILike(transaction.Description, pattern)) ||
                (transaction.UserNote != null && EF.Functions.ILike(transaction.UserNote, pattern)));
        }

        return query
            .OrderByDescending(transaction => transaction.BookingDate ?? transaction.ValueDate)
            .ThenByDescending(transaction => transaction.UpdatedAt)
            .Take(limit + 1)
            .ToListAsync(ct);
    }

    /// <summary>Alle genannten Etiketten muessen zu diesem Space gehoeren - sonst gilt keines.</summary>
    public async Task<bool> TagsValidAsync(Guid fullWorthSpaceId, Guid[] tagIds, CancellationToken ct)
    {
        if (tagIds.Length == 0) return true;

        var connection = await RawSql.OpenAsync(db, ct);
        await using var command = RawSql.Command(connection,
            "SELECT count(*) FROM \"FinanceTags\" WHERE \"FullWorthSpaceId\"=@space AND \"Id\"=ANY(@ids)",
            ("@space", fullWorthSpaceId), ("@ids", tagIds));
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct)) == tagIds.Length;
    }

    public async Task<HashSet<Guid>> TransactionIdsWithTagAsync(
        Guid fullWorthSpaceId, Guid tagId, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        await using var command = RawSql.Command(connection, """
SELECT tt."TransactionId" FROM "TransactionTags" tt JOIN "FinanceTags" ft ON ft."Id"=tt."TagId"
WHERE ft."FullWorthSpaceId"=@space AND ft."Id"=@tag
""", ("@space", fullWorthSpaceId), ("@tag", tagId));
        await using var reader = await command.ExecuteReaderAsync(ct);

        var ids = new HashSet<Guid>();
        while (await reader.ReadAsync(ct)) ids.Add(reader.GetGuid(0));
        return ids;
    }

    /// <summary>Nur die ausdruecklich gesetzten Stände; alles andere leitet der Aufrufer ab.</summary>
    public async Task<Dictionary<Guid, bool>> ReviewStatesAsync(Guid fullWorthSpaceId, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        await using var command = RawSql.Command(connection,
            "SELECT \"TransactionId\",\"IsReviewed\" FROM \"TransactionReviewStates\" WHERE \"FullWorthSpaceId\"=@space",
            ("@space", fullWorthSpaceId));
        await using var reader = await command.ExecuteReaderAsync(ct);

        var states = new Dictionary<Guid, bool>();
        while (await reader.ReadAsync(ct)) states[reader.GetGuid(0)] = reader.GetBoolean(1);
        return states;
    }

    public async Task<bool> AnyContractLinkExistsAsync(Guid[] ids, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        await using var command = RawSql.Command(connection,
            "SELECT EXISTS(SELECT 1 FROM \"ContractTransactionLinks\" WHERE \"TransactionId\"=ANY(@ids))",
            ("@ids", ids));
        return Convert.ToBoolean(await command.ExecuteScalarAsync(ct));
    }

    public Task<RecurringContract?> FindContractAsync(
        Guid fullWorthSpaceId, Guid contractId, CancellationToken ct) =>
        db.Contracts.AsNoTracking().SingleOrDefaultAsync(contract =>
            contract.Id == contractId && contract.FullWorthSpaceId == fullWorthSpaceId, ct);

    public Task<bool> CategoryExistsAsync(Guid fullWorthSpaceId, Guid categoryId, CancellationToken ct) =>
        db.Categories.AsNoTracking()
            .AnyAsync(category => category.Id == categoryId && category.FullWorthSpaceId == fullWorthSpaceId, ct);

    /// <summary>
    /// Wendet die Aenderung an. <c>false</c> heisst: nichts wurde geschrieben, die Transaktion wurde
    /// zurueckgerollt.
    /// </summary>
    public async Task<bool> ApplyAsync(
        Guid userId, Guid fullWorthSpaceId, IReadOnlyList<FinanceTransaction> items,
        AdvancedTransactionBulkRequest request, Guid[] addTags, Guid[] removeTags, string? contractAction,
        CancellationToken ct)
    {
        var ids = items.Select(transaction => transaction.Id).ToArray();
        var now = DateTimeOffset.UtcNow;

        await using var scope = await db.Database.BeginTransactionAsync(ct);
        try
        {
            foreach (var transaction in items)
            {
                if (request.UpdateCategory)
                {
                    transaction.CategoryId = request.CategoryId;
                    transaction.CategorizationSource = "manual";
                }
                if (request.IsIgnored.HasValue) transaction.IsIgnored = request.IsIgnored.Value;
                if (request.ReplaceNotes)
                    transaction.UserNote = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
                transaction.UpdatedAt = now;
            }

            if (request.PairAsTransfer)
            {
                // Beide Seiten bekommen dieselbe Gruppe - daran erkennt der Rest des Systems, dass
                // hier kein Geld den Haushalt verlassen hat.
                var groupId = Guid.NewGuid();
                foreach (var transaction in items)
                {
                    transaction.IsTransfer = true;
                    transaction.TransferGroupId = groupId;
                    transaction.TransferPurpose = null;
                }
            }

            await db.SaveChangesAsync(ct);

            if (request.IsReviewed.HasValue)
                await db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO "TransactionReviewStates" ("TransactionId","FullWorthSpaceId","IsReviewed","UpdatedAt")
SELECT value, {fullWorthSpaceId}, {request.IsReviewed.Value}, {now}
FROM unnest({ids}) AS value
ON CONFLICT ("TransactionId") DO UPDATE
SET "FullWorthSpaceId"=EXCLUDED."FullWorthSpaceId","IsReviewed"=EXCLUDED."IsReviewed","UpdatedAt"=EXCLUDED."UpdatedAt";
""", ct);

            foreach (var tagId in addTags)
                await db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO "TransactionTags" ("TransactionId","TagId","CreatedAt")
SELECT value, {tagId}, {now} FROM unnest({ids}) AS value
ON CONFLICT ("TransactionId","TagId") DO NOTHING;
""", ct);

            if (removeTags.Length > 0)
                await db.Database.ExecuteSqlInterpolatedAsync($"""
DELETE FROM "TransactionTags" WHERE "TransactionId"=ANY({ids}) AND "TagId"=ANY({removeTags});
""", ct);

            if (contractAction == "link")
                await db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO "ContractTransactionLinks"
("Id","FullWorthSpaceId","ContractId","TransactionId","Amount","LinkSource","Confidence","CreatedAt")
SELECT gen_random_uuid(), {fullWorthSpaceId}, {request.ContractId!.Value}, t."Id", abs(t."Amount"), 'manual', NULL, {now}
FROM "Transactions" t WHERE t."Id"=ANY({ids});
""", ct);
            else if (contractAction == "unlink")
                await db.Database.ExecuteSqlInterpolatedAsync($"""
DELETE FROM "ContractTransactionLinks"
WHERE "FullWorthSpaceId"={fullWorthSpaceId} AND "ContractId"={request.ContractId!.Value} AND "TransactionId"=ANY({ids});
""", ct);

            audit.Record(fullWorthSpaceId, userId, "transaction.bulk_updated", "FinanceTransaction");
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await scope.RollbackAsync(CancellationToken.None);
            throw;
        }
        catch
        {
            await scope.RollbackAsync(ct);
            return false;
        }
    }

    private IQueryable<FinanceTransaction> Writable(IReadOnlySet<Guid> writableAccountIds) =>
        db.Transactions.Where(transaction => writableAccountIds.Contains(transaction.AccountId));
}
