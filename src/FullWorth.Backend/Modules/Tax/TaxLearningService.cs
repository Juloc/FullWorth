using System.Text;
using System.Text.RegularExpressions;
using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Tax;

public static class TaxUserMappingTypes
{
    public const string TransactionSignature = "transaction_signature";
    public const string PurchaseSignature = "purchase_signature";
    public const string PurchaseItemSignature = "purchase_item_signature";
}

public static partial class TaxLearningKey
{
    [GeneratedRegex(@"\d+(?:[.,]\d+)?", RegexOptions.CultureInvariant)]
    private static partial Regex NumberPattern();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespacePattern();

    public static string Transaction(string? counterparty, string? normalizedCounterparty, string? description) =>
        Join(normalizedCounterparty ?? counterparty, description);

    public static string Purchase(string? merchant, string? merchantRaw, string? notes) =>
        Join(merchant, merchantRaw, notes);

    public static string PurchaseItem(string? merchant, string? rawName, string? name, string? brand) =>
        Join(merchant, rawName, name, brand);

    private static string Join(params string?[] values)
    {
        var parts = values.Select(Normalize).Where(x => x.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        return string.Join('|', parts);
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var normalized = value.Normalize(NormalizationForm.FormKC).Trim().ToLowerInvariant();
        normalized = NumberPattern().Replace(normalized, "#");
        normalized = WhitespacePattern().Replace(normalized, " ");
        return normalized;
    }
}

public sealed class TaxLearningService(FullWorthDbContext db)
{
    public async Task LearnFromDecisionAsync(Guid userId, TaxCandidate candidate, CancellationToken ct)
    {
        if (candidate.Status is not (TaxCandidateStatuses.Confirmed or TaxCandidateStatuses.Rejected or TaxCandidateStatuses.Ignored))
            return;

        var ownsProfile = await db.TaxProfiles.AsNoTracking()
            .AnyAsync(x => x.Id == candidate.TaxProfileId && x.UserId == userId && x.FullWorthSpaceId == candidate.FullWorthSpaceId, ct);
        if (!ownsProfile) return;

        var primary = await db.TaxCandidateSources.AsNoTracking()
            .Where(x => x.TaxCandidateId == candidate.Id && x.IsPrimary)
            .OrderBy(x => x.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (primary is null) return;

        var identity = await ResolveIdentityAsync(primary, candidate.FullWorthSpaceId, userId, ct);
        if (identity is null || string.IsNullOrWhiteSpace(identity.Value.MatchValue)) return;

        var action = candidate.Status == TaxCandidateStatuses.Confirmed ? "suggest" : "ignore";
        if (action == "suggest" && !candidate.TaxCategoryId.HasValue) return;

        var existing = await db.TaxUserMappings
            .Where(x => x.FullWorthSpaceId == candidate.FullWorthSpaceId
                        && x.TaxProfileId == candidate.TaxProfileId
                        && x.MatchType == identity.Value.MatchType
                        && x.MatchValue == identity.Value.MatchValue)
            .OrderByDescending(x => x.UpdatedAt)
            .FirstOrDefaultAsync(ct);

        if (existing is null)
        {
            db.TaxUserMappings.Add(new TaxUserMapping
            {
                FullWorthSpaceId = candidate.FullWorthSpaceId,
                TaxProfileId = candidate.TaxProfileId,
                MatchType = identity.Value.MatchType,
                MatchValue = identity.Value.MatchValue,
                TaxCategoryId = action == "suggest" ? candidate.TaxCategoryId : null,
                EligiblePercentage = candidate.EligiblePercentage,
                Action = action,
                CreatedFromCandidateId = candidate.Id,
                Active = true
            });
        }
        else
        {
            existing.TaxCategoryId = action == "suggest" ? candidate.TaxCategoryId : null;
            existing.EligiblePercentage = candidate.EligiblePercentage;
            existing.Action = action;
            existing.CreatedFromCandidateId = candidate.Id;
            existing.Active = true;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
        }

        if (db.ChangeTracker.HasChanges()) await db.SaveChangesAsync(ct);
    }

    private async Task<(string MatchType, string MatchValue)?> ResolveIdentityAsync(
        TaxCandidateSource source,
        Guid fullWorthSpaceId,
        Guid userId,
        CancellationToken ct)
    {
        if (source.SourceType == TaxSourceTypes.Transaction)
        {
            var transaction = await (
                from tx in db.Transactions.AsNoTracking()
                join account in db.Accounts.AsNoTracking() on tx.AccountId equals account.Id
                where tx.Id == source.SourceId
                      && account.FullWorthSpaceId == fullWorthSpaceId
                      && db.AccountOwners.Any(owner => owner.AccountId == account.Id && owner.UserId == userId)
                select new { tx.Counterparty, tx.NormalizedCounterparty, tx.Description }).SingleOrDefaultAsync(ct);
            return transaction is null
                ? null
                : (TaxUserMappingTypes.TransactionSignature,
                    TaxLearningKey.Transaction(transaction.Counterparty, transaction.NormalizedCounterparty, transaction.Description));
        }

        if (source.SourceType == TaxSourceTypes.PurchaseItem)
        {
            var item = await (
                from purchaseItem in db.PurchaseItems.AsNoTracking()
                join purchase in VisiblePurchases(userId, fullWorthSpaceId) on purchaseItem.PurchaseId equals purchase.Id
                where purchaseItem.Id == source.SourceId
                select new { purchase.Merchant, purchaseItem.RawName, purchaseItem.Name, purchaseItem.Brand }).SingleOrDefaultAsync(ct);
            return item is null
                ? null
                : (TaxUserMappingTypes.PurchaseItemSignature,
                    TaxLearningKey.PurchaseItem(item.Merchant, item.RawName, item.Name, item.Brand));
        }

        if (source.SourceType == TaxSourceTypes.Purchase)
        {
            var purchase = await VisiblePurchases(userId, fullWorthSpaceId)
                .Where(x => x.Id == source.SourceId)
                .Select(x => new { x.Merchant, x.MerchantRaw, x.Notes })
                .SingleOrDefaultAsync(ct);
            return purchase is null
                ? null
                : (TaxUserMappingTypes.PurchaseSignature,
                    TaxLearningKey.Purchase(purchase.Merchant, purchase.MerchantRaw, purchase.Notes));
        }

        return null;
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
