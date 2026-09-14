using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Purchases;

/// <summary>
/// Die Abgleichsansicht eines Kaufs: stimmt die Summe, und hat der Benutzer eine verbleibende
/// Differenz akzeptiert?
///
/// Die Abfragen dazu lagen bis 2026-09-15 in dieser Datei - der Handler oeffnete eine Verbindung,
/// schrieb ein INSERT ... ON CONFLICT und setzte danach selbst den Status. Beides liegt jetzt in
/// PurchaseReconciliationStore; hier bleibt die Uebersetzung nach HTTP.
/// </summary>
public static class PurchaseReviewEndpoints
{
    public static IEndpointRouteBuilder MapPurchaseReviewEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/purchase-review").WithTags("Purchases");
        group.MapGet("/{purchaseId:guid}", State);
        group.MapPost("/{purchaseId:guid}/confirm-difference", ConfirmDifference);
        group.MapPost("/{purchaseId:guid}/confirm", ConfirmPurchase);
        return app;
    }

    private static async Task<IResult> State(
        Guid purchaseId, Guid fullWorthSpaceId, CurrentUserContext currentUser,
        PurchaseAuthorizationStore authorization, PurchaseReconciliationStore reconciliation,
        CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        var access = await authorization.GetAccessAsync(userId, fullWorthSpaceId, purchaseId, ct);
        if (access == PurchaseAccessLevel.None) return Results.NotFound();

        var state = await reconciliation.CalculateAsync(fullWorthSpaceId, purchaseId, ct);
        if (state is null) return Results.NotFound();

        var confirmation = await reconciliation.ReadConfirmationAsync(purchaseId, ct);
        var confirmed = confirmation?.Matches(state) == true;
        return Results.Ok(new
        {
            state.PurchaseId,
            state.TransactionId,
            state.Currency,
            state.ItemTotal,
            state.PurchaseTotal,
            state.TransactionAmount,
            state.SubtotalAmount,
            state.DiscountAmount,
            state.DepositAmount,
            state.TaxAmount,
            state.RoundingAmount,
            state.CalculatedTotal,
            state.FinancialDifference,
            state.ReconciliationBasis,
            state.ItemDifference,
            state.TransactionDifference,
            state.ItemsReconciled,
            state.TransactionReconciled,
            state.FullyReconciled,
            differenceConfirmed = confirmed,
            confirmedAt = confirmed ? confirmation!.ConfirmedAt : (DateTimeOffset?)null,
            canWrite = access == PurchaseAccessLevel.Write
        });
    }

    private static async Task<IResult> ConfirmDifference(
        Guid purchaseId, Guid fullWorthSpaceId, CurrentUserContext currentUser, SpaceAccess space,
        PurchaseAuthorizationStore authorization, PurchaseReconciliationStore reconciliation,
        CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await space.HasCapabilityAsync(userId, fullWorthSpaceId, "purchases.manage", ct))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (await authorization.GetAccessAsync(userId, fullWorthSpaceId, purchaseId, ct) != PurchaseAccessLevel.Write)
            return Results.NotFound();

        var state = await reconciliation.CalculateAsync(fullWorthSpaceId, purchaseId, ct);
        if (state is null) return Results.NotFound();

        var confirmedAt = await reconciliation.ConfirmDifferenceAsync(userId, fullWorthSpaceId, purchaseId, state, ct);
        return Results.Ok(new { state.ItemDifference, state.FinancialDifference, state.TransactionDifference, confirmedAt });
    }

    private static async Task<IResult> ConfirmPurchase(
        Guid purchaseId, Guid fullWorthSpaceId, CurrentUserContext currentUser, SpaceAccess space,
        PurchaseAuthorizationStore authorization, PurchaseReconciliationStore reconciliation,
        CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await space.HasCapabilityAsync(userId, fullWorthSpaceId, "purchases.manage", ct))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (await authorization.GetAccessAsync(userId, fullWorthSpaceId, purchaseId, ct) != PurchaseAccessLevel.Write)
            return Results.NotFound();

        var state = await reconciliation.CalculateAsync(fullWorthSpaceId, purchaseId, ct);
        if (state is null) return Results.NotFound();

        if (!state.FullyReconciled)
        {
            var confirmation = await reconciliation.ReadConfirmationAsync(purchaseId, ct);
            if (confirmation?.Matches(state) != true)
                return Results.Conflict(new
                {
                    error = "Current reconciliation differences must be confirmed before confirming this purchase.",
                    code = "difference_confirmation_required",
                    state.ItemDifference,
                    state.FinancialDifference,
                    state.TransactionDifference
                });
        }

        return await reconciliation.MarkConfirmedAsync(userId, fullWorthSpaceId, purchaseId, ct)
            ? Results.NoContent()
            : Results.NotFound();
    }
}
