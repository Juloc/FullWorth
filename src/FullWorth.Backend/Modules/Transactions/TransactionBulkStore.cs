using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Audit;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Transactions;

/// <summary>Eine Buchung, wie sie in der Vorschau einer Massenaenderung erscheint.</summary>
public sealed record TransactionBulkSample(
    Guid Id, DateOnly? Date, string? Counterparty, decimal Amount, string Currency, Guid? CategoryId);

/// <summary>
/// Massenaenderungen an Buchungen: welche der Filter trifft, und was mit ihnen geschieht.
///
/// Der Filter wird an einer Stelle gebaut und von Vorschau und Ausfuehrung gleich benutzt - sonst
/// zeigt die Vorschau etwas anderes an, als die Ausfuehrung anfasst, und das faellt erst auf, wenn
/// jemand 3000 Buchungen falsch umkategorisiert hat.
/// </summary>
public sealed class TransactionBulkStore(FullWorthDbContext db, AuditService audit)
{
    /// <summary>Mehr auf einmal darf niemand aendern; darueber muss der Filter enger werden.</summary>
    public const int MaxMatches = 10_000;

    public Task<bool> CategoryExistsAsync(Guid fullWorthSpaceId, Guid categoryId, CancellationToken ct) =>
        db.Categories.AsNoTracking()
            .AnyAsync(category => category.Id == categoryId && category.FullWorthSpaceId == fullWorthSpaceId, ct);

    /// <summary>Ein zusammengefuehrter Vertrag zaehlt nicht - er ist nur noch ein Verweis.</summary>
    public Task<bool> ActiveContractExistsAsync(Guid fullWorthSpaceId, Guid contractId, CancellationToken ct) =>
        db.Contracts.AsNoTracking()
            .AnyAsync(contract => contract.Id == contractId
                               && contract.FullWorthSpaceId == fullWorthSpaceId
                               && contract.MergedIntoContractId == null, ct);

    public async Task<(int Count, IReadOnlyList<TransactionBulkSample> Samples)> PreviewAsync(
        IReadOnlySet<Guid> writableAccountIds, TransactionBulkFilter filter, CancellationToken ct)
    {
        var query = Filtered(writableAccountIds, filter);
        var count = await query.CountAsync(ct);
        var samples = await query
            .OrderByDescending(transaction => transaction.BookingDate ?? transaction.ValueDate)
            .Take(10)
            .Select(transaction => new TransactionBulkSample(
                transaction.Id,
                transaction.BookingDate ?? transaction.ValueDate,
                transaction.Counterparty,
                transaction.Amount,
                transaction.Currency,
                transaction.CategoryId))
            .ToListAsync(ct);
        return (count, samples);
    }

    /// <summary>Einer mehr als erlaubt, damit der Aufrufer "zu viele" von "genau die Grenze" unterscheiden kann.</summary>
    public Task<List<Guid>> MatchingIdsAsync(
        IReadOnlySet<Guid> writableAccountIds, TransactionBulkFilter filter, CancellationToken ct) =>
        Filtered(writableAccountIds, filter)
            .Select(transaction => transaction.Id)
            .Take(MaxMatches + 1)
            .ToListAsync(ct);

    public async Task<int> ApplyAsync(
        Guid userId, Guid fullWorthSpaceId, IReadOnlyList<Guid> ids, TransactionBulkMutation request,
        CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var rows = await db.Transactions.Where(row => ids.Contains(row.Id)).ToListAsync(ct);
        foreach (var row in rows)
        {
            if (request.UpdateCategory)
            {
                row.CategoryId = request.CategoryId;
                row.CategorizationSource = "manual";
            }
            if (request.IsIgnored.HasValue) row.IsIgnored = request.IsIgnored.Value;
            if (request.ReplaceNote is not null) row.UserNote = request.ReplaceNote.Trim();
            row.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync(ct);

        var connection = await RawSql.OpenAsync(db, ct);
        var now = DateTimeOffset.UtcNow;
        var idArray = ids.ToArray();

        // Je eine Anweisung statt einer Schleife: bei zehntausend Treffern waren das zehntausend
        // Runden zur Datenbank.
        if (request.IsReviewed.HasValue)
        {
            await using var command = RawSql.Command(connection, """
INSERT INTO "TransactionReviewStates" ("TransactionId","FullWorthSpaceId","IsReviewed","UpdatedAt")
SELECT id,@space,@reviewed,@now FROM unnest(@ids) AS id
ON CONFLICT ("TransactionId") DO UPDATE SET "IsReviewed"=EXCLUDED."IsReviewed","UpdatedAt"=EXCLUDED."UpdatedAt"
""", ("@ids", idArray), ("@space", fullWorthSpaceId), ("@reviewed", request.IsReviewed.Value), ("@now", now));
            await command.ExecuteNonQueryAsync(ct);
        }

        if (request.ClearContract)
        {
            await using var command = RawSql.Command(connection,
                "DELETE FROM \"ContractTransactionLinks\" WHERE \"FullWorthSpaceId\"=@space AND \"TransactionId\"=ANY(@ids)",
                ("@space", fullWorthSpaceId), ("@ids", idArray));
            await command.ExecuteNonQueryAsync(ct);
        }
        else if (request.ContractId.HasValue)
        {
            // Nur Ausgaben: eine Gutschrift gehoert nicht an einen Vertrag.
            var expenses = rows.Where(row => row.Amount < 0).ToArray();
            if (expenses.Length > 0)
            {
                await using var command = RawSql.Command(connection, """
INSERT INTO "ContractTransactionLinks" ("Id","FullWorthSpaceId","ContractId","TransactionId","Amount","LinkSource","Confidence","CreatedAt")
SELECT gen_random_uuid(),@space,@contract,t.id,t.amount,'manual',1,@now
FROM unnest(@ids, @amounts) AS t(id, amount)
ON CONFLICT ("ContractId","TransactionId") DO UPDATE SET "Amount"=EXCLUDED."Amount","LinkSource"='manual',"Confidence"=1
""", ("@space", fullWorthSpaceId), ("@contract", request.ContractId.Value),
                    ("@ids", expenses.Select(row => row.Id).ToArray()),
                    ("@amounts", expenses.Select(row => Math.Abs(row.Amount)).ToArray()),
                    ("@now", now));
                await command.ExecuteNonQueryAsync(ct);
            }
        }

        audit.Record(fullWorthSpaceId, userId, "transactions.bulk.updated", "FullWorthSpace", fullWorthSpaceId);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return ids.Count;
    }

    private IQueryable<FinanceTransaction> Filtered(
        IReadOnlySet<Guid> writableAccountIds, TransactionBulkFilter filter)
    {
        var query = db.Transactions.AsNoTracking()
            .Where(transaction => writableAccountIds.Contains(transaction.AccountId));

        if (filter.AccountIds is { Count: > 0 })
        {
            // Ein Konto, das er nicht aendern darf, macht die ganze Auswahl leer - nicht nur dieses.
            if (filter.AccountIds.Any(id => !writableAccountIds.Contains(id))) return query.Where(_ => false);
            query = query.Where(transaction => filter.AccountIds.Contains(transaction.AccountId));
        }
        if (filter.CategoryIds is { Count: > 0 })
            query = query.Where(transaction =>
                transaction.CategoryId.HasValue && filter.CategoryIds.Contains(transaction.CategoryId.Value));
        if (filter.From.HasValue)
            query = query.Where(transaction => (transaction.BookingDate ?? transaction.ValueDate) >= filter.From.Value);
        if (filter.To.HasValue)
            query = query.Where(transaction => (transaction.BookingDate ?? transaction.ValueDate) <= filter.To.Value);
        if (filter.IsIgnored.HasValue)
            query = query.Where(transaction => transaction.IsIgnored == filter.IsIgnored.Value);
        if (filter.IsTransfer.HasValue)
            query = query.Where(transaction => transaction.IsTransfer == filter.IsTransfer.Value);
        if (filter.IsPending.HasValue)
            query = filter.IsPending.Value
                ? query.Where(transaction => transaction.Status == "PDNG")
                : query.Where(transaction => transaction.Status != "PDNG");
        if (!string.IsNullOrWhiteSpace(filter.Direction))
            query = filter.Direction.Trim().ToLowerInvariant() == "income"
                ? query.Where(transaction => transaction.Amount > 0)
                : query.Where(transaction => transaction.Amount < 0);
        if (!string.IsNullOrWhiteSpace(filter.Query))
        {
            var term = filter.Query.Trim().ToLower();
            query = query.Where(transaction =>
                (transaction.Counterparty != null && transaction.Counterparty.ToLower().Contains(term)) ||
                (transaction.Description != null && transaction.Description.ToLower().Contains(term)) ||
                (transaction.NormalizedCounterparty != null && transaction.NormalizedCounterparty.ToLower().Contains(term)));
        }
        return query;
    }
}
