using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Tax;

public sealed record TaxDocumentTarget(Guid PurchaseId, string UploadPath);

public sealed class TaxDocumentTargetService(FullWorthDbContext db, TaxStore store)
{
    public async Task<TaxDocumentTarget?> ResolveAsync(Guid userId, Guid fullWorthSpaceId, Guid candidateId, CancellationToken ct)
    {
        var candidate = await store.GetCandidateAsync(userId, fullWorthSpaceId, candidateId, ct);
        if (candidate is null) return null;

        var source = await db.TaxCandidateSources.AsNoTracking()
            .Where(x => x.TaxCandidateId == candidateId && x.IsPrimary)
            .OrderBy(x => x.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (source is null) return null;

        Guid? purchaseId = source.SourceType switch
        {
            TaxSourceTypes.Purchase => source.SourceId,
            TaxSourceTypes.PurchaseItem => await db.PurchaseItems.AsNoTracking()
                .Where(x => x.Id == source.SourceId)
                .Select(x => (Guid?)x.PurchaseId)
                .SingleOrDefaultAsync(ct),
            _ => null
        };
        if (!purchaseId.HasValue) return null;

        var visible = await db.Purchases.AsNoTracking().AnyAsync(purchase =>
            purchase.Id == purchaseId.Value && purchase.FullWorthSpaceId == fullWorthSpaceId &&
            db.FullWorthSpaceMembers.Any(member => member.FullWorthSpaceId == fullWorthSpaceId && member.UserId == userId) &&
            (purchase.Visibility != "private" || purchase.CreatedByUserId == userId) &&
            (!purchase.PaymentLinks.Any() || purchase.PaymentLinks.Any(link => db.Transactions.Any(transaction =>
                transaction.Id == link.TransactionId && db.Accounts.Any(account => account.Id == transaction.AccountId && account.Owners.Any(owner => owner.UserId == userId))))) &&
            (purchase.TransactionId == null || db.Transactions.Any(transaction =>
                transaction.Id == purchase.TransactionId.Value && db.Accounts.Any(account => account.Id == transaction.AccountId && account.Owners.Any(owner => owner.UserId == userId)))), ct);
        if (!visible) return null;

        return new TaxDocumentTarget(purchaseId.Value, $"/api/purchases/{purchaseId.Value:D}/documents");
    }
}
