using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Security;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Transactions;

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

public static class TransactionBulkAdvancedEndpoints
{
    private const int MaxExplicitIds = 1000;
    private const int MaxFilterMatches = 5000;

    public static IEndpointRouteBuilder MapTransactionBulkAdvancedEndpoints(this IEndpointRouteBuilder app)
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
        SpaceAccess space,
        AdvancedBulkStore bulk,
        CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        var selection = await ResolveSelection(space, bulk, userId, fullWorthSpaceId, request.Filter, request.TransactionIds, ct);
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
        SpaceAccess space,
        AdvancedBulkStore bulk,
        CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!request.ConfirmSelection)
            return Results.BadRequest(new { error = "Explicit bulk confirmation is required." });

        var selection = await ResolveSelection(space, bulk, userId, fullWorthSpaceId, request.Filter, request.TransactionIds, ct);
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
        if (categorizeAction && !await space.HasCapabilityAsync(userId, fullWorthSpaceId, "transactions.categorize", ct))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (writeAction && !await space.HasCapabilityAsync(userId, fullWorthSpaceId, "transactions.write", ct))
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        if (request.UpdateCategory && request.CategoryId.HasValue &&
            !await bulk.CategoryExistsAsync(fullWorthSpaceId, request.CategoryId.Value, ct))
            return Results.BadRequest(new { error = "Category is invalid for this FullWorth Space." });

        var addTags = (request.AddTagIds ?? []).Distinct().ToArray();
        var removeTags = (request.RemoveTagIds ?? []).Distinct().ToArray();
        if (!await bulk.TagsValidAsync(fullWorthSpaceId, addTags.Concat(removeTags).Distinct().ToArray(), ct))
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
            if (!await space.HasCapabilityAsync(userId, fullWorthSpaceId, "contracts.manage", ct))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var contract = await bulk.FindContractAsync(fullWorthSpaceId, request.ContractId.Value, ct);
            if (contract is null || contract.MergedIntoContractId is not null || !contract.IsActive)
                return Results.BadRequest(new { error = "Contract is unavailable." });
            if (contract.AccountId.HasValue)
            {
                var writable = await space.WritableAccountIdsAsync(userId, fullWorthSpaceId, ct);
                if (!writable.Contains(contract.AccountId.Value))
                    return Results.StatusCode(StatusCodes.Status403Forbidden);
            }
            if (contractAction == "link")
            {
                if (selection.Items.Any(transaction => transaction.Amount >= 0))
                    return Results.BadRequest(new { error = "Only expense transactions can be bulk-linked to a contract." });
                if (await bulk.AnyContractLinkExistsAsync(selection.Items.Select(item => item.Id).ToArray(), ct))
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

        return await bulk.ApplyAsync(
            userId, fullWorthSpaceId, selection.Items, request, addTags, removeTags, contractAction, ct)
            ? Results.Ok(new { changed = selection.Items.Count, pairedTransfer = request.PairAsTransfer })
            : Results.Conflict(new { error = "Bulk update failed atomically. No selected transaction was changed." });
    }

    /// <summary>
    /// Welche Buchungen die Anfrage meint. Ausdrueckliche Ids muessen VOLLSTAENDIG schreibbar sein -
    /// fehlt eine, gilt die ganze Auswahl als fremd, statt stillschweigend weniger zu aendern.
    /// </summary>
    private static async Task<Selection> ResolveSelection(
        SpaceAccess space,
        AdvancedBulkStore bulk,
        Guid userId,
        Guid fullWorthSpaceId,
        AdvancedTransactionBulkFilter? filter,
        IReadOnlyList<Guid>? explicitIds,
        CancellationToken ct)
    {
        var writable = await space.WritableAccountIdsAsync(userId, fullWorthSpaceId, ct);

        if (explicitIds is { Count: > 0 })
        {
            var explicitSet = explicitIds.Distinct().ToArray();
            if (explicitSet.Length > MaxExplicitIds) return new([], false, true);
            var chosen = await bulk.ByIdsAsync(writable, explicitSet, ct);
            return chosen.Count == explicitSet.Length ? new(chosen, false, false) : new([], true, false);
        }

        filter ??= new AdvancedTransactionBulkFilter();
        if (filter.AccountId.HasValue && !writable.Contains(filter.AccountId.Value)) return new([], true, false);

        var itemsFiltered = await bulk.ByFilterAsync(writable, filter, MaxFilterMatches, ct);
        if (itemsFiltered.Count > MaxFilterMatches) return new([], false, true);

        if (filter.TagId.HasValue)
        {
            var matchingTags = await bulk.TransactionIdsWithTagAsync(fullWorthSpaceId, filter.TagId.Value, ct);
            itemsFiltered = itemsFiltered.Where(transaction => matchingTags.Contains(transaction.Id)).ToList();
        }
        if (string.Equals(filter.ReviewState, "reviewed", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(filter.ReviewState, "needs_review", StringComparison.OrdinalIgnoreCase))
        {
            var reviews = await bulk.ReviewStatesAsync(fullWorthSpaceId, ct);
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

    private sealed record Selection(List<FinanceTransaction> Items, bool Forbidden, bool TooLarge);
}
