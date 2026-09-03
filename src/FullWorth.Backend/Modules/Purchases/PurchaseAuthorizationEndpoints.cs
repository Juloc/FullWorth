using FullWorth.Backend.Data;
using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Purchases;

public static class PurchaseAuthorizationEndpoints
{
    public static IEndpointRouteBuilder MapAuthorizedPurchaseEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/purchases").WithTags("Purchases");

        group.MapGet("/", async (
            Guid? fullWorthSpaceId,
            Guid? transactionId,
            string? source,
            DateOnly? from,
            DateOnly? to,
            CurrentUserContext currentUser,
            PurchaseAuthorizationStore store,
            CancellationToken ct) =>
            Results.Ok(await store.ListForUserAsync(
                currentUser.RequireUserId(),
                fullWorthSpaceId,
                transactionId,
                source,
                from,
                to,
                ct)));

        group.MapGet("/{id:guid}", async (
            Guid id,
            Guid fullWorthSpaceId,
            CurrentUserContext currentUser,
            PurchaseAuthorizationStore store,
            CancellationToken ct) =>
        {
            var purchase = await store.GetForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, ct);
            return purchase is null ? Results.NotFound() : Results.Ok(purchase);
        });

        group.MapPost("/", async (
            Guid fullWorthSpaceId,
            PurchaseWrite request,
            CurrentUserContext currentUser,
            PurchaseAuthorizationStore store,
            CancellationToken ct) =>
        {
            var outcome = await store.CreateForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, request, ct);
            return Outcome(outcome, created: true);
        });

        group.MapPut("/{id:guid}", async (
            Guid id,
            Guid fullWorthSpaceId,
            PurchaseWrite request,
            CurrentUserContext currentUser,
            PurchaseAuthorizationStore store,
            CancellationToken ct) =>
        {
            var outcome = await store.UpdateForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, request, ct);
            return Outcome(outcome);
        });

        group.MapPut("/{id:guid}/items", async (
            Guid id,
            Guid fullWorthSpaceId,
            List<PurchaseItemWrite> items,
            CurrentUserContext currentUser,
            PurchaseAuthorizationStore store,
            FullWorthDbContext db,
            CancellationToken ct) =>
        {
            var userId = currentUser.RequireUserId();
            var previousPurchase = await store.GetForUserAsync(userId, fullWorthSpaceId, id, ct);
            if (previousPurchase is null) return Results.NotFound();
            var financialStore = new PurchaseDiscountDetailsStore(db, store);
            var previousFinancial = await financialStore.GetAsync(userId, fullWorthSpaceId, id, ct);

            var outcome = await store.ReplaceItemsForUserAsync(userId, fullWorthSpaceId, id, items, ct);
            if (outcome.Result != PurchaseMutationResult.Success)
                return outcome.Result switch
                {
                    PurchaseMutationResult.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
                    PurchaseMutationResult.Invalid => Results.BadRequest(new { error = outcome.Error ?? "Invalid purchase items." }),
                    _ => Results.NotFound()
                };

            // ReplaceItemsForUserAsync predates structured receipt financials and recreates item ids.
            // Transparently reattach the old per-item metadata/discount FKs by the same visible row order.
            // Smart Review immediately overwrites these preserved values with the user's edited financials;
            // legacy editors simply keep them intact instead of silently losing receipt evidence.
            if (previousFinancial is not null)
            {
                var freshFinancial = await financialStore.GetAsync(userId, fullWorthSpaceId, id, ct);
                if (freshFinancial is not null && freshFinancial.Items.Count == items.Count)
                {
                    var oldFinancialById = previousFinancial.Items.ToDictionary(x => x.PurchaseItemId);
                    var oldIndexById = previousPurchase.Items.Select((item, index) => new { item.Id, index })
                        .ToDictionary(x => x.Id, x => x.index);
                    var itemFinancials = new List<PurchaseItemFinancialWrite>();
                    for (var index = 0; index < freshFinancial.Items.Count; index++)
                    {
                        PurchaseItemFinancialRow? old = null;
                        if (index < previousPurchase.Items.Count)
                            oldFinancialById.TryGetValue(previousPurchase.Items[index].Id, out old);
                        var fresh = freshFinancial.Items[index];
                        itemFinancials.Add(new PurchaseItemFinancialWrite(
                            fresh.PurchaseItemId,
                            old?.OriginalUnitPrice,
                            old?.DiscountAmount ?? 0m,
                            old?.DiscountLabel,
                            old?.DepositAmount ?? 0m));
                    }

                    var discounts = previousFinancial.Discounts.Select(discount =>
                    {
                        Guid? newItemId = null;
                        if (discount.PurchaseItemId is { } oldId && oldIndexById.TryGetValue(oldId, out var index) && index < freshFinancial.Items.Count)
                            newItemId = freshFinancial.Items[index].PurchaseItemId;
                        return new PurchaseDiscountWrite(
                            discount.Id,
                            newItemId,
                            discount.Type,
                            discount.Label,
                            discount.Amount,
                            discount.Percentage,
                            discount.CouponCode,
                            discount.RawText,
                            discount.Source,
                            discount.Confidence);
                    }).ToList();

                    var preserved = await financialStore.SaveAsync(
                        userId,
                        fullWorthSpaceId,
                        id,
                        new PurchaseFinancialWrite(
                            previousFinancial.SubtotalAmount,
                            previousFinancial.DiscountAmount,
                            previousFinancial.DepositAmount,
                            previousFinancial.TaxAmount,
                            previousFinancial.RoundingAmount,
                            itemFinancials,
                            discounts),
                        ct);
                    if (preserved != PurchaseMutationResult.Success)
                        return Results.Problem("Purchase items were saved but receipt financial metadata could not be preserved.");
                }
            }
            return Results.NoContent();
        });

        group.MapGet("/{id:guid}/match-candidates", async (
            Guid id,
            Guid fullWorthSpaceId,
            CurrentUserContext currentUser,
            PurchaseAuthorizationStore store,
            CancellationToken ct) =>
        {
            var outcome = await store.MatchCandidatesForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, ct);
            return outcome.Result switch
            {
                PurchaseMutationResult.Success => Results.Ok(outcome.Candidates ?? []),
                PurchaseMutationResult.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
                _ => Results.NotFound()
            };
        });

        group.MapPost("/{id:guid}/link", async (
            Guid id,
            Guid fullWorthSpaceId,
            LinkPurchaseRequest request,
            CurrentUserContext currentUser,
            PurchaseAuthorizationStore store,
            CancellationToken ct) =>
        {
            var result = await store.LinkForUserAsync(
                currentUser.RequireUserId(),
                fullWorthSpaceId,
                id,
                request.TransactionId,
                request.Confidence,
                ct);
            return result switch
            {
                PurchaseMutationResult.Success => Results.NoContent(),
                PurchaseMutationResult.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
                PurchaseMutationResult.Invalid => Results.Conflict(new { error = "This purchase already has payment allocations. Edit them in the purchase workspace instead of replacing them through the legacy single-link route." }),
                _ => Results.NotFound()
            };
        });

        group.MapGet("/{id:guid}/reconciliation", async (
            Guid id,
            Guid fullWorthSpaceId,
            CurrentUserContext currentUser,
            PurchaseAuthorizationStore store,
            FullWorthDbContext db,
            CancellationToken ct) =>
        {
            var userId = currentUser.RequireUserId();
            if (await store.GetAccessAsync(userId, fullWorthSpaceId, id, ct) == PurchaseAccessLevel.None)
                return Results.NotFound();
            var state = await PurchaseFinancialReconciliation.CalculateAsync(db, fullWorthSpaceId, id, ct);
            return state is null ? Results.NotFound() : Results.Ok(state);
        });

        return app;
    }

    private static IResult Outcome(PurchaseMutationOutcome outcome, bool created = false) => outcome.Result switch
    {
        PurchaseMutationResult.Success when created && outcome.Purchase is not null =>
            Results.Created($"/api/purchases/{outcome.Purchase.Id}?fullWorthSpaceId={outcome.Purchase.FullWorthSpaceId}", outcome.Purchase),
        PurchaseMutationResult.Success when outcome.Purchase is not null => Results.Ok(outcome.Purchase),
        PurchaseMutationResult.Success => Results.NoContent(),
        PurchaseMutationResult.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
        PurchaseMutationResult.Invalid => Results.BadRequest(new { error = outcome.Error ?? "Invalid purchase." }),
        _ => Results.NotFound()
    };
}
