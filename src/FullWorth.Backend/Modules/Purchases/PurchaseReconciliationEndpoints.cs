using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Purchases;

public static class PurchaseReconciliationEndpoints
{
    public static IEndpointRouteBuilder MapPurchaseReconciliationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/purchases/{id:guid}/reconciliation", async (Guid id, FullWorthDbContext db, CancellationToken ct) =>
        {
            var purchase = await db.Purchases.AsNoTracking().Where(x => x.Id == id)
                .Select(x => new { x.Id, x.TransactionId, x.TotalAmount, x.Currency, ItemTotal = x.Items.Sum(i => (decimal?)i.TotalPrice) ?? 0m })
                .SingleOrDefaultAsync(ct);
            if (purchase is null) return Results.NotFound();

            decimal? transactionAmount = null;
            if (purchase.TransactionId.HasValue)
                transactionAmount = await db.Transactions.AsNoTracking().Where(x => x.Id == purchase.TransactionId.Value).Select(x => (decimal?)x.Amount).SingleOrDefaultAsync(ct);

            var itemDifference = purchase.TotalAmount - purchase.ItemTotal;
            decimal? transactionDifference = transactionAmount.HasValue ? Math.Abs(transactionAmount.Value) - purchase.TotalAmount : null;
            return Results.Ok(new
            {
                purchase.Id,
                purchase.TransactionId,
                purchase.Currency,
                purchaseTotal = purchase.TotalAmount,
                itemTotal = purchase.ItemTotal,
                itemDifference,
                transactionAmount,
                transactionDifference,
                itemsReconciled = Math.Abs(itemDifference) <= .01m,
                transactionReconciled = !transactionAmount.HasValue || Math.Abs(transactionDifference!.Value) <= .01m,
                fullyReconciled = Math.Abs(itemDifference) <= .01m && (!transactionAmount.HasValue || Math.Abs(transactionDifference!.Value) <= .01m)
            });
        }).WithTags("Purchases");
        return app;
    }
}
