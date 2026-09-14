using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Audit;
using FullWorth.Backend.Security;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Transactions;

public sealed record TransactionBulkFilter(
    IReadOnlyList<Guid>? AccountIds = null,
    IReadOnlyList<Guid>? CategoryIds = null,
    DateOnly? From = null,
    DateOnly? To = null,
    string? Direction = null,
    string? Query = null,
    bool? IsIgnored = null,
    bool? IsTransfer = null,
    bool? IsPending = null);
public sealed record TransactionBulkMutation(
    TransactionBulkFilter Filter,
    bool UpdateCategory = false,
    Guid? CategoryId = null,
    bool? IsIgnored = null,
    bool? IsReviewed = null,
    Guid? ContractId = null,
    bool ClearContract = false,
    string? ReplaceNote = null,
    bool ConfirmReplaceNotes = false);



/// <summary>Massenaenderungen an Buchungen - Vorschau und Ausfuehrung. Kam aus Parity.</summary>
public static class TransactionBulkEndpoints
{
    public static IEndpointRouteBuilder MapTransactionBulkEndpoints(this IEndpointRouteBuilder app)
    {
        var bulk = app.MapGroup("/api/transaction-bulk").WithTags("Transactions");
        bulk.MapPost("/preview", PreviewBulk);
        bulk.MapPost("/execute", ExecuteBulk);
        return app;
    }

    private static async Task<IResult> PreviewBulk(
        Guid fullWorthSpaceId, TransactionBulkFilter request, CurrentUserContext currentUser,
        FullWorthDbContext db, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await SpaceCapabilities.HasCapabilityAsync(db, userId, fullWorthSpaceId, "transactions.write", ct))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        var query = await BuildBulkQueryAsync(db, userId, fullWorthSpaceId, request, ct);
        if (query is null) return Results.NotFound();
        var count = await query.CountAsync(ct);
        var samples = await query.OrderByDescending(tx => tx.BookingDate ?? tx.ValueDate).Take(10)
            .Select(tx => new
            {
                tx.Id,
                date = tx.BookingDate ?? tx.ValueDate,
                tx.Counterparty,
                tx.Amount,
                tx.Currency,
                tx.CategoryId
            })
            .ToListAsync(ct);
        return Results.Ok(new { count, samples, capped = count > 10000 });
    }

    private static async Task<IResult> ExecuteBulk(
        Guid fullWorthSpaceId, TransactionBulkMutation request, CurrentUserContext currentUser,
        FullWorthDbContext db, AuditService audit, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await SpaceCapabilities.HasCapabilityAsync(db, userId, fullWorthSpaceId, "transactions.write", ct))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (request.ReplaceNote is not null && !request.ConfirmReplaceNotes)
            return Results.BadRequest(new { error = "Replacing notes requires explicit confirmation." });
        if (request.UpdateCategory && request.CategoryId.HasValue &&
            !await db.Categories.AsNoTracking().AnyAsync(category =>
                category.Id == request.CategoryId && category.FullWorthSpaceId == fullWorthSpaceId, ct))
            return Results.BadRequest(new { error = "Category is invalid." });
        if (request.ContractId.HasValue &&
            !await db.Contracts.AsNoTracking().AnyAsync(contract =>
                contract.Id == request.ContractId &&
                contract.FullWorthSpaceId == fullWorthSpaceId &&
                contract.MergedIntoContractId == null, ct))
            return Results.BadRequest(new { error = "Contract is invalid." });

        var query = await BuildBulkQueryAsync(db, userId, fullWorthSpaceId, request.Filter, ct);
        if (query is null) return Results.NotFound();
        var ids = await query.Select(tx => tx.Id).Take(10001).ToListAsync(ct);
        if (ids.Count > 10000)
            return Results.BadRequest(new { error = "More than 10000 transactions match. Narrow the filters." });
        if (ids.Count == 0) return Results.Ok(new { updated = 0 });

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var rows = await db.Transactions.Where(tx => ids.Contains(tx.Id)).ToListAsync(ct);
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
        if (request.IsReviewed.HasValue)
        {
            foreach (var id in ids)
            {
                await using var command = RawSql.Command(connection, """
INSERT INTO "TransactionReviewStates" ("TransactionId","FullWorthSpaceId","IsReviewed","UpdatedAt")
VALUES (@id,@space,@reviewed,@now)
ON CONFLICT ("TransactionId") DO UPDATE SET "IsReviewed"=EXCLUDED."IsReviewed","UpdatedAt"=EXCLUDED."UpdatedAt"
""", ("@id", id), ("@space", fullWorthSpaceId), ("@reviewed", request.IsReviewed.Value),
                    ("@now", DateTimeOffset.UtcNow));
                await command.ExecuteNonQueryAsync(ct);
            }
        }

        if (request.ClearContract)
        {
            foreach (var id in ids)
            {
                await using var command = RawSql.Command(connection,
                    "DELETE FROM \"ContractTransactionLinks\" WHERE \"FullWorthSpaceId\"=@space AND \"TransactionId\"=@id",
                    ("@space", fullWorthSpaceId), ("@id", id));
                await command.ExecuteNonQueryAsync(ct);
            }
        }
        else if (request.ContractId.HasValue)
        {
            foreach (var row in rows.Where(row => row.Amount < 0))
            {
                await using var command = RawSql.Command(connection, """
INSERT INTO "ContractTransactionLinks" ("Id","FullWorthSpaceId","ContractId","TransactionId","Amount","LinkSource","Confidence","CreatedAt")
VALUES (@id,@space,@contract,@transaction,@amount,'manual',1,@now)
ON CONFLICT ("ContractId","TransactionId") DO UPDATE SET "Amount"=EXCLUDED."Amount","LinkSource"='manual',"Confidence"=1
""", ("@id", Guid.NewGuid()), ("@space", fullWorthSpaceId), ("@contract", request.ContractId.Value),
                    ("@transaction", row.Id), ("@amount", Math.Abs(row.Amount)), ("@now", DateTimeOffset.UtcNow));
                await command.ExecuteNonQueryAsync(ct);
            }
        }

        audit.Record(fullWorthSpaceId, userId, "transactions.bulk.updated", "FullWorthSpace", fullWorthSpaceId);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.Ok(new { updated = ids.Count });
    }

    private static async Task<IQueryable<FullWorth.Backend.Modules.Transactions.FinanceTransaction>?> BuildBulkQueryAsync(
        FullWorthDbContext db, Guid userId, Guid fullWorthSpaceId, TransactionBulkFilter filter, CancellationToken ct)
    {
        if (!await RawSql.IsMemberAsync(db, userId, fullWorthSpaceId, ct)) return null;
        var writable = await RawSql.WritableAccountIdsAsync(db, userId, fullWorthSpaceId, ct);
        var query = db.Transactions.AsNoTracking().Where(tx => writable.Contains(tx.AccountId));
        if (filter.AccountIds is { Count: > 0 })
        {
            if (filter.AccountIds.Any(id => !writable.Contains(id))) return query.Where(_ => false);
            query = query.Where(tx => filter.AccountIds.Contains(tx.AccountId));
        }
        if (filter.CategoryIds is { Count: > 0 })
            query = query.Where(tx => tx.CategoryId.HasValue && filter.CategoryIds.Contains(tx.CategoryId.Value));
        if (filter.From.HasValue) query = query.Where(tx => (tx.BookingDate ?? tx.ValueDate) >= filter.From.Value);
        if (filter.To.HasValue) query = query.Where(tx => (tx.BookingDate ?? tx.ValueDate) <= filter.To.Value);
        if (filter.IsIgnored.HasValue) query = query.Where(tx => tx.IsIgnored == filter.IsIgnored.Value);
        if (filter.IsTransfer.HasValue) query = query.Where(tx => tx.IsTransfer == filter.IsTransfer.Value);
        if (filter.IsPending.HasValue)
            query = filter.IsPending.Value ? query.Where(tx => tx.Status == "PDNG") : query.Where(tx => tx.Status != "PDNG");
        if (!string.IsNullOrWhiteSpace(filter.Direction))
            query = filter.Direction.Trim().ToLowerInvariant() == "income"
                ? query.Where(tx => tx.Amount > 0)
                : query.Where(tx => tx.Amount < 0);
        if (!string.IsNullOrWhiteSpace(filter.Query))
        {
            var term = filter.Query.Trim().ToLower();
            query = query.Where(tx => (tx.Counterparty != null && tx.Counterparty.ToLower().Contains(term)) ||
                                      (tx.Description != null && tx.Description.ToLower().Contains(term)) ||
                                      (tx.NormalizedCounterparty != null && tx.NormalizedCounterparty.ToLower().Contains(term)));
        }
        return query;
    }

}
