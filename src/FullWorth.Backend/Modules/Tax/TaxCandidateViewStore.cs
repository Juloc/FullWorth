using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Tax;

public sealed class TaxCandidateViewStore(FullWorthDbContext db, TaxStore store)
{
    public async Task<List<TaxCandidateView>?> ListAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        int taxYear,
        string? status,
        CancellationToken ct)
    {
        var candidates = await store.ListCandidatesAsync(userId, fullWorthSpaceId, taxYear, status, ct);
        return candidates is null ? null : await ProjectAsync(userId, fullWorthSpaceId, candidates, ct);
    }

    public async Task<TaxCandidateView?> GetAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        Guid candidateId,
        CancellationToken ct)
    {
        var candidate = await store.GetCandidateAsync(userId, fullWorthSpaceId, candidateId, ct);
        if (candidate is null) return null;
        return (await ProjectAsync(userId, fullWorthSpaceId, [candidate], ct)).Single();
    }

    private async Task<List<TaxCandidateView>> ProjectAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        IReadOnlyCollection<TaxCandidate> candidates,
        CancellationToken ct)
    {
        if (candidates.Count == 0) return [];

        var candidateIds = candidates.Select(x => x.Id).ToArray();
        var categoryIds = candidates.Where(x => x.TaxCategoryId.HasValue).Select(x => x.TaxCategoryId!.Value).Distinct().ToArray();

        var categories = await db.TaxCategories.AsNoTracking()
            .Where(x => categoryIds.Contains(x.Id))
            .Select(x => new { x.Id, x.Code, x.Name })
            .ToDictionaryAsync(x => x.Id, ct);

        var sources = await db.TaxCandidateSources.AsNoTracking()
            .Where(x => candidateIds.Contains(x.TaxCandidateId))
            .OrderByDescending(x => x.IsPrimary)
            .ThenBy(x => x.CreatedAt)
            .ToListAsync(ct);
        var primarySources = sources.Where(x => x.IsPrimary)
            .GroupBy(x => x.TaxCandidateId)
            .ToDictionary(x => x.Key, x => x.First());
        var documentCandidateIds = sources.Where(x => x.SourceType == TaxSourceTypes.PurchaseDocument)
            .Select(x => x.TaxCandidateId)
            .ToHashSet();

        var transactionIds = primarySources.Values
            .Where(x => x.SourceType == TaxSourceTypes.Transaction)
            .Select(x => x.SourceId)
            .Distinct()
            .ToArray();
        var transactionSources = await (
            from transaction in db.Transactions.AsNoTracking()
            join account in db.Accounts.AsNoTracking() on transaction.AccountId equals account.Id
            where transactionIds.Contains(transaction.Id)
                  && account.FullWorthSpaceId == fullWorthSpaceId
                  && db.AccountOwners.Any(owner => owner.AccountId == account.Id && owner.UserId == userId)
            select new
            {
                transaction.Id,
                Title = transaction.Counterparty ?? transaction.NormalizedCounterparty ?? transaction.Description ?? "Bankbuchung",
                Date = transaction.BookingDate ?? transaction.ValueDate
            }).ToDictionaryAsync(x => x.Id, ct);

        var itemIds = primarySources.Values
            .Where(x => x.SourceType == TaxSourceTypes.PurchaseItem)
            .Select(x => x.SourceId)
            .Distinct()
            .ToArray();
        var itemSources = await (
            from item in db.PurchaseItems.AsNoTracking()
            join purchase in VisiblePurchases(userId, fullWorthSpaceId) on item.PurchaseId equals purchase.Id
            where itemIds.Contains(item.Id)
            select new
            {
                item.Id,
                Title = purchase.Merchant == "" ? item.Name : purchase.Merchant + " · " + item.Name,
                Date = purchase.PurchaseDate
            }).ToDictionaryAsync(x => x.Id, ct);

        var purchaseIds = primarySources.Values
            .Where(x => x.SourceType == TaxSourceTypes.Purchase)
            .Select(x => x.SourceId)
            .Distinct()
            .ToArray();
        var purchaseSources = await VisiblePurchases(userId, fullWorthSpaceId)
            .Where(x => purchaseIds.Contains(x.Id))
            .Select(x => new
            {
                x.Id,
                Title = x.Merchant == "" ? "Kauf" : x.Merchant,
                Date = x.PurchaseDate
            }).ToDictionaryAsync(x => x.Id, ct);

        return candidates.Select(candidate =>
        {
            categories.TryGetValue(candidate.TaxCategoryId ?? Guid.Empty, out var category);
            primarySources.TryGetValue(candidate.Id, out var source);

            var title = "Steuerhinweis";
            DateOnly? sourceDate = null;
            if (source is not null && source.SourceType == TaxSourceTypes.Transaction && transactionSources.TryGetValue(source.SourceId, out var transaction))
            {
                title = transaction.Title;
                sourceDate = transaction.Date;
            }
            else if (source is not null && source.SourceType == TaxSourceTypes.PurchaseItem && itemSources.TryGetValue(source.SourceId, out var item))
            {
                title = item.Title;
                sourceDate = item.Date;
            }
            else if (source is not null && source.SourceType == TaxSourceTypes.Purchase && purchaseSources.TryGetValue(source.SourceId, out var purchase))
            {
                title = purchase.Title;
                sourceDate = purchase.Date;
            }

            return new TaxCandidateView(
                candidate.Id,
                candidate.TaxYear,
                candidate.Status,
                candidate.TaxCategoryId,
                category?.Code,
                category?.Name,
                candidate.GrossAmount,
                candidate.EligibleAmount,
                candidate.EligiblePercentage,
                candidate.Currency,
                candidate.Confidence,
                candidate.DetectionSource,
                candidate.ReasonCode,
                candidate.Explanation,
                source?.SourceType,
                source?.SourceId,
                title,
                sourceDate,
                documentCandidateIds.Contains(candidate.Id),
                candidate.UpdatedAt);
        }).ToList();
    }

    private IQueryable<FullWorth.Backend.Modules.Purchases.Purchase> VisiblePurchases(Guid userId, Guid fullWorthSpaceId) =>
        db.Purchases.AsNoTracking().Where(purchase =>
            purchase.FullWorthSpaceId == fullWorthSpaceId &&
            db.FullWorthSpaceMembers.Any(member => member.FullWorthSpaceId == purchase.FullWorthSpaceId && member.UserId == userId) &&
            (purchase.Visibility != "private" || purchase.CreatedByUserId == userId) &&
            (!purchase.PaymentLinks.Any() || purchase.PaymentLinks.Any(link => db.Transactions.Any(transaction =>
                transaction.Id == link.TransactionId && db.Accounts.Any(account =>
                    account.Id == transaction.AccountId && account.FullWorthSpaceId == purchase.FullWorthSpaceId && account.Owners.Any(owner => owner.UserId == userId))))) &&
            (purchase.TransactionId == null || db.Transactions.Any(transaction =>
                transaction.Id == purchase.TransactionId.Value && db.Accounts.Any(account =>
                    account.Id == transaction.AccountId && account.FullWorthSpaceId == purchase.FullWorthSpaceId && account.Owners.Any(owner => owner.UserId == userId)))));
}
