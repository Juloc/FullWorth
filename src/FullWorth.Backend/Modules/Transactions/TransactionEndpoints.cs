using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Transactions;

public static class TransactionEndpoints
{
    public static IEndpointRouteBuilder MapTransactionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/transactions").WithTags("Transactions");

        group.MapGet("/", async (
            Guid? fullWorthSpaceId,
            Guid? accountId,
            Guid? categoryId,
            DateOnly? from,
            DateOnly? to,
            string? direction,
            string? query,
            bool? includeIgnored,
            bool? transfersOnly,
            string? sort,
            string? order,
            int? offset,
            int? limit,
            Guid? accountGroupId,
            bool? includeDescendants,
            string? merchant,
            Guid? merchantId,
            decimal? minAmount,
            decimal? maxAmount,
            bool? refundOnly,
            bool? hasReceipt,
            string? status,
            bool? ignoredOnly,
            Guid[]? collectionIds,
            CurrentUserContext currentUser,
            TransactionStore store,
            CancellationToken ct) =>
            Results.Ok(await store.SearchForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId,
                new TransactionQuery(
                    accountId, categoryId, from, to, direction, query, includeIgnored, transfersOnly,
                    sort, order, offset, limit, accountGroupId, includeDescendants, merchant,
                    minAmount, maxAmount, refundOnly, hasReceipt, status, ignoredOnly, merchantId,
                    collectionIds), ct)));

        group.MapGet("/{id:guid}", async (Guid id, Guid fullWorthSpaceId, CurrentUserContext currentUser, TransactionStore store, CancellationToken ct) =>
        {
            var item = await store.GetForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, ct);
            return item is null ? Results.NotFound() : Results.Ok(item);
        });

        group.MapPost("/", async (Guid fullWorthSpaceId, CreateTransactionRequest request, CurrentUserContext currentUser, TransactionStore store, CancellationToken ct) =>
        {
            try
            {
                var (result, id) = await store.CreateManualForOwnerAsync(currentUser.RequireUserId(), fullWorthSpaceId, request, ct);
                return result switch
                {
                    TransactionCreateResult.Created => Results.Created($"/api/transactions/{id}?fullWorthSpaceId={fullWorthSpaceId}", new { id }),
                    TransactionCreateResult.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
                    TransactionCreateResult.NotManual => Results.Conflict(new { error = "Archived import accounts must be linked to a real account before adding manual corrections." }),
                    TransactionCreateResult.InvalidCategory => Results.BadRequest(new { error = "Category must belong to the FullWorth Space." }),
                    _ => Results.NotFound()
                };
            }
            catch (ArgumentException exception) { return Results.BadRequest(new { error = exception.Message }); }
        });

        group.MapDelete("/{id:guid}", async (Guid id, Guid fullWorthSpaceId, CurrentUserContext currentUser, TransactionStore store, CancellationToken ct) =>
        {
            var userId = currentUser.RequireUserId();
            var ownership = await store.GetOwnershipForUserAsync(userId, fullWorthSpaceId, id, ct);
            if (ownership is null) return Results.NotFound();
            if (ownership != AccountOwnershipTypes.Owner) return Results.StatusCode(StatusCodes.Status403Forbidden);
            return await store.DeleteManualForOwnerAsync(userId, fullWorthSpaceId, id, ct) switch
            {
                TransactionDeleteResult.Deleted => Results.NoContent(),
                TransactionDeleteResult.NotManual => Results.Conflict(new { error = "Only manually booked transactions can be deleted." }),
                TransactionDeleteResult.Referenced => Results.Conflict(new { error = "This transaction is linked to a receipt; remove it first." }),
                _ => Results.NotFound()
            };
        });

        group.MapPatch("/{id:guid}/classification", async (Guid id, Guid fullWorthSpaceId, TransactionClassification request, CurrentUserContext currentUser, TransactionStore store, CancellationToken ct) =>
        {
            var userId = currentUser.RequireUserId();
            var ownership = await store.GetOwnershipForUserAsync(userId, fullWorthSpaceId, id, ct);
            if (ownership is null) return Results.NotFound();
            if (ownership != AccountOwnershipTypes.Owner) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var result = await store.ClassifyForOwnerAsync(userId, fullWorthSpaceId, id, request, ct);
            return result == TransactionClassificationResult.Updated ? Results.NoContent() : Results.NotFound();
        });

        group.MapPatch("/{id:guid}/refund", async (Guid id, Guid fullWorthSpaceId, RefundLink request, CurrentUserContext currentUser, TransactionStore store, CancellationToken ct) =>
        {
            var userId = currentUser.RequireUserId();
            var ownership = await store.GetOwnershipForUserAsync(userId, fullWorthSpaceId, id, ct);
            if (ownership is null) return Results.NotFound();
            if (ownership != AccountOwnershipTypes.Owner) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var result = await store.LinkRefundForOwnerAsync(userId, fullWorthSpaceId, id, request?.OriginalTransactionId, request?.RefundCategoryId, ct);
            return result switch
            {
                RefundLinkResult.Updated => Results.NoContent(),
                RefundLinkResult.Invalid => Results.BadRequest(new { error = "invalid_refund_link" }),
                _ => Results.NotFound()
            };
        });

        group.MapPost("/{id:guid}/transfer-link", async (Guid id, Guid fullWorthSpaceId, TransferLinkRequest request, CurrentUserContext currentUser, TransactionStore store, CancellationToken ct) =>
        {
            var result = await store.LinkTransferForOwnerAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, request.OtherTransactionId, ct);
            return result switch
            {
                TransferLinkResult.Linked => Results.NoContent(),
                TransferLinkResult.Invalid => Results.BadRequest(new { error = "invalid_transfer_link" }),
                _ => Results.NotFound()
            };
        });

        group.MapDelete("/{id:guid}/transfer-link", async (Guid id, Guid fullWorthSpaceId, CurrentUserContext currentUser, TransactionStore store, CancellationToken ct) =>
        {
            var result = await store.UnlinkTransferForOwnerAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, ct);
            return result switch
            {
                TransferUnlinkResult.Unlinked => Results.NoContent(),
                TransferUnlinkResult.NotLinked => Results.Conflict(new { error = "not_linked" }),
                _ => Results.NotFound()
            };
        });

        group.MapGet("/{id:guid}/allocations", async (Guid id, Guid fullWorthSpaceId, CurrentUserContext currentUser, TransactionStore store, CancellationToken ct) =>
        {
            var result = await store.GetAllocationsForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });

        group.MapPut("/{id:guid}/allocations", async (Guid id, Guid fullWorthSpaceId, List<AllocationLine> request, CurrentUserContext currentUser, TransactionStore store, CancellationToken ct) =>
        {
            var userId = currentUser.RequireUserId();
            var ownership = await store.GetOwnershipForUserAsync(userId, fullWorthSpaceId, id, ct);
            if (ownership is null) return Results.NotFound();
            if (ownership != AccountOwnershipTypes.Owner) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var result = await store.ReplaceAllocationsForOwnerAsync(userId, fullWorthSpaceId, id, request ?? [], ct);
            return result switch
            {
                AllocationResult.Updated => Results.NoContent(),
                AllocationResult.Unbalanced => Results.BadRequest(new { error = "Allocations must net to the transaction amount." }),
                AllocationResult.InvalidCategory => Results.BadRequest(new { error = "Allocation category must belong to the FullWorth Space." }),
                AllocationResult.InvalidPurchaseItem => Results.BadRequest(new { error = "Article must belong to a visible purchase linked to this transaction, and its category cannot be overridden here." }),
                _ => Results.NotFound()
            };
        });

        return app;
    }
}
