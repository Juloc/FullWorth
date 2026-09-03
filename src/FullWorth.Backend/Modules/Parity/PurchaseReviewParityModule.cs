using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Audit;
using FullWorth.Backend.Modules.Purchases;
using FullWorth.Backend.Security;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Parity;

/// <summary>
/// Compatibility endpoints for the parity review UI. Reconciliation is delegated to the canonical
/// discount/deposit-aware calculator, and difference confirmations are bound to the exact reconciliation
/// state fingerprint so any semantic edit to items/discounts/financials silently invalidates a stale
/// confirmation while an id-only rewrite of the identical basket keeps it valid.
/// </summary>
public static class PurchaseReviewParityEndpoints
{
    public static IEndpointRouteBuilder MapPurchaseReviewParityEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/purchase-review").WithTags("Purchases");
        group.MapGet("/{purchaseId:guid}", State);
        group.MapPost("/{purchaseId:guid}/confirm-difference", ConfirmDifference);
        group.MapPost("/{purchaseId:guid}/confirm", ConfirmPurchase);
        return app;
    }

    private static async Task<IResult> State(
        Guid purchaseId, Guid fullWorthSpaceId, CurrentUserContext currentUser,
        FullWorthDbContext db, PurchaseAuthorizationStore authorization, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        var access = await authorization.GetAccessAsync(userId, fullWorthSpaceId, purchaseId, ct);
        if (access == PurchaseAccessLevel.None) return Results.NotFound();
        var state = await PurchaseFinancialReconciliation.CalculateAsync(db, fullWorthSpaceId, purchaseId, ct);
        if (state is null) return Results.NotFound();
        var confirmation = await LoadConfirmationAsync(db, purchaseId, ct);
        var confirmed = Matches(confirmation, state);
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
        Guid purchaseId, Guid fullWorthSpaceId, CurrentUserContext currentUser,
        FullWorthDbContext db, PurchaseAuthorizationStore authorization, AuditService audit, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await PermissionsErgonomicsParityEndpoints.HasCapabilityAsync(db, userId, fullWorthSpaceId, "purchases.manage", ct))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (await authorization.GetAccessAsync(userId, fullWorthSpaceId, purchaseId, ct) != PurchaseAccessLevel.Write)
            return Results.NotFound();
        var state = await PurchaseFinancialReconciliation.CalculateAsync(db, fullWorthSpaceId, purchaseId, ct);
        if (state is null) return Results.NotFound();

        var connection = await ParitySql.OpenAsync(db, ct);
        var now = DateTimeOffset.UtcNow;
        await using var command = ParitySql.Command(connection, """
INSERT INTO "PurchaseReconciliationConfirmations"
("PurchaseId","FullWorthSpaceId","UserId","ItemDifference","TransactionDifference","StateFingerprint","ConfirmedAt")
VALUES (@purchase,@space,@user,@item,@transaction,@fingerprint,@now)
ON CONFLICT ("PurchaseId") DO UPDATE SET
 "FullWorthSpaceId"=EXCLUDED."FullWorthSpaceId","UserId"=EXCLUDED."UserId",
 "ItemDifference"=EXCLUDED."ItemDifference","TransactionDifference"=EXCLUDED."TransactionDifference",
 "StateFingerprint"=EXCLUDED."StateFingerprint","ConfirmedAt"=EXCLUDED."ConfirmedAt"
""",
            ("@purchase", purchaseId), ("@space", fullWorthSpaceId), ("@user", userId), ("@item", state.ItemDifference),
            ("@transaction", state.TransactionDifference), ("@fingerprint", state.StateFingerprint), ("@now", now));
        await command.ExecuteNonQueryAsync(ct);
        audit.Record(fullWorthSpaceId, userId, "purchase.difference.confirmed", "Purchase", purchaseId);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { state.ItemDifference, state.FinancialDifference, state.TransactionDifference, confirmedAt = now });
    }

    private static async Task<IResult> ConfirmPurchase(
        Guid purchaseId, Guid fullWorthSpaceId, CurrentUserContext currentUser,
        FullWorthDbContext db, PurchaseAuthorizationStore authorization, AuditService audit, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await PermissionsErgonomicsParityEndpoints.HasCapabilityAsync(db, userId, fullWorthSpaceId, "purchases.manage", ct))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (await authorization.GetAccessAsync(userId, fullWorthSpaceId, purchaseId, ct) != PurchaseAccessLevel.Write)
            return Results.NotFound();
        var state = await PurchaseFinancialReconciliation.CalculateAsync(db, fullWorthSpaceId, purchaseId, ct);
        if (state is null) return Results.NotFound();
        if (!state.FullyReconciled)
        {
            var confirmation = await LoadConfirmationAsync(db, purchaseId, ct);
            if (!Matches(confirmation, state))
                return Results.Conflict(new
                {
                    error = "Current reconciliation differences must be confirmed before confirming this purchase.",
                    code = "difference_confirmation_required",
                    state.ItemDifference,
                    state.FinancialDifference,
                    state.TransactionDifference
                });
        }
        var purchase = await db.Purchases.SingleOrDefaultAsync(row => row.Id == purchaseId && row.FullWorthSpaceId == fullWorthSpaceId, ct);
        if (purchase is null) return Results.NotFound();
        purchase.Status = "confirmed";
        purchase.UpdatedAt = DateTimeOffset.UtcNow;
        audit.Record(fullWorthSpaceId, userId, "purchase.confirmed", "Purchase", purchaseId);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private sealed record Confirmation(decimal ItemDifference, decimal? TransactionDifference, string StateFingerprint, DateTimeOffset ConfirmedAt);

    private static async Task<Confirmation?> LoadConfirmationAsync(FullWorthDbContext db, Guid purchaseId, CancellationToken ct)
    {
        var connection = await ParitySql.OpenAsync(db, ct);
        await using var command = ParitySql.Command(connection,
            "SELECT \"ItemDifference\",\"TransactionDifference\",\"StateFingerprint\",\"ConfirmedAt\" FROM \"PurchaseReconciliationConfirmations\" WHERE \"PurchaseId\"=@id",
            ("@id", purchaseId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new Confirmation(
                ParitySql.Decimal(reader, "ItemDifference"),
                ParitySql.NullableDecimal(reader, "TransactionDifference"),
                ParitySql.String(reader, "StateFingerprint") ?? string.Empty,
                ParitySql.Timestamp(reader, "ConfirmedAt"))
            : null;
    }

    private static bool Matches(Confirmation? confirmation, PurchaseFinancialReconciliationState state)
    {
        if (confirmation is null) return false;
        if (confirmation.ItemDifference != state.ItemDifference) return false;
        if (confirmation.TransactionDifference != state.TransactionDifference) return false;
        return !string.IsNullOrWhiteSpace(confirmation.StateFingerprint) &&
               string.Equals(confirmation.StateFingerprint, state.StateFingerprint, StringComparison.Ordinal);
    }
}
