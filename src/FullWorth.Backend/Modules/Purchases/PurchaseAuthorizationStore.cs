using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Accounts;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Purchases;

public enum PurchaseAccessLevel
{
    None,
    Read,
    Write
}

public enum PurchaseMutationResult
{
    Success,
    NotFound,
    Forbidden,
    Invalid
}

public sealed record PurchaseItemView(
    Guid Id,
    Guid? CategoryId,
    string Name,
    string? Brand,
    string? Sku,
    string? Asin,
    decimal Quantity,
    decimal? UnitPrice,
    decimal TotalPrice,
    string Currency,
    string CategorizationSource,
    string? Notes,
    Guid? ProductId = null,
    string? RawName = null,
    string? Barcode = null,
    string? QuantityUnit = null,
    decimal? PackageQuantity = null,
    string? PackageUnit = null,
    decimal? PackageCount = null,
    decimal? BaseUnitPrice = null,
    decimal? DiscountAmount = null,
    decimal? DepositAmount = null,
    string? LineType = null,
    int SortOrder = 0,
    DateOnly? ReturnDeadline = null,
    DateOnly? WarrantyEnd = null,
    string? SerialNumber = null,
    decimal? OriginalUnitPrice = null,
    string? DiscountLabel = null);

public sealed record PurchaseView(
    Guid Id,
    Guid FullWorthSpaceId,
    Guid? TransactionId,
    string Source,
    string Merchant,
    string? ExternalOrderId,
    DateOnly? PurchaseDate,
    decimal TotalAmount,
    string Currency,
    string Status,
    decimal? MatchConfidence,
    string? SourceReference,
    string? Notes,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    bool HasReceipt,
    IReadOnlyList<PurchaseItemView> Items,
    string ReviewState = "needs_review",
    string Visibility = "space",
    bool IsBookmarked = false,
    Guid? CreatedByUserId = null,
    int PaymentCount = 0,
    int DocumentCount = 0);

public sealed record PurchaseMutationOutcome(PurchaseMutationResult Result, PurchaseView? Purchase = null, string? Error = null);
public sealed record PurchaseCandidateOutcome(PurchaseMutationResult Result, IReadOnlyList<object>? Candidates = null);

/// <summary>
/// Backwards-compatible authorization layer for the original /api/purchases routes. Newer article
/// endpoints use PurchaseWorkspaceService, but these routes remain in use by receipt scan, Amazon and
/// the existing web UI. The security and reconciliation invariants therefore must be identical.
/// </summary>
public sealed class PurchaseAuthorizationStore(FullWorthDbContext db)
{
    public Task<List<PurchaseView>> ListForUserAsync(
        Guid userId,
        Guid? fullWorthSpaceId,
        Guid? transactionId,
        string? source,
        DateOnly? from,
        DateOnly? to,
        CancellationToken ct)
    {
        var query = VisiblePurchases(userId, fullWorthSpaceId);
        if (transactionId.HasValue)
            query = query.Where(x => x.TransactionId == transactionId.Value || x.PaymentLinks.Any(link => link.TransactionId == transactionId.Value));
        if (!string.IsNullOrWhiteSpace(source)) query = query.Where(x => x.Source == source);
        if (from.HasValue) query = query.Where(x => x.PurchaseDate >= from.Value);
        if (to.HasValue) query = query.Where(x => x.PurchaseDate <= to.Value);

        return Project(query.OrderByDescending(x => x.PurchaseDate).ThenByDescending(x => x.CreatedAt)).ToListAsync(ct);
    }

    public Task<PurchaseView?> GetForUserAsync(Guid userId, Guid fullWorthSpaceId, Guid purchaseId, CancellationToken ct) =>
        Project(VisiblePurchases(userId, fullWorthSpaceId).Where(x => x.Id == purchaseId)).SingleOrDefaultAsync(ct);

    public async Task<PurchaseAccessLevel> GetAccessAsync(Guid userId, Guid fullWorthSpaceId, Guid purchaseId, CancellationToken ct)
    {
        if (await WritablePurchases(userId, fullWorthSpaceId).AnyAsync(x => x.Id == purchaseId, ct)) return PurchaseAccessLevel.Write;
        if (await VisiblePurchases(userId, fullWorthSpaceId).AnyAsync(x => x.Id == purchaseId, ct)) return PurchaseAccessLevel.Read;
        return PurchaseAccessLevel.None;
    }

    public async Task<PurchaseAccessLevel> GetTransactionAccessAsync(Guid userId, Guid fullWorthSpaceId, Guid transactionId, CancellationToken ct)
    {
        var ownership = await db.Transactions.AsNoTracking()
            .Where(transaction => transaction.Id == transactionId)
            .Join(db.Accounts.AsNoTracking(), transaction => transaction.AccountId, account => account.Id, (_, account) => account)
            .Where(account => account.FullWorthSpaceId == fullWorthSpaceId &&
                              db.FullWorthSpaceMembers.Any(member => member.FullWorthSpaceId == fullWorthSpaceId && member.UserId == userId))
            .Join(db.AccountOwners.AsNoTracking().Where(owner => owner.UserId == userId),
                account => account.Id,
                owner => owner.AccountId,
                (_, owner) => owner.OwnershipType)
            .SingleOrDefaultAsync(ct);

        return ownership switch
        {
            AccountOwnershipTypes.Owner => PurchaseAccessLevel.Write,
            AccountOwnershipTypes.Viewer => PurchaseAccessLevel.Read,
            _ => PurchaseAccessLevel.None
        };
    }

    public async Task<PurchaseMutationOutcome> CreateForUserAsync(Guid userId, Guid fullWorthSpaceId, PurchaseWrite request, CancellationToken ct)
    {
        if (!await IsFullWorthSpaceMemberAsync(userId, fullWorthSpaceId, ct)) return new(PurchaseMutationResult.NotFound);

        var transactionAccess = await ValidateRequestedTransactionAsync(userId, fullWorthSpaceId, request.TransactionId, ct);
        if (transactionAccess != PurchaseMutationResult.Success) return new(transactionAccess);

        try
        {
            await ValidatePurchaseWriteAsync(fullWorthSpaceId, request, ct);
            var entity = new Purchase { FullWorthSpaceId = fullWorthSpaceId, CreatedByUserId = userId };
            ApplyWrite(entity, request);
            db.Purchases.Add(entity);
            await db.SaveChangesAsync(ct);
            return new(PurchaseMutationResult.Success, await GetForUserAsync(userId, fullWorthSpaceId, entity.Id, ct));
        }
        catch (ArgumentException exception)
        {
            return new(PurchaseMutationResult.Invalid, Error: exception.Message);
        }
    }

    public async Task<PurchaseMutationOutcome> UpdateForUserAsync(Guid userId, Guid fullWorthSpaceId, Guid purchaseId, PurchaseWrite request, CancellationToken ct)
    {
        var access = await GetAccessAsync(userId, fullWorthSpaceId, purchaseId, ct);
        if (access == PurchaseAccessLevel.None) return new(PurchaseMutationResult.NotFound);
        if (access != PurchaseAccessLevel.Write) return new(PurchaseMutationResult.Forbidden);

        var transactionAccess = await ValidateRequestedTransactionAsync(userId, fullWorthSpaceId, request.TransactionId, ct);
        if (transactionAccess != PurchaseMutationResult.Success) return new(transactionAccess);

        try
        {
            await ValidatePurchaseWriteAsync(fullWorthSpaceId, request, ct);
            var entity = await WritablePurchases(userId, fullWorthSpaceId).SingleOrDefaultAsync(x => x.Id == purchaseId, ct);
            if (entity is null) return new(PurchaseMutationResult.NotFound);
            ApplyWrite(entity, request);
            await db.SaveChangesAsync(ct);
            return new(PurchaseMutationResult.Success, await GetForUserAsync(userId, fullWorthSpaceId, purchaseId, ct));
        }
        catch (ArgumentException exception)
        {
            return new(PurchaseMutationResult.Invalid, Error: exception.Message);
        }
    }

    public async Task<PurchaseMutationOutcome> ReplaceItemsForUserAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        Guid purchaseId,
        IReadOnlyList<PurchaseItemWrite> items,
        CancellationToken ct)
    {
        var access = await GetAccessAsync(userId, fullWorthSpaceId, purchaseId, ct);
        if (access == PurchaseAccessLevel.None) return new(PurchaseMutationResult.NotFound);
        if (access != PurchaseAccessLevel.Write) return new(PurchaseMutationResult.Forbidden);

        var purchase = await WritablePurchases(userId, fullWorthSpaceId).Include(x => x.Items).SingleOrDefaultAsync(x => x.Id == purchaseId, ct);
        if (purchase is null) return new(PurchaseMutationResult.NotFound);

        var categoryIds = items.Where(x => x.CategoryId.HasValue).Select(x => x.CategoryId!.Value).Distinct().ToArray();
        if (categoryIds.Length > 0)
        {
            var validCount = await db.Categories.AsNoTracking().CountAsync(x => x.FullWorthSpaceId == fullWorthSpaceId && categoryIds.Contains(x.Id), ct);
            if (validCount != categoryIds.Length) return new(PurchaseMutationResult.NotFound);
        }
        var productIds = items.Where(x => x.ProductId.HasValue).Select(x => x.ProductId!.Value).Distinct().ToArray();
        if (productIds.Length > 0)
        {
            var validCount = await db.Products.AsNoTracking().CountAsync(x => x.FullWorthSpaceId == fullWorthSpaceId && productIds.Contains(x.Id), ct);
            if (validCount != productIds.Length) return new(PurchaseMutationResult.NotFound);
        }

        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.Name)) return new(PurchaseMutationResult.Invalid, Error: "Purchase item name is required.");
            if (item.Quantity <= 0) return new(PurchaseMutationResult.Invalid, Error: "Purchase item quantity must be greater than zero.");
        }

        var newItems = items.Select((item, index) => ToEntity(item, purchase.Currency, index)).ToList();
        await ApplyItemRulesAsync(fullWorthSpaceId, newItems, ct);

        var existingOrdered = purchase.Items.OrderBy(x => x.SortOrder).ThenBy(x => x.CreatedAt).ThenBy(x => x.Id).ToList();
        if (EquivalentItems(existingOrdered, newItems))
            return new(PurchaseMutationResult.Success, await GetForUserAsync(userId, fullWorthSpaceId, purchaseId, ct));

        // The source breakdown changed. Generated purchase allocations represent the old amounts and must
        // be removed, while unrelated/manual allocations merely lose their now-invalid item pointer.
        var oldIds = purchase.Items.Select(x => x.Id).ToArray();
        if (oldIds.Length > 0)
        {
            var allocations = await db.TransactionAllocations.Where(x => x.PurchaseItemId.HasValue && oldIds.Contains(x.PurchaseItemId.Value)).ToListAsync(ct);
            var allocationIds = allocations.Select(x => x.Id).ToArray();
            var generatedIds = allocationIds.Length == 0
                ? []
                : await db.Set<PurchaseAllocationLink>().AsNoTracking()
                    .Where(x => x.PurchaseId == purchaseId && allocationIds.Contains(x.TransactionAllocationId))
                    .Select(x => x.TransactionAllocationId)
                    .ToListAsync(ct);
            var generatedSet = generatedIds.ToHashSet();
            var generated = allocations.Where(x => generatedSet.Contains(x.Id)).ToList();
            if (generated.Count > 0) db.TransactionAllocations.RemoveRange(generated);
            foreach (var allocation in allocations.Where(x => !generatedSet.Contains(x.Id))) allocation.PurchaseItemId = null;
        }

        db.PurchaseItems.RemoveRange(purchase.Items);
        db.PurchaseItems.AddRange(newItems.Select(x => { x.PurchaseId = purchase.Id; return x; }));
        purchase.Status = "review";
        purchase.ReviewState = "needs_review";
        purchase.UpdatedAt = DateTimeOffset.UtcNow;
        var accepted = await db.Set<PurchaseDifferenceAcceptance>().Where(x => x.PurchaseId == purchaseId).ToListAsync(ct);
        if (accepted.Count > 0) db.RemoveRange(accepted);
        await db.SaveChangesAsync(ct);
        return new(PurchaseMutationResult.Success, await GetForUserAsync(userId, fullWorthSpaceId, purchaseId, ct));
    }

    public async Task<PurchaseCandidateOutcome> MatchCandidatesForUserAsync(Guid userId, Guid fullWorthSpaceId, Guid purchaseId, CancellationToken ct)
    {
        var access = await GetAccessAsync(userId, fullWorthSpaceId, purchaseId, ct);
        if (access == PurchaseAccessLevel.None) return new(PurchaseMutationResult.NotFound);
        if (access != PurchaseAccessLevel.Write) return new(PurchaseMutationResult.Forbidden);

        var purchase = await WritablePurchases(userId, fullWorthSpaceId).AsNoTracking().SingleOrDefaultAsync(x => x.Id == purchaseId, ct);
        if (purchase is null) return new(PurchaseMutationResult.NotFound);
        if (purchase.TransactionId.HasValue || await db.PurchasePaymentLinks.AnyAsync(x => x.PurchaseId == purchaseId, ct))
            return new(PurchaseMutationResult.Success, []);

        var date = purchase.PurchaseDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var candidates = await OwnedTransactions(userId, fullWorthSpaceId)
            .Where(transaction => transaction.Amount < 0 &&
                                  transaction.BookingDate >= date.AddDays(-4) &&
                                  transaction.BookingDate <= date.AddDays(4) &&
                                  transaction.Currency == purchase.Currency)
            .Select(transaction => new { transaction.Id, transaction.BookingDate, transaction.Amount, transaction.Counterparty, transaction.Description })
            .ToListAsync(ct);

        var scored = candidates.Select(transaction =>
        {
            var amountDelta = Math.Abs(Math.Abs(transaction.Amount) - Math.Abs(purchase.TotalAmount));
            var amountScore = purchase.TotalAmount == 0 ? 0m : Math.Max(0m, 1m - amountDelta / Math.Max(1m, Math.Abs(purchase.TotalAmount)));
            var merchantScore = !string.IsNullOrWhiteSpace(transaction.Counterparty) && !string.IsNullOrWhiteSpace(purchase.Merchant) &&
                                (transaction.Counterparty.Contains(purchase.Merchant, StringComparison.OrdinalIgnoreCase) || purchase.Merchant.Contains(transaction.Counterparty, StringComparison.OrdinalIgnoreCase)) ? 1m : 0m;
            var dateScore = transaction.BookingDate.HasValue ? Math.Max(0m, 1m - Math.Abs(transaction.BookingDate.Value.DayNumber - date.DayNumber) / 5m) : 0m;
            var confidence = Math.Clamp(amountScore * .65m + merchantScore * .20m + dateScore * .15m, 0m, 1m);
            return (object)new { transaction.Id, transaction.BookingDate, transaction.Amount, transaction.Counterparty, transaction.Description, confidence };
        }).OrderByDescending(item => (decimal)item.GetType().GetProperty("confidence")!.GetValue(item)!).Take(10).ToList();

        return new(PurchaseMutationResult.Success, scored);
    }

    public async Task<PurchaseMutationResult> LinkForUserAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        Guid purchaseId,
        Guid transactionId,
        decimal? confidence,
        CancellationToken ct)
    {
        var purchaseAccess = await GetAccessAsync(userId, fullWorthSpaceId, purchaseId, ct);
        if (purchaseAccess == PurchaseAccessLevel.None) return PurchaseMutationResult.NotFound;
        if (purchaseAccess != PurchaseAccessLevel.Write) return PurchaseMutationResult.Forbidden;

        var transactionAccess = await GetTransactionAccessAsync(userId, fullWorthSpaceId, transactionId, ct);
        if (transactionAccess == PurchaseAccessLevel.None) return PurchaseMutationResult.NotFound;
        if (transactionAccess != PurchaseAccessLevel.Write) return PurchaseMutationResult.Forbidden;

        var purchase = await WritablePurchases(userId, fullWorthSpaceId).Include(x => x.PaymentLinks).SingleOrDefaultAsync(x => x.Id == purchaseId, ct);
        if (purchase is null) return PurchaseMutationResult.NotFound;
        if (purchase.PaymentLinks.Count > 0 && purchase.PaymentLinks.All(x => x.TransactionId != transactionId))
            return PurchaseMutationResult.Invalid;

        var link = purchase.PaymentLinks.SingleOrDefault(x => x.TransactionId == transactionId);
        if (link is null)
        {
            link = new PurchasePaymentLink
            {
                FullWorthSpaceId = fullWorthSpaceId,
                PurchaseId = purchase.Id,
                TransactionId = transactionId,
                Amount = Math.Abs(purchase.TotalAmount),
                Currency = purchase.Currency,
                LinkSource = confidence.HasValue ? "match" : "manual",
                Confidence = confidence,
                CreatedByUserId = userId
            };
            db.PurchasePaymentLinks.Add(link);
        }
        else
        {
            link.Amount = Math.Abs(purchase.TotalAmount);
            link.Currency = purchase.Currency;
            link.Confidence = confidence;
            link.UpdatedAt = DateTimeOffset.UtcNow;
        }

        // Compatibility mirror only. Linking a payment must not bypass the explicit review/reconcile
        // confirmation gate introduced by PurchaseLifecycleService.
        purchase.TransactionId = transactionId;
        purchase.MatchConfidence = confidence;
        if (purchase.Status == "confirmed") purchase.ReviewState = "confirmed";
        else
        {
            purchase.Status = "review";
            purchase.ReviewState = "needs_review";
        }
        purchase.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return PurchaseMutationResult.Success;
    }

    public async Task<object?> GetReconciliationForUserAsync(Guid userId, Guid fullWorthSpaceId, Guid purchaseId, CancellationToken ct)
    {
        var purchase = await VisiblePurchases(userId, fullWorthSpaceId)
            .Include(x => x.Items)
            .Include(x => x.PaymentLinks)
            .SingleOrDefaultAsync(x => x.Id == purchaseId, ct);
        if (purchase is null) return null;

        // Old records may have only the single legacy FK. Use it only when no modern link rows exist.
        var paymentLinks = purchase.PaymentLinks.ToList();
        decimal? legacyTransactionAmount = null;
        if (paymentLinks.Count == 0 && purchase.TransactionId.HasValue)
        {
            legacyTransactionAmount = await AccessibleTransactions(userId, fullWorthSpaceId)
                .Where(x => x.Id == purchase.TransactionId.Value)
                .Select(x => (decimal?)x.Amount)
                .SingleOrDefaultAsync(ct);
        }

        var rec = PurchaseArticleCalculator.Reconcile(purchase.TotalAmount, purchase.Items, paymentLinks, purchase.Currency);
        var transactionAmount = paymentLinks.Count > 0 ? -rec.LinkedPaymentTotal : legacyTransactionAmount;
        var transactionDifference = paymentLinks.Count > 0
            ? -rec.PaymentDifference
            : legacyTransactionAmount.HasValue ? Math.Abs(legacyTransactionAmount.Value) - Math.Abs(purchase.TotalAmount) : (decimal?)null;
        var transactionReconciled = paymentLinks.Count > 0
            ? rec.PaymentsReconciled
            : !legacyTransactionAmount.HasValue || Math.Abs(transactionDifference!.Value) <= rec.Tolerance;

        return new
        {
            purchase.Id,
            purchase.TransactionId,
            purchase.Currency,
            purchaseTotal = rec.PurchaseTotal,
            itemTotal = rec.ItemTotal,
            itemDifference = rec.ItemDifference,
            linkedPaymentTotal = rec.LinkedPaymentTotal,
            paymentDifference = rec.PaymentDifference,
            transactionAmount,
            transactionDifference,
            itemsReconciled = rec.ItemsReconciled,
            transactionReconciled,
            fullyReconciled = rec.ItemsReconciled && transactionReconciled,
            rec.Tolerance
        };
    }

    public async Task<string?> GetReceiptPathForUserAsync(Guid userId, Guid fullWorthSpaceId, Guid purchaseId, CancellationToken ct)
    {
        var purchase = await VisiblePurchases(userId, fullWorthSpaceId)
            .Where(x => x.Id == purchaseId)
            .Select(x => new
            {
                x.ReceiptImagePath,
                DocumentPath = x.Documents.OrderBy(d => d.CreatedAt).Select(d => d.StoragePath).FirstOrDefault()
            })
            .SingleOrDefaultAsync(ct);
        return purchase?.ReceiptImagePath ?? purchase?.DocumentPath;
    }

    public Task<bool> IsFullWorthSpaceMemberAsync(Guid userId, Guid fullWorthSpaceId, CancellationToken ct) =>
        db.FullWorthSpaceMembers.AsNoTracking().AnyAsync(member => member.FullWorthSpaceId == fullWorthSpaceId && member.UserId == userId);

    private IQueryable<Purchase> VisiblePurchases(Guid userId, Guid? fullWorthSpaceId) =>
        db.Purchases.AsNoTracking().Where(purchase =>
            (!fullWorthSpaceId.HasValue || purchase.FullWorthSpaceId == fullWorthSpaceId.Value) &&
            db.FullWorthSpaceMembers.Any(member => member.FullWorthSpaceId == purchase.FullWorthSpaceId && member.UserId == userId) &&
            (purchase.Visibility != "private" || purchase.CreatedByUserId == userId) &&
            (!purchase.PaymentLinks.Any() || purchase.PaymentLinks.Any(link => db.Transactions.Any(transaction =>
                transaction.Id == link.TransactionId && db.Accounts.Any(account =>
                    account.Id == transaction.AccountId && account.FullWorthSpaceId == purchase.FullWorthSpaceId && account.Owners.Any(owner => owner.UserId == userId))))) &&
            (purchase.TransactionId == null || db.Transactions.Any(transaction =>
                transaction.Id == purchase.TransactionId.Value && db.Accounts.Any(account =>
                    account.Id == transaction.AccountId && account.FullWorthSpaceId == purchase.FullWorthSpaceId && account.Owners.Any(owner => owner.UserId == userId)))));

    private IQueryable<Purchase> WritablePurchases(Guid userId, Guid fullWorthSpaceId) =>
        db.Purchases.Where(purchase =>
            purchase.FullWorthSpaceId == fullWorthSpaceId &&
            db.FullWorthSpaceMembers.Any(member => member.FullWorthSpaceId == fullWorthSpaceId && member.UserId == userId) &&
            (purchase.Visibility != "private" || purchase.CreatedByUserId == userId) &&
            (!purchase.PaymentLinks.Any() || purchase.PaymentLinks.All(link => db.Transactions.Any(transaction =>
                transaction.Id == link.TransactionId && db.Accounts.Any(account =>
                    account.Id == transaction.AccountId && account.FullWorthSpaceId == fullWorthSpaceId && account.Owners.Any(owner =>
                        owner.UserId == userId && owner.OwnershipType == AccountOwnershipTypes.Owner))))) &&
            (purchase.TransactionId == null || db.Transactions.Any(transaction =>
                transaction.Id == purchase.TransactionId.Value && db.Accounts.Any(account =>
                    account.Id == transaction.AccountId && account.FullWorthSpaceId == fullWorthSpaceId && account.Owners.Any(owner =>
                        owner.UserId == userId && owner.OwnershipType == AccountOwnershipTypes.Owner)))));

    private IQueryable<FullWorth.Backend.Modules.Transactions.FinanceTransaction> AccessibleTransactions(Guid userId, Guid fullWorthSpaceId) =>
        db.Transactions.AsNoTracking().Where(transaction => db.Accounts.Any(account =>
            account.Id == transaction.AccountId && account.FullWorthSpaceId == fullWorthSpaceId &&
            db.FullWorthSpaceMembers.Any(member => member.FullWorthSpaceId == fullWorthSpaceId && member.UserId == userId) &&
            account.Owners.Any(owner => owner.UserId == userId)));

    private IQueryable<FullWorth.Backend.Modules.Transactions.FinanceTransaction> OwnedTransactions(Guid userId, Guid fullWorthSpaceId) =>
        db.Transactions.AsNoTracking().Where(transaction => db.Accounts.Any(account =>
            account.Id == transaction.AccountId && account.FullWorthSpaceId == fullWorthSpaceId &&
            db.FullWorthSpaceMembers.Any(member => member.FullWorthSpaceId == fullWorthSpaceId && member.UserId == userId) &&
            account.Owners.Any(owner => owner.UserId == userId && owner.OwnershipType == AccountOwnershipTypes.Owner)));

    private IQueryable<PurchaseView> Project(IQueryable<Purchase> purchases) =>
        purchases.Select(purchase => new PurchaseView(
            purchase.Id,
            purchase.FullWorthSpaceId,
            purchase.TransactionId,
            purchase.Source,
            purchase.Merchant,
            purchase.ExternalOrderId,
            purchase.PurchaseDate,
            purchase.TotalAmount,
            purchase.Currency,
            purchase.Status,
            purchase.MatchConfidence,
            purchase.SourceReference,
            purchase.Notes,
            purchase.CreatedAt,
            purchase.UpdatedAt,
            purchase.ReceiptImagePath != null || purchase.Documents.Any(),
            purchase.Items.OrderBy(item => item.SortOrder).ThenBy(item => item.CreatedAt).Select(item => new PurchaseItemView(
                item.Id,
                item.CategoryId,
                item.Name,
                item.Brand,
                item.Sku,
                item.Asin,
                item.Quantity,
                item.UnitPrice,
                item.TotalPrice,
                item.Currency,
                item.CategorizationSource,
                item.Notes,
                item.ProductId,
                item.RawName,
                item.Barcode,
                item.QuantityUnit,
                item.PackageQuantity,
                item.PackageUnit,
                item.PackageCount,
                item.BaseUnitPrice,
                item.DiscountAmount,
                item.DepositAmount,
                item.LineType,
                item.SortOrder,
                item.ReturnDeadline,
                item.WarrantyEnd,
                item.SerialNumber,
                item.OriginalUnitPrice,
                item.DiscountLabel)).ToList(),
            purchase.ReviewState,
            purchase.Visibility,
            purchase.IsBookmarked,
            purchase.CreatedByUserId,
            purchase.PaymentLinks.Count(),
            purchase.Documents.Count()));

    private async Task<PurchaseMutationResult> ValidateRequestedTransactionAsync(Guid userId, Guid fullWorthSpaceId, Guid? transactionId, CancellationToken ct)
    {
        if (!transactionId.HasValue) return PurchaseMutationResult.Success;
        var access = await GetTransactionAccessAsync(userId, fullWorthSpaceId, transactionId.Value, ct);
        return access switch
        {
            PurchaseAccessLevel.Write => PurchaseMutationResult.Success,
            PurchaseAccessLevel.Read => PurchaseMutationResult.Forbidden,
            _ => PurchaseMutationResult.NotFound
        };
    }

    private async Task ApplyItemRulesAsync(Guid fullWorthSpaceId, IEnumerable<PurchaseItem> items, CancellationToken ct)
    {
        var rules = await db.CategorizationRules.AsNoTracking()
            .Where(x => x.FullWorthSpaceId == fullWorthSpaceId && x.IsEnabled && x.Target == "item")
            .OrderBy(x => x.Priority).ThenBy(x => x.Id)
            .ToListAsync(ct);

        foreach (var item in items)
        {
            if (item.CategorizationSource == "manual") continue;
            foreach (var rule in rules)
            {
                var text = rule.MatchField switch
                {
                    "brand" => item.Brand,
                    "sku" => item.Sku,
                    "asin" => item.Asin,
                    "barcode" => item.Barcode,
                    "raw_name" => item.RawName,
                    _ => item.Name
                };
                if (!Matches(text, rule.Pattern, rule.MatchMode)) continue;
                var abs = Math.Abs(item.TotalPrice);
                if (rule.MinAmount.HasValue && abs < rule.MinAmount.Value) continue;
                if (rule.MaxAmount.HasValue && abs > rule.MaxAmount.Value) continue;
                item.CategoryId = rule.CategoryId;
                item.CategorizationSource = "rule";
                if (rule.StopProcessing) break;
            }
        }
    }

    private async Task ValidatePurchaseWriteAsync(Guid fullWorthSpaceId, PurchaseWrite request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Merchant)) throw new ArgumentException("Merchant is required.");
        if (string.IsNullOrWhiteSpace(request.Currency)) throw new ArgumentException("Currency is required.");
        var currency = request.Currency.Trim().ToUpperInvariant();
        if (currency.Length != 3 || currency.Any(character => character is < 'A' or > 'Z')) throw new ArgumentException("Currency must be a three-letter code.");
        if (request.MerchantId.HasValue && !await db.Merchants.AsNoTracking().AnyAsync(x => x.Id == request.MerchantId && x.FullWorthSpaceId == fullWorthSpaceId, ct))
            throw new ArgumentException("Merchant must belong to the FullWorth Space.");
    }

    private static void ApplyWrite(Purchase entity, PurchaseWrite request)
    {
        entity.TransactionId = request.TransactionId;
        entity.Source = string.IsNullOrWhiteSpace(request.Source) ? "receipt" : request.Source.Trim().ToLowerInvariant();
        entity.Merchant = request.Merchant.Trim();
        entity.MerchantId = request.MerchantId;
        entity.MerchantRaw = Clean(request.MerchantRaw);
        entity.ExternalOrderId = Clean(request.ExternalOrderId);
        entity.PurchaseDate = request.PurchaseDate;
        entity.PurchaseTime = request.PurchaseTime;
        entity.TimeZone = Clean(request.TimeZone);
        entity.SubtotalAmount = request.SubtotalAmount;
        entity.DiscountAmount = request.DiscountAmount;
        entity.DepositAmount = request.DepositAmount;
        entity.TaxAmount = request.TaxAmount;
        entity.TipAmount = request.TipAmount;
        entity.ShippingAmount = request.ShippingAmount;
        entity.FeeAmount = request.FeeAmount;
        entity.TotalAmount = request.TotalAmount;
        entity.Currency = request.Currency.Trim().ToUpperInvariant();
        entity.Status = string.IsNullOrWhiteSpace(request.Status) ? "review" : request.Status.Trim().ToLowerInvariant();
        entity.ReviewState = string.IsNullOrWhiteSpace(request.ReviewState)
            ? (entity.Status == "confirmed" ? "confirmed" : "needs_review")
            : request.ReviewState.Trim().ToLowerInvariant();
        entity.ReceiptNumber = Clean(request.ReceiptNumber);
        entity.InvoiceNumber = Clean(request.InvoiceNumber);
        entity.PaymentMethodText = Clean(request.PaymentMethodText);
        entity.SourceReference = Clean(request.SourceReference);
        entity.Notes = Clean(request.Notes);
        entity.IsBookmarked = request.IsBookmarked ?? entity.IsBookmarked;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private static PurchaseItem ToEntity(PurchaseItemWrite item, string purchaseCurrency, int fallbackSort) => new()
    {
        ProductId = item.ProductId,
        CategoryId = item.CategoryId,
        RawName = string.IsNullOrWhiteSpace(item.RawName) ? item.Name.Trim() : item.RawName.Trim(),
        Name = item.Name.Trim(),
        Brand = Clean(item.Brand),
        Sku = Clean(item.Sku),
        Barcode = Clean(item.Barcode),
        Asin = Clean(item.Asin),
        Quantity = item.Quantity,
        QuantityUnit = PurchaseArticleCalculator.NormalizeUnit(item.QuantityUnit),
        PackageQuantity = item.PackageQuantity,
        PackageUnit = Clean(item.PackageUnit)?.ToLowerInvariant(),
        PackageCount = item.PackageCount,
        UnitPrice = item.UnitPrice,
        OriginalUnitPrice = item.OriginalUnitPrice,
        BaseUnitPrice = item.BaseUnitPrice ?? PurchaseArticleCalculator.BaseUnitPrice(item.UnitPrice, item.Quantity, item.QuantityUnit, item.PackageCount, item.PackageQuantity, item.PackageUnit, item.Currency),
        TotalPrice = PurchaseArticleCalculator.RoundMoney(item.TotalPrice, string.IsNullOrWhiteSpace(item.Currency) ? purchaseCurrency : item.Currency),
        DiscountAmount = item.DiscountAmount,
        DiscountLabel = Clean(item.DiscountLabel),
        DepositAmount = item.DepositAmount,
        TaxRate = item.TaxRate,
        TaxAmount = item.TaxAmount,
        Currency = string.IsNullOrWhiteSpace(item.Currency) ? purchaseCurrency : item.Currency.Trim().ToUpperInvariant(),
        LineType = string.IsNullOrWhiteSpace(item.LineType) ? "product" : item.LineType.Trim().ToLowerInvariant(),
        CategorizationSource = item.CategoryId.HasValue ? "manual" : "none",
        ExtractionConfidence = item.ExtractionConfidence,
        IsManuallyCorrected = item.IsManuallyCorrected,
        TotalPriceOverridden = item.TotalPriceOverridden,
        Notes = Clean(item.Notes),
        SortOrder = item.SortOrder ?? fallbackSort,
        ReturnDeadline = item.ReturnDeadline,
        WarrantyEnd = item.WarrantyEnd,
        SerialNumber = Clean(item.SerialNumber)
    };

    private static bool EquivalentItems(IReadOnlyList<PurchaseItem> current, IReadOnlyList<PurchaseItem> desired)
    {
        if (current.Count != desired.Count) return false;
        for (var index = 0; index < current.Count; index++)
        {
            var a = current[index];
            var b = desired[index];
            if (a.ProductId != b.ProductId || a.CategoryId != b.CategoryId ||
                !Same(a.RawName, b.RawName) || !Same(a.Name, b.Name) || !Same(a.Brand, b.Brand) || !Same(a.Sku, b.Sku) ||
                !Same(a.Barcode, b.Barcode) || !Same(a.Asin, b.Asin) || a.Quantity != b.Quantity || !Same(a.QuantityUnit, b.QuantityUnit) ||
                a.PackageQuantity != b.PackageQuantity || !Same(a.PackageUnit, b.PackageUnit) || a.PackageCount != b.PackageCount ||
                a.UnitPrice != b.UnitPrice || a.OriginalUnitPrice != b.OriginalUnitPrice || a.BaseUnitPrice != b.BaseUnitPrice ||
                a.TotalPrice != b.TotalPrice || a.DiscountAmount != b.DiscountAmount || !Same(a.DiscountLabel, b.DiscountLabel) ||
                a.DepositAmount != b.DepositAmount || a.TaxRate != b.TaxRate || a.TaxAmount != b.TaxAmount ||
                !Same(a.Currency, b.Currency) || !Same(a.LineType, b.LineType) || !Same(a.CategorizationSource, b.CategorizationSource) ||
                a.ExtractionConfidence != b.ExtractionConfidence || a.IsManuallyCorrected != b.IsManuallyCorrected ||
                a.TotalPriceOverridden != b.TotalPriceOverridden || !Same(a.Notes, b.Notes) || a.SortOrder != b.SortOrder ||
                a.ReturnDeadline != b.ReturnDeadline || a.WarrantyEnd != b.WarrantyEnd || !Same(a.SerialNumber, b.SerialNumber))
                return false;
        }
        return true;
    }

    private static bool Same(string? a, string? b) => string.Equals(a ?? string.Empty, b ?? string.Empty, StringComparison.Ordinal);

    private static bool Matches(string? text, string pattern, string mode)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return true;
        if (string.IsNullOrWhiteSpace(text)) return false;
        return mode switch
        {
            "equals" => string.Equals(text.Trim(), pattern.Trim(), StringComparison.OrdinalIgnoreCase),
            "starts_with" => text.Trim().StartsWith(pattern.Trim(), StringComparison.OrdinalIgnoreCase),
            "ends_with" => text.Trim().EndsWith(pattern.Trim(), StringComparison.OrdinalIgnoreCase),
            _ => text.Contains(pattern.Trim(), StringComparison.OrdinalIgnoreCase)
        };
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
