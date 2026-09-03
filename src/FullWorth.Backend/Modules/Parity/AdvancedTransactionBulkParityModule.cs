using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Audit;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Security;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Parity;

public sealed record AdvancedTransactionBulkFilter(
    string? Query = null,
    Guid? AccountId = null,
    Guid? CategoryId = null,
    DateOnly? From = null,
    DateOnly? To = null,
    string? Direction = null,
    string? Status = null,
    bool IncludeIgnored = false,
    bool? IsIgnored = null,
    bool TransfersOnly = false,
    string? ReviewState = null,
    Guid? TagId = null);

public sealed record AdvancedTransactionBulkRequest(
    AdvancedTransactionBulkFilter? Filter,
    IReadOnlyList<Guid>? TransactionIds,
    int ExpectedCount,
    bool ConfirmSelection,
    bool UpdateCategory = false,
    Guid? CategoryId = null,
    bool? IsIgnored = null,
    bool? IsReviewed = null,
    IReadOnlyList<Guid>? AddTagIds = null,
    IReadOnlyList<Guid>? RemoveTagIds = null,
    string? ContractAction = null,
    Guid? ContractId = null,
    bool ReplaceNotes = false,
    string? Note = null,
    bool ConfirmReplaceNotes = false,
    bool PairAsTransfer = false);

public static class AdvancedTransactionBulkParityEndpoints
{
    private const int MaxExplicitIds = 1000;
    private const int MaxFilterMatches = 5000;

    public static IEndpointRouteBuilder MapAdvancedTransactionBulkParityEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/transaction-bulk").WithTags("Transactions");
        group.MapPost("/advanced-preview", Preview);
        group.MapPost("/apply", Apply);
        return app;
    }

    private static async Task<IResult> Preview(
        AdvancedTransactionBulkRequest request,
        Guid fullWorthSpaceId,
        CurrentUserContext currentUser,
        FullWorthDbContext db,
        CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        var selection = await ResolveSelection(db, userId, fullWorthSpaceId, request.Filter, request.TransactionIds, ct);
        if (selection.Forbidden) return Results.NotFound();
        if (selection.TooLarge)
            return Results.BadRequest(new { error = $"Bulk filter matches more than {MaxFilterMatches} transactions. Narrow the filter first." });

        return Results.Ok(new
        {
            count = selection.Items.Count,
            canPairTransfer = CanPairAsTransfer(selection.Items),
            sample = selection.Items.Take(12).Select(transaction => new
            {
                transaction.Id,
                date = transaction.BookingDate ?? transaction.ValueDate,
                transaction.Counterparty,
                transaction.Description,
                transaction.Amount,
                transaction.Currency,
                transaction.CategoryId,
                transaction.IsIgnored,
                transaction.IsTransfer
            })
        });
    }

    private static async Task<IResult> Apply(
        AdvancedTransactionBulkRequest request,
        Guid fullWorthSpaceId,
        CurrentUserContext currentUser,
        FullWorthDbContext db,
        AuditService audit,
        CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!request.ConfirmSelection)
            return Results.BadRequest(new { error = "Explicit bulk confirmation is required." });

        var selection = await ResolveSelection(db, userId, fullWorthSpaceId, request.Filter, request.TransactionIds, ct);
        if (selection.Forbidden) return Results.NotFound();
        if (selection.TooLarge)
            return Results.BadRequest(new { error = $"Bulk filter matches more than {MaxFilterMatches} transactions. Narrow the filter first." });
        if (selection.Items.Count == 0)
            return Results.BadRequest(new { error = "No writable transactions match the selection." });
        if (request.ExpectedCount != selection.Items.Count)
            return Results.Conflict(new { error = "The matching transaction set changed after preview. Review the selection again.", expected = request.ExpectedCount, actual = selection.Items.Count });

        var categorizeAction = request.UpdateCategory || request.IsReviewed.HasValue ||
                               (request.AddTagIds?.Count ?? 0) > 0 || (request.RemoveTagIds?.Count ?? 0) > 0;
        var writeAction = request.IsIgnored.HasValue || request.ReplaceNotes || request.PairAsTransfer ||
                          !string.IsNullOrWhiteSpace(request.ContractAction);
        if (categorizeAction && !await PermissionsErgonomicsParityEndpoints.HasCapabilityAsync(db, userId, fullWorthSpaceId, "transactions.categorize", ct))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (writeAction && !await PermissionsErgonomicsParityEndpoints.HasCapabilityAsync(db, userId, fullWorthSpaceId, "transactions.write", ct))
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        if (request.UpdateCategory && request.CategoryId.HasValue &&
            !await db.Categories.AsNoTracking().AnyAsync(category =>
                category.Id == request.CategoryId.Value && category.FullWorthSpaceId == fullWorthSpaceId && !category.IsArchived, ct))
            return Results.BadRequest(new { error = "Category is invalid for this FullWorth Space." });

        var addTags = (request.AddTagIds ?? []).Distinct().ToArray();
        var removeTags = (request.RemoveTagIds ?? []).Distinct().ToArray();
        if (!await TagsValid(db, fullWorthSpaceId, addTags.Concat(removeTags).Distinct().ToArray(), ct))
            return Results.BadRequest(new { error = "A selected tag is invalid for this FullWorth Space." });

        if (request.ReplaceNotes)
        {
            if (!request.ConfirmReplaceNotes)
                return Results.BadRequest(new { error = "Replacing notes requires the explicit note replacement confirmation." });
            if ((request.Note?.Length ?? 0) > 2000)
                return Results.BadRequest(new { error = "Bulk note is too long." });
        }

        var contractAction = request.ContractAction?.Trim().ToLowerInvariant();
        if (contractAction is not null and not "" and not "link" and not "unlink")
            return Results.BadRequest(new { error = "Contract action must be link or unlink." });
        if (!string.IsNullOrWhiteSpace(contractAction))
        {
            if (!request.ContractId.HasValue)
                return Results.BadRequest(new { error = "A contract is required for the bulk contract action." });
            if (!await PermissionsErgonomicsParityEndpoints.HasCapabilityAsync(db, userId, fullWorthSpaceId, "contracts.manage", ct))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var contract = await db.Contracts.AsNoTracking().SingleOrDefaultAsync(row =>
                row.Id == request.ContractId.Value && row.FullWorthSpaceId == fullWorthSpaceId && row.IsActive, ct);
            if (contract is null) return Results.BadRequest(new { error = "Contract is unavailable." });
            if (contract.AccountId.HasValue)
            {
                var writable = await ParitySql.WritableAccountIdsAsync(db, userId, fullWorthSpaceId, ct);
                if (!writable.Contains(contract.AccountId.Value))
                    return Results.StatusCode(StatusCodes.Status403Forbidden);
            }
            if (contractAction == "link")
            {
                if (selection.Items.Any(transaction => transaction.Amount >= 0))
                    return Results.BadRequest(new { error = "Only expense transactions can be bulk-linked to a contract." });
                if (await AnyContractLinkExists(db, selection.Items.Select(item => item.Id).ToArray(), ct))
                    return Results.Conflict(new { error = "At least one selected transaction already has a contract allocation. Resolve existing allocations first." });
            }
        }

        if (request.PairAsTransfer)
        {
            if (!string.IsNullOrWhiteSpace(contractAction))
                return Results.BadRequest(new { error = "Transfer pairing and contract linking cannot be combined." });
            if (!CanPairAsTransfer(selection.Items))
                return Results.BadRequest(new { error = "The selection is not a safe two-leg transfer pair." });
        }

        var ids = selection.Items.Select(transaction => transaction.Id).ToArray();
        await using var transactionScope = await db.Database.BeginTransactionAsync(ct);
        try
        {
            foreach (var transaction in selection.Items)
            {
                if (request.UpdateCategory)
                {
                    transaction.CategoryId = request.CategoryId;
                    transaction.CategorizationSource = "manual";
                }
                if (request.IsIgnored.HasValue) transaction.IsIgnored = request.IsIgnored.Value;
                if (request.ReplaceNotes) transaction.UserNote = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
                transaction.UpdatedAt = DateTimeOffset.UtcNow;
            }

            if (request.PairAsTransfer)
            {
                var groupId = Guid.NewGuid();
                foreach (var transaction in selection.Items)
                {
                    transaction.IsTransfer = true;
                    transaction.TransferGroupId = groupId;
                    transaction.TransferPurpose = null;
                }
            }

            await db.SaveChangesAsync(ct);

            if (request.IsReviewed.HasValue)
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO "TransactionReviewStates" ("TransactionId","FullWorthSpaceId","IsReviewed","UpdatedAt")
SELECT value, {fullWorthSpaceId}, {request.IsReviewed.Value}, {DateTimeOffset.UtcNow}
FROM unnest({ids}) AS value
ON CONFLICT ("TransactionId") DO UPDATE
SET "FullWorthSpaceId"=EXCLUDED."FullWorthSpaceId","IsReviewed"=EXCLUDED."IsReviewed","UpdatedAt"=EXCLUDED."UpdatedAt";
""", ct);
            }

            foreach (var tagId in addTags)
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO "TransactionTags" ("TransactionId","TagId","CreatedAt")
SELECT value, {tagId}, {DateTimeOffset.UtcNow} FROM unnest({ids}) AS value
ON CONFLICT ("TransactionId","TagId") DO NOTHING;
""", ct);
            }
            if (removeTags.Length > 0)
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"""
DELETE FROM "TransactionTags" WHERE "TransactionId"=ANY({ids}) AND "TagId"=ANY({removeTags});
""", ct);
            }

            if (contractAction == "link")
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO "ContractTransactionLinks"
("Id","FullWorthSpaceId","ContractId","TransactionId","Amount","LinkSource","Confidence","CreatedAt")
SELECT gen_random_uuid(), {fullWorthSpaceId}, {request.ContractId!.Value}, t."Id", abs(t."Amount"), 'manual', NULL, {DateTimeOffset.UtcNow}
FROM "Transactions" t WHERE t."Id"=ANY({ids});
""", ct);
            }
            else if (contractAction == "unlink")
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"""
DELETE FROM "ContractTransactionLinks"
WHERE "FullWorthSpaceId"={fullWorthSpaceId} AND "ContractId"={request.ContractId!.Value} AND "TransactionId"=ANY({ids});
""", ct);
            }

            audit.Record(fullWorthSpaceId, userId, "transaction.bulk_updated", "FinanceTransaction");
            await db.SaveChangesAsync(ct);
            await transactionScope.CommitAsync(ct);
            return Results.Ok(new { changed = selection.Items.Count, pairedTransfer = request.PairAsTransfer });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await transactionScope.RollbackAsync(CancellationToken.None);
            throw;
        }
        catch
        {
            await transactionScope.RollbackAsync(ct);
            return Results.Conflict(new { error = "Bulk update failed atomically. No selected transaction was changed." });
        }
    }

    private static async Task<Selection> ResolveSelection(
        FullWorthDbContext db,
        Guid userId,
        Guid fullWorthSpaceId,
        AdvancedTransactionBulkFilter? filter,
        IReadOnlyList<Guid>? explicitIds,
        CancellationToken ct)
    {
        var writable = await ParitySql.WritableAccountIdsAsync(db, userId, fullWorthSpaceId, ct);
        var query = db.Transactions.Where(transaction => writable.Contains(transaction.AccountId));

        if (explicitIds is { Count: > 0 })
        {
            var ids = explicitIds.Distinct().ToArray();
            if (ids.Length > MaxExplicitIds) return new([], false, true);
            var items = await query.Where(transaction => ids.Contains(transaction.Id)).ToListAsync(ct);
            return items.Count == ids.Length ? new(items, false, false) : new([], true, false);
        }

        filter ??= new AdvancedTransactionBulkFilter();
        if (filter.AccountId.HasValue)
        {
            if (!writable.Contains(filter.AccountId.Value)) return new([], true, false);
            query = query.Where(transaction => transaction.AccountId == filter.AccountId.Value);
        }
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

        var itemsFiltered = await query.OrderByDescending(transaction => transaction.BookingDate ?? transaction.ValueDate)
            .ThenByDescending(transaction => transaction.UpdatedAt).Take(MaxFilterMatches + 1).ToListAsync(ct);
        if (itemsFiltered.Count > MaxFilterMatches) return new([], false, true);

        if (filter.TagId.HasValue)
        {
            var matchingTags = await LoadTagTransactionIds(db, fullWorthSpaceId, filter.TagId.Value, ct);
            itemsFiltered = itemsFiltered.Where(transaction => matchingTags.Contains(transaction.Id)).ToList();
        }
        if (string.Equals(filter.ReviewState, "reviewed", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(filter.ReviewState, "needs_review", StringComparison.OrdinalIgnoreCase))
        {
            var reviews = await LoadReviewStates(db, fullWorthSpaceId, ct);
            var wantReviewed = string.Equals(filter.ReviewState, "reviewed", StringComparison.OrdinalIgnoreCase);
            itemsFiltered = itemsFiltered.Where(transaction => IsReviewed(transaction, reviews) == wantReviewed).ToList();
        }
        return new(itemsFiltered, false, false);
    }

    private static bool IsReviewed(FinanceTransaction transaction, IReadOnlyDictionary<Guid, bool> explicitStates)
    {
        if (explicitStates.TryGetValue(transaction.Id, out var reviewed)) return reviewed;
        var source = transaction.CategorizationSource?.Trim().ToLowerInvariant() ?? "none";
        var external = transaction.CategoryId.HasValue && source is not "none" and not "rule" and not "catalog" and not "manual";
        return source == "manual" || external;
    }

    private static bool CanPairAsTransfer(IReadOnlyList<FinanceTransaction> items)
    {
        if (items.Count != 2) return false;
        var a = items[0]; var b = items[1];
        if (a.AccountId == b.AccountId || a.IsTransfer || b.IsTransfer || a.TransferGroupId.HasValue || b.TransferGroupId.HasValue) return false;
        if (Math.Sign(a.Amount) == Math.Sign(b.Amount) || a.Amount == 0 || b.Amount == 0) return false;
        if (!string.Equals(a.Currency, b.Currency, StringComparison.OrdinalIgnoreCase)) return false;
        if (Math.Abs(Math.Abs(a.Amount) - Math.Abs(b.Amount)) > 0.01m) return false;
        var ad = a.BookingDate ?? a.ValueDate; var bd = b.BookingDate ?? b.ValueDate;
        return ad.HasValue && bd.HasValue && Math.Abs(ad.Value.DayNumber - bd.Value.DayNumber) <= 3;
    }

    private static async Task<bool> TagsValid(FullWorthDbContext db, Guid fullWorthSpaceId, Guid[] tagIds, CancellationToken ct)
    {
        if (tagIds.Length == 0) return true;
        var connection = await ParitySql.OpenAsync(db, ct);
        await using var command = ParitySql.Command(connection,
            "SELECT count(*) FROM \"FinanceTags\" WHERE \"FullWorthSpaceId\"=@space AND \"Id\"=ANY(@ids)",
            ("@space", fullWorthSpaceId), ("@ids", tagIds));
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct)) == tagIds.Length;
    }

    private static async Task<HashSet<Guid>> LoadTagTransactionIds(FullWorthDbContext db, Guid fullWorthSpaceId, Guid tagId, CancellationToken ct)
    {
        var connection = await ParitySql.OpenAsync(db, ct);
        await using var command = ParitySql.Command(connection, """
SELECT tt."TransactionId" FROM "TransactionTags" tt JOIN "FinanceTags" ft ON ft."Id"=tt."TagId"
WHERE ft."FullWorthSpaceId"=@space AND ft."Id"=@tag
""", ("@space", fullWorthSpaceId), ("@tag", tagId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var ids = new HashSet<Guid>(); while (await reader.ReadAsync(ct)) ids.Add(reader.GetGuid(0));
        return ids;
    }

    private static async Task<Dictionary<Guid, bool>> LoadReviewStates(FullWorthDbContext db, Guid fullWorthSpaceId, CancellationToken ct)
    {
        var connection = await ParitySql.OpenAsync(db, ct);
        await using var command = ParitySql.Command(connection,
            "SELECT \"TransactionId\",\"IsReviewed\" FROM \"TransactionReviewStates\" WHERE \"FullWorthSpaceId\"=@space", ("@space", fullWorthSpaceId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var states = new Dictionary<Guid, bool>(); while (await reader.ReadAsync(ct)) states[reader.GetGuid(0)] = reader.GetBoolean(1);
        return states;
    }

    private static async Task<bool> AnyContractLinkExists(FullWorthDbContext db, Guid[] ids, CancellationToken ct)
    {
        var connection = await ParitySql.OpenAsync(db, ct);
        await using var command = ParitySql.Command(connection,
            "SELECT EXISTS(SELECT 1 FROM \"ContractTransactionLinks\" WHERE \"TransactionId\"=ANY(@ids))", ("@ids", ids));
        return Convert.ToBoolean(await command.ExecuteScalarAsync(ct));
    }

    private sealed record Selection(List<FinanceTransaction> Items, bool Forbidden, bool TooLarge);
}
