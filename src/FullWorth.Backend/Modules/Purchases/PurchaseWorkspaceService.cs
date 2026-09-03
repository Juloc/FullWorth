using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.Transactions;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Purchases;

public sealed record PurchaseItemPatch(
    Guid? ProductId, Guid? CategoryId, string Name, string? RawName, string? Brand, string? Sku,
    string? Barcode, string? Asin, decimal Quantity, string? QuantityUnit, decimal? PackageQuantity,
    string? PackageUnit, decimal? PackageCount, decimal? UnitPrice, decimal TotalPrice, decimal? BaseUnitPrice,
    decimal? DiscountAmount, decimal? DepositAmount, decimal? TaxRate, decimal? TaxAmount, string Currency,
    string? LineType, string? Notes, int? SortOrder, DateOnly? ReturnDeadline, DateOnly? WarrantyEnd,
    string? SerialNumber, bool TotalPriceOverridden = false,
    decimal? OriginalUnitPrice = null, string? DiscountLabel = null);

public sealed record PurchasePaymentWrite(Guid TransactionId, decimal Amount, string Currency, string LinkSource = "manual", decimal? Confidence = null);
public sealed record PurchasePaymentPatch(decimal Amount, string Currency);
public sealed record DifferenceAcceptanceWrite(string Kind, string Reason, string? Note = null);
public sealed record PurchaseAllocationImportRequest(string Mode = "replace", Guid? RemainderCategoryId = null, bool AddRemainder = false);
public sealed record PurchaseVisibilityWrite(string Visibility);
public sealed record PurchaseReturnWrite(decimal Quantity, decimal Amount, string Currency, Guid? RefundTransactionId, string? Note);

public sealed class PurchaseWorkspaceService(FullWorthDbContext db)
{
    public async Task<object?> GetWorkspaceAsync(Guid userId, Guid fullWorthSpaceId, Guid purchaseId, CancellationToken ct)
    {
        var purchase = await VisiblePurchases(userId, fullWorthSpaceId)
            .Include(x => x.Items.OrderBy(i => i.SortOrder).ThenBy(i => i.CreatedAt))
            .Include(x => x.PaymentLinks)
            .Include(x => x.Documents)
            .Include(x => x.Discounts.OrderBy(d => d.CreatedAt).ThenBy(d => d.Id))
            .Include(x => x.AcceptedDifferences)
            .SingleOrDefaultAsync(x => x.Id == purchaseId, ct);
        if (purchase is null) return null;

        var itemIds = purchase.Items.Select(x => x.Id).ToArray();
        var allocationLinks = await db.TransactionAllocations.AsNoTracking()
            .Where(x =>
                (x.PurchaseItemId.HasValue && itemIds.Contains(x.PurchaseItemId.Value)) ||
                db.Set<PurchaseAllocationLink>().Any(link => link.TransactionAllocationId == x.Id && link.PurchaseId == purchaseId))
            .Select(x => new
            {
                x.Id,
                x.TransactionId,
                x.PurchaseItemId,
                x.CategoryId,
                x.Amount,
                x.Note,
                allocationType = db.Set<PurchaseAllocationLink>()
                    .Where(link => link.TransactionAllocationId == x.Id && link.PurchaseId == purchaseId)
                    .Select(link => link.AllocationType)
                    .FirstOrDefault(),
                purchaseDiscountId = db.Set<PurchaseAllocationLink>()
                    .Where(link => link.TransactionAllocationId == x.Id && link.PurchaseId == purchaseId)
                    .Select(link => link.PurchaseDiscountId)
                    .FirstOrDefault()
            })
            .ToListAsync(ct);
        var reconciliation = Reconciliation(purchase);
        return new
        {
            purchase = ToPurchaseDto(purchase), reconciliation, allocationLinks,
            access = await CanWritePurchaseAsync(userId, fullWorthSpaceId, purchaseId, ct) ? "write" : "read"
        };
    }

    public async Task<object?> ListPagedAsync(Guid userId, Guid fullWorthSpaceId, string? query, DateOnly? from, DateOnly? to,
        Guid? categoryId, Guid? productId, string? reviewState, bool? linked, bool? bookmarked,
        decimal? minAmount, decimal? maxAmount, string? source, int offset, int limit, CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, fullWorthSpaceId, ct)) return null;
        var q = VisiblePurchases(userId, fullWorthSpaceId);
        if (from.HasValue) q = q.Where(x => x.PurchaseDate >= from.Value);
        if (to.HasValue) q = q.Where(x => x.PurchaseDate <= to.Value);
        if (categoryId.HasValue) q = q.Where(x => x.Items.Any(i => i.CategoryId == categoryId.Value));
        if (productId.HasValue) q = q.Where(x => x.Items.Any(i => i.ProductId == productId.Value));
        if (!string.IsNullOrWhiteSpace(reviewState)) q = q.Where(x => x.ReviewState == reviewState);
        if (linked.HasValue) q = linked.Value ? q.Where(x => x.PaymentLinks.Any() || x.TransactionId != null) : q.Where(x => !x.PaymentLinks.Any() && x.TransactionId == null);
        if (bookmarked.HasValue) q = q.Where(x => x.IsBookmarked == bookmarked.Value);
        if (minAmount.HasValue) q = q.Where(x => Math.Abs(x.TotalAmount) >= minAmount.Value);
        if (maxAmount.HasValue) q = q.Where(x => Math.Abs(x.TotalAmount) <= maxAmount.Value);
        if (!string.IsNullOrWhiteSpace(source)) q = q.Where(x => x.Source == source);
        if (!string.IsNullOrWhiteSpace(query))
        {
            var pattern = $"%{query.Trim()}%";
            q = q.Where(x => EF.Functions.ILike(x.Merchant, pattern) ||
                (x.MerchantRaw != null && EF.Functions.ILike(x.MerchantRaw, pattern)) ||
                (x.ReceiptNumber != null && EF.Functions.ILike(x.ReceiptNumber, pattern)) ||
                (x.InvoiceNumber != null && EF.Functions.ILike(x.InvoiceNumber, pattern)) ||
                (x.Notes != null && EF.Functions.ILike(x.Notes, pattern)) ||
                x.Discounts.Any(d => EF.Functions.ILike(d.Label, pattern) ||
                    (d.CouponCode != null && EF.Functions.ILike(d.CouponCode, pattern)) ||
                    (d.RawText != null && EF.Functions.ILike(d.RawText, pattern))) ||
                x.Items.Any(i => EF.Functions.ILike(i.Name, pattern) || EF.Functions.ILike(i.RawName, pattern) ||
                    (i.Brand != null && EF.Functions.ILike(i.Brand, pattern)) || (i.Barcode != null && EF.Functions.ILike(i.Barcode, pattern))));
        }
        offset = Math.Max(0, offset);
        limit = Math.Clamp(limit <= 0 ? 100 : limit, 1, 500);
        var total = await q.CountAsync(ct);
        var page = await q.AsNoTracking()
            .Include(x => x.Items)
            .Include(x => x.Discounts)
            .Include(x => x.PaymentLinks)
            .Include(x => x.Documents)
            .OrderByDescending(x => x.PurchaseDate).ThenByDescending(x => x.CreatedAt)
            .Skip(offset).Take(limit)
            .ToListAsync(ct);
        var rows = page.Select(x =>
        {
            var rec = CalculateReconciliation(x);
            return new
            {
                x.Id, x.Merchant, x.MerchantId, x.PurchaseDate, x.PurchaseTime, x.TotalAmount, x.Currency,
                x.Source, x.Status, x.ReviewState, x.IsBookmarked, x.Visibility, x.UpdatedAt,
                itemCount = x.Items.Count, discountCount = x.Discounts.Count, documentCount = x.Documents.Count, paymentCount = x.PaymentLinks.Count,
                discountAmount = rec.ItemDiscountTotal + rec.BasketDiscountTotal,
                hasDifference = (x.Items.Count > 0 && !rec.ItemsReconciled) || (x.SubtotalAmount.HasValue && !rec.FormulaReconciled),
                itemDifference = rec.ItemDifference,
                formulaDifference = rec.FormulaDifference,
                linked = x.PaymentLinks.Count > 0 || x.TransactionId != null
            };
        }).ToList();
        return new { total, offset, limit, items = rows };
    }

    public async Task<(PurchaseMutationResult Result, object? Value, string? Error)> AddItemAsync(Guid userId, Guid fullWorthSpaceId, Guid purchaseId, PurchaseItemPatch request, CancellationToken ct)
    {
        var access = await WriteAccessAsync(userId, fullWorthSpaceId, purchaseId, ct);
        if (access != PurchaseMutationResult.Success) return (access, null, null);
        var validation = await ValidateItemAsync(fullWorthSpaceId, request, ct);
        if (validation is not null) return (PurchaseMutationResult.Invalid, null, validation);
        var purchase = await db.Purchases.SingleAsync(x => x.Id == purchaseId && x.FullWorthSpaceId == fullWorthSpaceId, ct);
        var sort = request.SortOrder ?? ((await db.PurchaseItems.Where(x => x.PurchaseId == purchaseId).MaxAsync(x => (int?)x.SortOrder, ct) ?? -1) + 1);
        var item = new PurchaseItem { PurchaseId = purchaseId, CreatedAt = DateTimeOffset.UtcNow };
        ApplyItem(item, request, purchase.Currency, sort);
        db.PurchaseItems.Add(item);
        await InvalidatePurchaseReviewAsync(purchase, ct);
        await db.SaveChangesAsync(ct);
        return (PurchaseMutationResult.Success, await GetItemDtoAsync(item.Id, ct), null);
    }

    public async Task<(PurchaseMutationResult Result, object? Value, string? Error)> UpdateItemAsync(Guid userId, Guid fullWorthSpaceId, Guid purchaseId, Guid itemId, PurchaseItemPatch request, CancellationToken ct)
    {
        var access = await WriteAccessAsync(userId, fullWorthSpaceId, purchaseId, ct);
        if (access != PurchaseMutationResult.Success) return (access, null, null);
        var validation = await ValidateItemAsync(fullWorthSpaceId, request, ct);
        if (validation is not null) return (PurchaseMutationResult.Invalid, null, validation);
        var item = await db.PurchaseItems.Include(x => x.Purchase).SingleOrDefaultAsync(x => x.Id == itemId && x.PurchaseId == purchaseId && x.Purchase.FullWorthSpaceId == fullWorthSpaceId, ct);
        if (item is null) return (PurchaseMutationResult.NotFound, null, null);
        var oldCategory = item.CategoryId;
        ApplyItem(item, request, item.Purchase.Currency, request.SortOrder ?? item.SortOrder);
        item.IsManuallyCorrected = true;
        item.UpdatedAt = DateTimeOffset.UtcNow;
        if (oldCategory != item.CategoryId)
        {
            var linkedAllocations = await db.TransactionAllocations.Where(x => x.PurchaseItemId == item.Id).ToListAsync(ct);
            foreach (var allocation in linkedAllocations) allocation.CategoryId = item.CategoryId;
        }
        await InvalidatePurchaseReviewAsync(item.Purchase, ct);
        await db.SaveChangesAsync(ct);
        return (PurchaseMutationResult.Success, await GetItemDtoAsync(item.Id, ct), null);
    }

    public async Task<PurchaseMutationResult> DeleteItemAsync(Guid userId, Guid fullWorthSpaceId, Guid purchaseId, Guid itemId, CancellationToken ct)
    {
        var access = await WriteAccessAsync(userId, fullWorthSpaceId, purchaseId, ct);
        if (access != PurchaseMutationResult.Success) return access;
        var item = await db.PurchaseItems.Include(x => x.Purchase).SingleOrDefaultAsync(x => x.Id == itemId && x.PurchaseId == purchaseId && x.Purchase.FullWorthSpaceId == fullWorthSpaceId, ct);
        if (item is null) return PurchaseMutationResult.NotFound;
        var allocations = await db.TransactionAllocations.Where(x => x.PurchaseItemId == itemId).ToListAsync(ct);
        foreach (var allocation in allocations) allocation.PurchaseItemId = null;
        var itemDiscounts = await db.Set<PurchaseDiscount>().Where(x => x.PurchaseItemId == itemId).ToListAsync(ct);
        var remainingDiscountAmount = await db.Set<PurchaseDiscount>()
            .Where(x => x.PurchaseId == purchaseId && (!x.PurchaseItemId.HasValue || x.PurchaseItemId.Value != itemId))
            .SumAsync(x => (decimal?)x.Amount, ct) ?? 0m;
        db.RemoveRange(itemDiscounts);
        item.Purchase.DiscountAmount = PurchaseArticleCalculator.RoundMoney(remainingDiscountAmount, item.Purchase.Currency);
        db.PurchaseItems.Remove(item);
        await InvalidatePurchaseReviewAsync(item.Purchase, ct);
        await db.SaveChangesAsync(ct);
        return PurchaseMutationResult.Success;
    }

    public async Task<PurchaseMutationResult> ReorderItemsAsync(Guid userId, Guid fullWorthSpaceId, Guid purchaseId, IReadOnlyList<Guid> itemIds, CancellationToken ct)
    {
        var access = await WriteAccessAsync(userId, fullWorthSpaceId, purchaseId, ct);
        if (access != PurchaseMutationResult.Success) return access;
        var items = await db.PurchaseItems.Where(x => x.PurchaseId == purchaseId).ToListAsync(ct);
        if (items.Count != itemIds.Count || itemIds.Distinct().Count() != itemIds.Count || items.Any(x => !itemIds.Contains(x.Id))) return PurchaseMutationResult.Invalid;
        var byId = items.ToDictionary(x => x.Id);
        for (var i = 0; i < itemIds.Count; i++) { byId[itemIds[i]].SortOrder = i; byId[itemIds[i]].UpdatedAt = DateTimeOffset.UtcNow; }
        await db.SaveChangesAsync(ct);
        return PurchaseMutationResult.Success;
    }

    public async Task<PurchaseMutationResult> MatchItemProductAsync(Guid userId, Guid fullWorthSpaceId, Guid purchaseId, Guid itemId, Guid productId, CancellationToken ct)
    {
        var access = await WriteAccessAsync(userId, fullWorthSpaceId, purchaseId, ct);
        if (access != PurchaseMutationResult.Success) return access;
        if (!await db.Set<Product>().AsNoTracking().AnyAsync(x => x.Id == productId && x.FullWorthSpaceId == fullWorthSpaceId && !x.IsArchived, ct)) return PurchaseMutationResult.NotFound;
        var item = await db.PurchaseItems.Where(x => x.PurchaseId == purchaseId && x.Id == itemId).SingleOrDefaultAsync(ct);
        if (item is null) return PurchaseMutationResult.NotFound;
        item.ProductId = productId;
        item.IsManuallyCorrected = true;
        item.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return PurchaseMutationResult.Success;
    }

    public async Task<PurchaseMutationResult> UnlinkItemProductAsync(Guid userId, Guid fullWorthSpaceId, Guid purchaseId, Guid itemId, CancellationToken ct)
    {
        var access = await WriteAccessAsync(userId, fullWorthSpaceId, purchaseId, ct);
        if (access != PurchaseMutationResult.Success) return access;
        var item = await db.PurchaseItems.SingleOrDefaultAsync(x => x.PurchaseId == purchaseId && x.Id == itemId, ct);
        if (item is null) return PurchaseMutationResult.NotFound;
        item.ProductId = null;
        item.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return PurchaseMutationResult.Success;
    }

    public async Task<PurchaseMutationResult> DeletePurchaseAsync(Guid userId, Guid fullWorthSpaceId, Guid purchaseId, CancellationToken ct)
    {
        var access = await WriteAccessAsync(userId, fullWorthSpaceId, purchaseId, ct);
        if (access != PurchaseMutationResult.Success) return access;
        var purchase = await db.Purchases.Include(x => x.Items).SingleOrDefaultAsync(x => x.Id == purchaseId && x.FullWorthSpaceId == fullWorthSpaceId, ct);
        if (purchase is null) return PurchaseMutationResult.NotFound;
        var itemIds = purchase.Items.Select(x => x.Id).ToArray();
        var allocations = await db.TransactionAllocations.Where(x => x.PurchaseItemId.HasValue && itemIds.Contains(x.PurchaseItemId.Value)).ToListAsync(ct);
        foreach (var allocation in allocations) allocation.PurchaseItemId = null;
        db.Purchases.Remove(purchase);
        await db.SaveChangesAsync(ct);
        return PurchaseMutationResult.Success;
    }

    public async Task<object?> ReconciliationAsync(Guid userId, Guid fullWorthSpaceId, Guid purchaseId, CancellationToken ct)
    {
        var purchase = await VisiblePurchases(userId, fullWorthSpaceId)
            .Include(x => x.Items)
            .Include(x => x.Discounts)
            .Include(x => x.PaymentLinks)
            .Include(x => x.AcceptedDifferences)
            .SingleOrDefaultAsync(x => x.Id == purchaseId, ct);
        return purchase is null ? null : Reconciliation(purchase);
    }

    public async Task<(PurchaseMutationResult Result, object? Value, string? Error)> AcceptDifferenceAsync(Guid userId, Guid fullWorthSpaceId, Guid purchaseId, DifferenceAcceptanceWrite request, CancellationToken ct)
    {
        var access = await WriteAccessAsync(userId, fullWorthSpaceId, purchaseId, ct);
        if (access != PurchaseMutationResult.Success) return (access, null, null);
        var kind = (request.Kind ?? string.Empty).Trim().ToLowerInvariant();
        if (kind is not "items" and not "payments") return (PurchaseMutationResult.Invalid, null, "Difference kind must be items or payments.");
        var reason = (request.Reason ?? string.Empty).Trim().ToLowerInvariant();
        if (reason is not "rounding" and not "unknown_line" and not "tip_fee" and not "unreadable" and not "other") return (PurchaseMutationResult.Invalid, null, "Difference reason is invalid.");
        var purchase = await db.Purchases.Include(x => x.Items).Include(x => x.Discounts).Include(x => x.PaymentLinks)
            .SingleAsync(x => x.Id == purchaseId && x.FullWorthSpaceId == fullWorthSpaceId, ct);
        var rec = CalculateReconciliation(purchase);
        var amount = kind == "items" ? rec.ItemDifference : rec.PaymentDifference;
        var existing = await db.Set<PurchaseDifferenceAcceptance>().SingleOrDefaultAsync(x => x.PurchaseId == purchaseId && x.Kind == kind, ct);
        if (existing is null) { existing = new PurchaseDifferenceAcceptance { PurchaseId = purchaseId, Kind = kind, AcceptedByUserId = userId }; db.Add(existing); }
        existing.Amount = amount; existing.Reason = reason; existing.Note = Clean(request.Note); existing.AcceptedByUserId = userId; existing.AcceptedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return (PurchaseMutationResult.Success, new { existing.Id, existing.Kind, existing.Amount, existing.Reason, existing.Note, existing.AcceptedAt }, null);
    }

    public async Task<PurchaseMutationResult> ClearDifferenceAsync(Guid userId, Guid fullWorthSpaceId, Guid purchaseId, string kind, CancellationToken ct)
    {
        var access = await WriteAccessAsync(userId, fullWorthSpaceId, purchaseId, ct);
        if (access != PurchaseMutationResult.Success) return access;
        var row = await db.Set<PurchaseDifferenceAcceptance>().SingleOrDefaultAsync(x => x.PurchaseId == purchaseId && x.Kind == kind.ToLowerInvariant(), ct);
        if (row is null) return PurchaseMutationResult.NotFound;
        db.Remove(row); await db.SaveChangesAsync(ct); return PurchaseMutationResult.Success;
    }

    public async Task<object?> PaymentsAsync(Guid userId, Guid fullWorthSpaceId, Guid purchaseId, CancellationToken ct)
    {
        if (!await VisiblePurchases(userId, fullWorthSpaceId).AnyAsync(x => x.Id == purchaseId, ct)) return null;
        return await db.Set<PurchasePaymentLink>().AsNoTracking().Where(x => x.PurchaseId == purchaseId)
            .OrderBy(x => x.CreatedAt).Select(x => new { x.Id, x.TransactionId, x.Amount, x.Currency, x.LinkSource, x.Confidence, x.CreatedAt, x.UpdatedAt }).ToListAsync(ct);
    }

    public async Task<object?> PaymentCandidatesAsync(Guid userId, Guid fullWorthSpaceId, Guid purchaseId, CancellationToken ct)
    {
        var purchase = await WritablePurchases(userId, fullWorthSpaceId).AsNoTracking().SingleOrDefaultAsync(x => x.Id == purchaseId, ct);
        if (purchase is null) return null;
        var date = purchase.PurchaseDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var existingTransactionIds = await db.Set<PurchasePaymentLink>().Where(x => x.PurchaseId == purchaseId).Select(x => x.TransactionId).ToListAsync(ct);
        var linkedAmount = await db.Set<PurchasePaymentLink>().Where(x => x.PurchaseId == purchaseId && x.Currency == purchase.Currency).SumAsync(x => (decimal?)Math.Abs(x.Amount), ct) ?? 0m;
        var remaining = Math.Max(0m, Math.Abs(purchase.TotalAmount) - linkedAmount);
        var tolerance = PurchaseArticleCalculator.Tolerance(purchase.Currency);
        if (remaining <= tolerance)
            return new { remaining = 0m, purchaseCurrency = purchase.Currency, candidates = Array.Empty<object>() };

        var candidates = await OwnedTransactions(userId, fullWorthSpaceId)
            .Where(x => !existingTransactionIds.Contains(x.Id) && x.Amount < 0 && x.BookingDate >= date.AddDays(-7) && x.BookingDate <= date.AddDays(7))
            .Select(x => new { x.Id, x.AccountId, x.BookingDate, x.Amount, x.Currency, x.Counterparty, x.Description }).ToListAsync(ct);
        var candidateIds = candidates.Select(x => x.Id).ToArray();
        var usedByTransaction = await db.Set<PurchasePaymentLink>().AsNoTracking()
            .Where(x => candidateIds.Contains(x.TransactionId))
            .GroupBy(x => x.TransactionId)
            .Select(g => new { TransactionId = g.Key, Used = g.Sum(x => Math.Abs(x.Amount)) })
            .ToDictionaryAsync(x => x.TransactionId, x => x.Used, ct);

        var scored = candidates.Select(x =>
        {
            var used = usedByTransaction.GetValueOrDefault(x.Id);
            var availableAmount = Math.Max(0m, Math.Abs(x.Amount) - used);
            var suggestedAllocation = Math.Min(availableAmount, remaining);
            var amountScore = suggestedAllocation <= tolerance ? 0m : Math.Max(0m, 1m - Math.Abs(suggestedAllocation - remaining) / Math.Max(1m, remaining));
            var merchantScore = !string.IsNullOrWhiteSpace(x.Counterparty) && !string.IsNullOrWhiteSpace(purchase.Merchant) &&
                (x.Counterparty.Contains(purchase.Merchant, StringComparison.OrdinalIgnoreCase) || purchase.Merchant.Contains(x.Counterparty, StringComparison.OrdinalIgnoreCase)) ? 1m : 0m;
            var dateScore = x.BookingDate.HasValue ? Math.Max(0m, 1m - Math.Abs(x.BookingDate.Value.DayNumber - date.DayNumber) / 8m) : 0m;
            var currencyScore = string.Equals(x.Currency, purchase.Currency, StringComparison.OrdinalIgnoreCase) ? 1m : 0m;
            var confidence = Math.Clamp(amountScore * .60m + merchantScore * .15m + dateScore * .15m + currencyScore * .10m, 0m, 1m);
            return new { x.Id, x.AccountId, x.BookingDate, x.Amount, x.Currency, x.Counterparty, x.Description, availableAmount, suggestedAllocation, confidence };
        }).Where(x => x.availableAmount > tolerance).OrderByDescending(x => x.confidence).Take(20).ToList();
        return new { remaining, purchaseCurrency = purchase.Currency, candidates = scored };
    }

    public async Task<(PurchaseMutationResult Result, object? Value, string? Error)> AddPaymentAsync(Guid userId, Guid fullWorthSpaceId, Guid purchaseId, PurchasePaymentWrite request, CancellationToken ct)
    {
        var purchaseAccess = await WriteAccessAsync(userId, fullWorthSpaceId, purchaseId, ct);
        if (purchaseAccess != PurchaseMutationResult.Success) return (purchaseAccess, null, null);
        if (request.Amount <= 0) return (PurchaseMutationResult.Invalid, null, "Payment amount must be greater than zero.");

        var tx = await OwnedTransactions(userId, fullWorthSpaceId).AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.TransactionId, ct);
        if (tx is null) return (PurchaseMutationResult.NotFound, null, null);
        var transactionCurrency = tx.Currency.Trim().ToUpperInvariant();
        if (!ValidCurrency(transactionCurrency)) return (PurchaseMutationResult.Invalid, null, "Linked transaction currency is invalid.");
        if (await db.Set<PurchasePaymentLink>().AnyAsync(x => x.PurchaseId == purchaseId && x.TransactionId == request.TransactionId, ct))
            return (PurchaseMutationResult.Invalid, null, "Transaction is already linked to this purchase.");

        var used = await db.Set<PurchasePaymentLink>().Where(x => x.TransactionId == request.TransactionId).SumAsync(x => (decimal?)Math.Abs(x.Amount), ct) ?? 0m;
        var available = Math.Max(0m, Math.Abs(tx.Amount) - used);
        if (request.Amount - available > PurchaseArticleCalculator.Tolerance(transactionCurrency))
            return (PurchaseMutationResult.Invalid, new { conflict = "transaction_overallocated", transactionId = tx.Id, transactionAmount = Math.Abs(tx.Amount), alreadyAllocated = used, available, requested = request.Amount }, "Payment allocation exceeds the amount still available on the linked transaction.");

        var link = new PurchasePaymentLink
        {
            FullWorthSpaceId = fullWorthSpaceId, PurchaseId = purchaseId, TransactionId = request.TransactionId,
            Amount = request.Amount, Currency = transactionCurrency, LinkSource = CleanToken(request.LinkSource, "manual"), Confidence = request.Confidence, CreatedByUserId = userId
        };
        db.Add(link); await db.SaveChangesAsync(ct); await MirrorLegacyPaymentAsync(purchaseId, ct);
        return (PurchaseMutationResult.Success, new { link.Id, link.TransactionId, link.Amount, Currency = transactionCurrency, link.LinkSource, link.Confidence }, null);
    }

    public async Task<(PurchaseMutationResult Result, object? Value, string? Error)> UpdatePaymentAsync(Guid userId, Guid fullWorthSpaceId, Guid purchaseId, Guid linkId, PurchasePaymentPatch request, CancellationToken ct)
    {
        var access = await WriteAccessAsync(userId, fullWorthSpaceId, purchaseId, ct);
        if (access != PurchaseMutationResult.Success) return (access, null, null);
        if (request.Amount <= 0) return (PurchaseMutationResult.Invalid, null, "Payment amount must be greater than zero.");
        var link = await db.Set<PurchasePaymentLink>().SingleOrDefaultAsync(x => x.Id == linkId && x.PurchaseId == purchaseId && x.FullWorthSpaceId == fullWorthSpaceId, ct);
        if (link is null) return (PurchaseMutationResult.NotFound, null, null);

        var tx = await OwnedTransactions(userId, fullWorthSpaceId).AsNoTracking().SingleOrDefaultAsync(x => x.Id == link.TransactionId, ct);
        if (tx is null) return (PurchaseMutationResult.NotFound, null, null);
        var transactionCurrency = tx.Currency.Trim().ToUpperInvariant();
        if (!ValidCurrency(transactionCurrency)) return (PurchaseMutationResult.Invalid, null, "Linked transaction currency is invalid.");
        var usedByOthers = await db.Set<PurchasePaymentLink>()
            .Where(x => x.TransactionId == link.TransactionId && x.Id != link.Id)
            .SumAsync(x => (decimal?)Math.Abs(x.Amount), ct) ?? 0m;
        var available = Math.Max(0m, Math.Abs(tx.Amount) - usedByOthers);
        if (request.Amount - available > PurchaseArticleCalculator.Tolerance(transactionCurrency))
            return (PurchaseMutationResult.Invalid, new { conflict = "transaction_overallocated", transactionId = tx.Id, transactionAmount = Math.Abs(tx.Amount), allocatedByOtherPurchases = usedByOthers, available, requested = request.Amount }, "Payment allocation exceeds the amount still available on the linked transaction.");

        link.Amount = request.Amount; link.Currency = transactionCurrency; link.UpdatedAt = DateTimeOffset.UtcNow; await db.SaveChangesAsync(ct);
        return (PurchaseMutationResult.Success, new { link.Id, link.TransactionId, link.Amount, Currency = transactionCurrency, link.LinkSource, link.Confidence }, null);
    }

    public async Task<PurchaseMutationResult> DeletePaymentAsync(Guid userId, Guid fullWorthSpaceId, Guid purchaseId, Guid linkId, CancellationToken ct)
    {
        var access = await WriteAccessAsync(userId, fullWorthSpaceId, purchaseId, ct);
        if (access != PurchaseMutationResult.Success) return access;
        var link = await db.Set<PurchasePaymentLink>().SingleOrDefaultAsync(x => x.Id == linkId && x.PurchaseId == purchaseId && x.FullWorthSpaceId == fullWorthSpaceId, ct);
        if (link is null) return PurchaseMutationResult.NotFound;
        db.Remove(link); await db.SaveChangesAsync(ct); await MirrorLegacyPaymentAsync(purchaseId, ct); return PurchaseMutationResult.Success;
    }

    public async Task<(PurchaseMutationResult Result, bool Linked)> AutoLinkAsync(Guid userId, Guid fullWorthSpaceId, Guid purchaseId, CancellationToken ct)
    {
        if (await PaymentCandidatesAsync(userId, fullWorthSpaceId, purchaseId, ct) is null) return (PurchaseMutationResult.NotFound, false);
        var purchase = await WritablePurchases(userId, fullWorthSpaceId).AsNoTracking().SingleAsync(x => x.Id == purchaseId, ct);
        var date = purchase.PurchaseDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var existingIds = await db.Set<PurchasePaymentLink>().Where(x => x.PurchaseId == purchaseId).Select(x => x.TransactionId).ToListAsync(ct);
        var linkedSum = await db.Set<PurchasePaymentLink>().Where(x => x.PurchaseId == purchaseId && x.Currency == purchase.Currency).SumAsync(x => (decimal?)Math.Abs(x.Amount), ct) ?? 0m;
        var target = Math.Max(0m, Math.Abs(purchase.TotalAmount) - linkedSum);
        var tolerance = PurchaseArticleCalculator.Tolerance(purchase.Currency);
        if (target <= tolerance) return (PurchaseMutationResult.Success, false);

        var transactions = await OwnedTransactions(userId, fullWorthSpaceId).Where(x => !existingIds.Contains(x.Id) && x.Amount < 0 && x.BookingDate >= date.AddDays(-7) && x.BookingDate <= date.AddDays(7) && x.Currency == purchase.Currency)
            .Select(x => new { x.Id, x.Amount, x.BookingDate, x.Counterparty }).ToListAsync(ct);
        var transactionIds = transactions.Select(x => x.Id).ToArray();
        var usedByTransaction = await db.Set<PurchasePaymentLink>().AsNoTracking()
            .Where(x => transactionIds.Contains(x.TransactionId))
            .GroupBy(x => x.TransactionId)
            .Select(g => new { TransactionId = g.Key, Used = g.Sum(x => Math.Abs(x.Amount)) })
            .ToDictionaryAsync(x => x.TransactionId, x => x.Used, ct);
        var scored = transactions.Select(x =>
        {
            var available = Math.Max(0m, Math.Abs(x.Amount) - usedByTransaction.GetValueOrDefault(x.Id));
            var amount = Math.Min(available, target);
            return new { x.Id, amount, confidence = ScorePayment(purchase, target, amount, x.BookingDate, x.Counterparty) };
        }).Where(x => x.amount > tolerance).OrderByDescending(x => x.confidence).ToList();
        if (scored.Count == 0 || scored[0].confidence < .94m || (scored.Count > 1 && scored[0].confidence - scored[1].confidence < .08m)) return (PurchaseMutationResult.Success, false);
        var add = await AddPaymentAsync(userId, fullWorthSpaceId, purchaseId, new PurchasePaymentWrite(scored[0].Id, scored[0].amount, purchase.Currency, "auto", scored[0].confidence), ct);
        return (add.Result, add.Result == PurchaseMutationResult.Success);
    }

    public async Task<(PurchaseMutationResult Result, object? Value, string? Error)> ImportAllocationsFromPurchaseAsync(Guid userId, Guid fullWorthSpaceId, Guid transactionId, Guid purchaseId, PurchaseAllocationImportRequest request, CancellationToken ct)
    {
        var tx = await OwnedTransactions(userId, fullWorthSpaceId).SingleOrDefaultAsync(x => x.Id == transactionId, ct);
        if (tx is null) return (PurchaseMutationResult.NotFound, null, null);
        if (tx.IsTransfer) return (PurchaseMutationResult.Invalid, null, "Convert the transfer to a normal transaction before adding purchase allocations.");

        var purchase = await VisiblePurchases(userId, fullWorthSpaceId)
            .Include(x => x.Items)
            .Include(x => x.Discounts)
            .Include(x => x.PaymentLinks)
            .SingleOrDefaultAsync(x => x.Id == purchaseId, ct);
        if (purchase is null) return (PurchaseMutationResult.NotFound, null, null);
        if (purchase.TransactionId != transactionId && !purchase.PaymentLinks.Any(x => x.TransactionId == transactionId))
            return (PurchaseMutationResult.Invalid, null, "Purchase must be linked to this transaction before its articles can be imported.");
        if (!string.Equals(purchase.Currency, tx.Currency, StringComparison.OrdinalIgnoreCase))
            return (PurchaseMutationResult.Invalid, null, "Purchase and transaction currencies must match for article allocation import.");

        var tolerance = PurchaseArticleCalculator.Tolerance(tx.Currency);
        var canonicalLinks = purchase.PaymentLinks.ToList();
        var paymentForTransaction = canonicalLinks.SingleOrDefault(x => x.TransactionId == transactionId);
        var ambiguousPaymentSplit = canonicalLinks.Count > 1 ||
            (paymentForTransaction is not null && Math.Abs(Math.Abs(paymentForTransaction.Amount) - Math.Abs(purchase.TotalAmount)) > tolerance);
        if (ambiguousPaymentSplit)
        {
            return (PurchaseMutationResult.Success, new
            {
                preview = true,
                requiresManualSelection = true,
                reason = "Purchase is paid by multiple or partial payment allocations. Select individual articles for this transaction instead of importing the whole purchase.",
                transactionAmount = tx.Amount,
                purchaseTotal = purchase.TotalAmount,
                paymentAmount = paymentForTransaction?.Amount,
                items = purchase.Items.OrderBy(x => x.SortOrder).Select(x => new { x.Id, x.CategoryId, x.Name, x.TotalPrice, x.DepositAmount, x.Currency, x.LineType })
            }, null);
        }

        if (purchase.Items.Count == 0) return (PurchaseMutationResult.Invalid, null, "Purchase has no items to allocate.");
        var reconciliation = CalculateReconciliation(purchase);
        var now = DateTimeOffset.UtcNow;
        var built = PurchaseAllocationBuilder.Build(purchase, tx, reconciliation, now);
        var proposed = built.Allocations;
        var provenance = built.Links;
        var allocated = PurchaseArticleCalculator.RoundMoney(proposed.Sum(x => x.Amount), tx.Currency);
        var remainder = PurchaseArticleCalculator.RoundMoney(tx.Amount - allocated, tx.Currency);
        if (Math.Abs(remainder) > tolerance)
        {
            if (!request.AddRemainder)
                return (PurchaseMutationResult.Success, new
                {
                    preview = true,
                    requiresManualSelection = false,
                    transactionAmount = tx.Amount,
                    allocated,
                    remainder,
                    reconciliation,
                    lines = proposed.Select(ToAllocationDto).ToList()
                }, null);
            if (!request.RemainderCategoryId.HasValue || !await db.Categories.AnyAsync(x => x.Id == request.RemainderCategoryId && x.FullWorthSpaceId == fullWorthSpaceId, ct))
                return (PurchaseMutationResult.Invalid, null, "A valid remainder category is required.");
            var remainderAllocation = new TransactionAllocation
            {
                Id = Guid.NewGuid(),
                TransactionId = transactionId,
                CategoryId = request.RemainderCategoryId,
                Amount = remainder,
                Note = "Remainder",
                CreatedAt = now.AddTicks(proposed.Count),
                UpdatedAt = now
            };
            proposed.Add(remainderAllocation);
            provenance.Add(new PurchaseAllocationLink
            {
                TransactionAllocationId = remainderAllocation.Id,
                PurchaseId = purchase.Id,
                AllocationType = "remainder",
                CreatedAt = now
            });
            allocated = PurchaseArticleCalculator.RoundMoney(allocated + remainder, tx.Currency);
            remainder = 0m;
        }
        if (Math.Abs(tx.Amount - allocated) > tolerance)
            return (PurchaseMutationResult.Invalid, null, "Proposed allocations do not net to the transaction amount.");

        var current = await db.TransactionAllocations.Where(x => x.TransactionId == transactionId).OrderBy(x => x.CreatedAt).ToListAsync(ct);
        var mode = CleanToken(request.Mode, "replace");
        if (mode == "preview")
            return (PurchaseMutationResult.Success, new { preview = true, conflict = current.Count > 0, existing = current.Select(ToAllocationDto).ToList(), proposed = proposed.Select(ToAllocationDto).ToList(), transactionAmount = tx.Amount, allocated, remainder, reconciliation }, null);
        if (mode == "merge")
        {
            var combined = current.Sum(x => x.Amount) + proposed.Sum(x => x.Amount);
            if (Math.Abs(combined - tx.Amount) > tolerance)
                return (PurchaseMutationResult.Invalid, null, "Merged allocations would not net to the transaction. Choose replace or edit the current split first.");
        }
        else if (mode == "replace") db.TransactionAllocations.RemoveRange(current);
        else return (PurchaseMutationResult.Invalid, null, "Allocation mode must be replace, merge or preview.");

        db.TransactionAllocations.AddRange(proposed);
        db.Set<PurchaseAllocationLink>().AddRange(provenance);
        await db.SaveChangesAsync(ct);
        return (PurchaseMutationResult.Success, new { preview = false, transactionAmount = tx.Amount, allocated = proposed.Sum(x => x.Amount), remaining = 0m, reconciliation, lines = proposed.Select(ToAllocationDto).ToList() }, null);
    }

    public async Task<PurchaseMutationResult> ClearAllocationsAsync(Guid userId, Guid fullWorthSpaceId, Guid transactionId, CancellationToken ct)
    {
        if (!await OwnedTransactions(userId, fullWorthSpaceId).AnyAsync(x => x.Id == transactionId, ct)) return PurchaseMutationResult.NotFound;
        var rows = await db.TransactionAllocations.Where(x => x.TransactionId == transactionId).ToListAsync(ct);
        db.TransactionAllocations.RemoveRange(rows); await db.SaveChangesAsync(ct); return PurchaseMutationResult.Success;
    }

    public async Task<(PurchaseMutationResult Result, object? Value, string? Error)> RecordReturnAsync(Guid userId, Guid fullWorthSpaceId, Guid purchaseId, Guid itemId, PurchaseReturnWrite request, CancellationToken ct)
    {
        var access = await WriteAccessAsync(userId, fullWorthSpaceId, purchaseId, ct);
        if (access != PurchaseMutationResult.Success) return (access, null, null);
        var item = await db.PurchaseItems.AsNoTracking().SingleOrDefaultAsync(x => x.Id == itemId && x.PurchaseId == purchaseId, ct);
        if (item is null) return (PurchaseMutationResult.NotFound, null, null);
        if (request.Quantity <= 0 || request.Quantity > item.Quantity) return (PurchaseMutationResult.Invalid, null, "Returned quantity is invalid.");
        if (request.Amount < 0) return (PurchaseMutationResult.Invalid, null, "Refund amount must not be negative.");
        if (!ValidCurrency(request.Currency)) return (PurchaseMutationResult.Invalid, null, "Refund currency is invalid.");
        if (request.RefundTransactionId.HasValue && !await OwnedTransactions(userId, fullWorthSpaceId).AnyAsync(x => x.Id == request.RefundTransactionId, ct)) return (PurchaseMutationResult.NotFound, null, null);
        var returnedQty = await db.Set<PurchaseItemReturn>().Where(x => x.PurchaseItemId == itemId).SumAsync(x => (decimal?)x.Quantity, ct) ?? 0m;
        if (returnedQty + request.Quantity > item.Quantity) return (PurchaseMutationResult.Invalid, null, "Returned quantity exceeds purchased quantity.");
        var row = new PurchaseItemReturn { PurchaseItemId = itemId, RefundTransactionId = request.RefundTransactionId, Quantity = request.Quantity, Amount = request.Amount, Currency = request.Currency.Trim().ToUpperInvariant(), Note = Clean(request.Note), CreatedByUserId = userId };
        db.Add(row);
        if (request.RefundTransactionId.HasValue)
        {
            var refund = await db.Transactions.SingleAsync(x => x.Id == request.RefundTransactionId, ct);
            var originalPayment = await db.Set<PurchasePaymentLink>().Where(x => x.PurchaseId == purchaseId).OrderBy(x => x.CreatedAt).Select(x => (Guid?)x.TransactionId).FirstOrDefaultAsync(ct);
            if (!originalPayment.HasValue) originalPayment = await db.Purchases.Where(x => x.Id == purchaseId).Select(x => x.TransactionId).SingleAsync(ct);
            if (originalPayment.HasValue) { refund.RefundOfTransactionId = originalPayment.Value; refund.RefundCategoryId = item.CategoryId; refund.UpdatedAt = DateTimeOffset.UtcNow; }
        }
        await db.SaveChangesAsync(ct);
        return (PurchaseMutationResult.Success, new { row.Id, row.PurchaseItemId, row.RefundTransactionId, row.Quantity, row.Amount, row.Currency, row.Status, row.Note, row.CreatedAt }, null);
    }

    public async Task<PurchaseMutationResult> SetVisibilityAsync(Guid userId, Guid fullWorthSpaceId, Guid purchaseId, PurchaseVisibilityWrite request, CancellationToken ct)
    {
        var access = await WriteAccessAsync(userId, fullWorthSpaceId, purchaseId, ct);
        if (access != PurchaseMutationResult.Success) return access;
        var visibility = CleanToken(request.Visibility, "space");
        if (visibility is not "space" and not "private") return PurchaseMutationResult.Invalid;
        var purchase = await db.Purchases.SingleAsync(x => x.Id == purchaseId && x.FullWorthSpaceId == fullWorthSpaceId, ct);
        if (visibility == "private" && purchase.CreatedByUserId.HasValue && purchase.CreatedByUserId != userId) return PurchaseMutationResult.Forbidden;
        if (!purchase.CreatedByUserId.HasValue) purchase.CreatedByUserId = userId;
        purchase.Visibility = visibility; purchase.UpdatedAt = DateTimeOffset.UtcNow; await db.SaveChangesAsync(ct); return PurchaseMutationResult.Success;
    }

    private object Reconciliation(Purchase purchase)
    {
        var calculation = CalculateReconciliation(purchase);
        var accepted = purchase.AcceptedDifferences.ToDictionary(x => x.Kind, x => new { x.Amount, x.Reason, x.Note, x.AcceptedAt });
        return new
        {
            purchase.Id, purchase.Currency,
            calculation.PurchaseTotal,
            calculation.ItemTotal,
            calculation.MerchandiseTotal,
            calculation.ItemDiscountTotal,
            calculation.BasketDiscountTotal,
            totalDiscount = calculation.ItemDiscountTotal + calculation.BasketDiscountTotal,
            calculation.DepositTotal,
            calculation.AdditionalChargeTotal,
            calculation.RoundingAmount,
            calculation.ItemDifference,
            calculation.SubtotalAmount,
            calculation.FormulaTotal,
            calculation.FormulaDifference,
            calculation.LinkedPaymentTotal,
            calculation.PaymentDifference,
            calculation.ItemsReconciled,
            calculation.FormulaReconciled,
            calculation.PaymentsReconciled,
            calculation.FullyReconciled,
            calculation.Tolerance,
            hasForeignCurrencyPayments = purchase.PaymentLinks.Any(x => !string.Equals(x.Currency, purchase.Currency, StringComparison.OrdinalIgnoreCase)),
            acceptedDifferences = accepted
        };
    }

    private static PurchaseReconciliationCalculation CalculateReconciliation(Purchase purchase) =>
        PurchaseArticleCalculator.Reconcile(
            purchase.TotalAmount,
            purchase.Items,
            purchase.Discounts,
            purchase.PaymentLinks,
            purchase.Currency,
            purchase.SubtotalAmount,
            purchase.DiscountAmount,
            purchase.DepositAmount,
            purchase.RoundingAmount,
            purchase.TipAmount,
            purchase.ShippingAmount,
            purchase.FeeAmount);

    private async Task InvalidatePurchaseReviewAsync(Purchase purchase, CancellationToken ct)
    {
        purchase.Status = "review";
        purchase.ReviewState = "needs_review";
        purchase.UpdatedAt = DateTimeOffset.UtcNow;
        var accepted = await db.Set<PurchaseDifferenceAcceptance>().Where(x => x.PurchaseId == purchase.Id).ToListAsync(ct);
        if (accepted.Count > 0) db.RemoveRange(accepted);
        await InvalidateGeneratedAllocationsAsync(purchase.Id, ct);
    }

    private async Task InvalidateGeneratedAllocationsAsync(Guid purchaseId, CancellationToken ct)
    {
        var allocationIds = await db.Set<PurchaseAllocationLink>()
            .Where(x => x.PurchaseId == purchaseId)
            .Select(x => x.TransactionAllocationId)
            .ToListAsync(ct);
        if (allocationIds.Count == 0) return;
        var allocations = await db.TransactionAllocations.Where(x => allocationIds.Contains(x.Id)).ToListAsync(ct);
        if (allocations.Count > 0) db.TransactionAllocations.RemoveRange(allocations);
    }

    private async Task MirrorLegacyPaymentAsync(Guid purchaseId, CancellationToken ct)
    {
        var purchase = await db.Purchases.SingleAsync(x => x.Id == purchaseId, ct);
        var links = await db.Set<PurchasePaymentLink>().Where(x => x.PurchaseId == purchaseId).OrderBy(x => x.CreatedAt).ToListAsync(ct);
        purchase.TransactionId = links.Count == 1 ? links[0].TransactionId : null;
        purchase.MatchConfidence = links.Count == 1 ? links[0].Confidence : null;
        purchase.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private async Task<string?> ValidateItemAsync(Guid fullWorthSpaceId, PurchaseItemPatch request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name)) return "Item name is required.";
        if (request.Quantity <= 0) return "Quantity must be greater than zero.";
        if (!ValidCurrency(request.Currency)) return "Item currency is invalid.";
        if (request.PackageQuantity is <= 0) return "Package quantity must be greater than zero.";
        if (request.PackageCount is <= 0) return "Package count must be greater than zero.";
        if (request.OriginalUnitPrice is < 0m) return "Original unit price must not be negative.";
        if (request.CategoryId.HasValue && !await db.Categories.AsNoTracking().AnyAsync(x => x.Id == request.CategoryId && x.FullWorthSpaceId == fullWorthSpaceId, ct)) return "Category must belong to the FullWorth Space.";
        if (request.ProductId.HasValue && !await db.Set<Product>().AsNoTracking().AnyAsync(x => x.Id == request.ProductId && x.FullWorthSpaceId == fullWorthSpaceId, ct)) return "Product must belong to the FullWorth Space.";
        return null;
    }

    private static void ApplyItem(PurchaseItem item, PurchaseItemPatch request, string purchaseCurrency, int sort)
    {
        item.ProductId = request.ProductId; item.CategoryId = request.CategoryId;
        item.RawName = string.IsNullOrWhiteSpace(request.RawName) ? request.Name.Trim() : request.RawName.Trim();
        item.Name = request.Name.Trim(); item.Brand = Clean(request.Brand); item.Sku = Clean(request.Sku); item.Barcode = Clean(request.Barcode); item.Asin = Clean(request.Asin);
        item.Quantity = request.Quantity; item.QuantityUnit = PurchaseArticleCalculator.NormalizeUnit(request.QuantityUnit);
        item.PackageQuantity = request.PackageQuantity; item.PackageUnit = Clean(request.PackageUnit)?.ToLowerInvariant(); item.PackageCount = request.PackageCount;
        item.UnitPrice = request.UnitPrice; item.OriginalUnitPrice = request.OriginalUnitPrice;
        item.BaseUnitPrice = request.BaseUnitPrice ?? PurchaseArticleCalculator.BaseUnitPrice(request.UnitPrice, request.Quantity, request.QuantityUnit, request.PackageCount, request.PackageQuantity, request.PackageUnit, request.Currency);
        item.TotalPrice = PurchaseArticleCalculator.RoundMoney(request.TotalPrice, request.Currency);
        // DiscountAmount/DiscountLabel are mirrors of PurchaseDiscount rows. Item editing must not create a
        // second discount source of truth; the dedicated discount editor owns those fields.
        item.DepositAmount = request.DepositAmount; item.TaxRate = request.TaxRate; item.TaxAmount = request.TaxAmount;
        item.Currency = string.IsNullOrWhiteSpace(request.Currency) ? purchaseCurrency : request.Currency.Trim().ToUpperInvariant();
        item.LineType = CleanToken(request.LineType, "product"); item.CategorizationSource = request.CategoryId.HasValue ? "manual" : item.CategorizationSource;
        item.Notes = Clean(request.Notes); item.SortOrder = sort; item.ReturnDeadline = request.ReturnDeadline; item.WarrantyEnd = request.WarrantyEnd; item.SerialNumber = Clean(request.SerialNumber);
        item.TotalPriceOverridden = request.TotalPriceOverridden;
    }

    private async Task<object?> GetItemDtoAsync(Guid itemId, CancellationToken ct) => await db.PurchaseItems.AsNoTracking().Where(x => x.Id == itemId).Select(x => new
    {
        x.Id, x.PurchaseId, x.ProductId, x.CategoryId, x.RawName, x.Name, x.Brand, x.Sku, x.Barcode, x.Asin,
        x.Quantity, x.QuantityUnit, x.PackageQuantity, x.PackageUnit, x.PackageCount, x.UnitPrice, x.OriginalUnitPrice, x.BaseUnitPrice, x.TotalPrice,
        x.DiscountAmount, x.DiscountLabel, x.DepositAmount, x.TaxRate, x.TaxAmount, x.Currency, x.LineType, x.CategorizationSource,
        x.ExtractionConfidence, x.IsManuallyCorrected, x.TotalPriceOverridden, x.Notes, x.SortOrder, x.ReturnDeadline, x.WarrantyEnd, x.SerialNumber, x.CreatedAt, x.UpdatedAt
    }).SingleOrDefaultAsync(ct);

    private IQueryable<Purchase> VisiblePurchases(Guid userId, Guid fullWorthSpaceId) => db.Purchases.Where(purchase =>
        purchase.FullWorthSpaceId == fullWorthSpaceId && db.FullWorthSpaceMembers.Any(m => m.FullWorthSpaceId == fullWorthSpaceId && m.UserId == userId) &&
        (purchase.Visibility != "private" || purchase.CreatedByUserId == userId) &&
        (!purchase.PaymentLinks.Any() || purchase.PaymentLinks.Any(link => db.Transactions.Any(tx => tx.Id == link.TransactionId && db.Accounts.Any(account => account.Id == tx.AccountId && account.FullWorthSpaceId == fullWorthSpaceId && account.Owners.Any(owner => owner.UserId == userId))))) &&
        (purchase.TransactionId == null || db.Transactions.Any(tx => tx.Id == purchase.TransactionId && db.Accounts.Any(account => account.Id == tx.AccountId && account.FullWorthSpaceId == fullWorthSpaceId && account.Owners.Any(owner => owner.UserId == userId)))));

    private IQueryable<Purchase> WritablePurchases(Guid userId, Guid fullWorthSpaceId) => db.Purchases.Where(purchase =>
        purchase.FullWorthSpaceId == fullWorthSpaceId && db.FullWorthSpaceMembers.Any(m => m.FullWorthSpaceId == fullWorthSpaceId && m.UserId == userId) &&
        (purchase.Visibility != "private" || purchase.CreatedByUserId == userId) &&
        (!purchase.PaymentLinks.Any() || purchase.PaymentLinks.All(link => db.Transactions.Any(tx => tx.Id == link.TransactionId && db.Accounts.Any(account => account.Id == tx.AccountId && account.FullWorthSpaceId == fullWorthSpaceId && account.Owners.Any(owner => owner.UserId == userId && owner.OwnershipType == AccountOwnershipTypes.Owner))))) &&
        (purchase.TransactionId == null || db.Transactions.Any(tx => tx.Id == purchase.TransactionId && db.Accounts.Any(account => account.Id == tx.AccountId && account.FullWorthSpaceId == fullWorthSpaceId && account.Owners.Any(owner => owner.UserId == userId && owner.OwnershipType == AccountOwnershipTypes.Owner)))));

    private IQueryable<FinanceTransaction> OwnedTransactions(Guid userId, Guid fullWorthSpaceId) => db.Transactions.Where(tx => db.Accounts.Any(account =>
        account.Id == tx.AccountId && account.FullWorthSpaceId == fullWorthSpaceId && account.Owners.Any(owner => owner.UserId == userId && owner.OwnershipType == AccountOwnershipTypes.Owner)) &&
        db.FullWorthSpaceMembers.Any(m => m.FullWorthSpaceId == fullWorthSpaceId && m.UserId == userId));

    private async Task<PurchaseMutationResult> WriteAccessAsync(Guid userId, Guid fullWorthSpaceId, Guid purchaseId, CancellationToken ct)
    {
        if (await WritablePurchases(userId, fullWorthSpaceId).AnyAsync(x => x.Id == purchaseId, ct)) return PurchaseMutationResult.Success;
        if (await VisiblePurchases(userId, fullWorthSpaceId).AnyAsync(x => x.Id == purchaseId, ct)) return PurchaseMutationResult.Forbidden;
        return PurchaseMutationResult.NotFound;
    }

    private async Task<bool> CanWritePurchaseAsync(Guid userId, Guid fullWorthSpaceId, Guid purchaseId, CancellationToken ct) => await WritablePurchases(userId, fullWorthSpaceId).AnyAsync(x => x.Id == purchaseId, ct);
    private async Task<bool> IsMemberAsync(Guid userId, Guid fullWorthSpaceId, CancellationToken ct) => await db.FullWorthSpaceMembers.AsNoTracking().AnyAsync(x => x.FullWorthSpaceId == fullWorthSpaceId && x.UserId == userId, ct);

    private static object ToPurchaseDto(Purchase p) => new
    {
        p.Id, p.FullWorthSpaceId, p.Source, p.Merchant, p.MerchantId, p.MerchantRaw, p.ExternalOrderId, p.PurchaseDate, p.PurchaseTime, p.TimeZone,
        p.SubtotalAmount, p.DiscountAmount, p.DepositAmount, p.RoundingAmount, p.TaxAmount, p.TipAmount, p.ShippingAmount, p.FeeAmount,
        p.TotalAmount, p.Currency, p.Status, p.ReviewState, p.ReceiptNumber, p.InvoiceNumber, p.PaymentMethodText, p.SourceReference, p.Notes,
        p.IsBookmarked, p.CreatedByUserId, p.PaidByUserId, p.ForWhomUserId, p.Visibility, p.CreatedAt, p.UpdatedAt,
        items = p.Items.Select(i => new
        {
            i.Id, i.ProductId, i.CategoryId, i.RawName, i.Name, i.Brand, i.Sku, i.Barcode, i.Asin,
            i.Quantity, i.QuantityUnit, i.PackageQuantity, i.PackageUnit, i.PackageCount,
            i.UnitPrice, i.OriginalUnitPrice, i.BaseUnitPrice, i.TotalPrice, i.DiscountAmount, i.DiscountLabel,
            i.DepositAmount, i.TaxRate, i.TaxAmount, i.Currency, i.LineType, i.CategorizationSource,
            i.ExtractionConfidence, i.IsManuallyCorrected, i.TotalPriceOverridden, i.Notes, i.SortOrder,
            i.ReturnDeadline, i.WarrantyEnd, i.SerialNumber
        }).ToList(),
        discounts = p.Discounts.Select(d => new
        {
            d.Id, d.PurchaseItemId, d.Type, d.Label, d.Amount, d.Percentage, d.CouponCode,
            d.RawText, d.Source, d.Confidence, d.CreatedAt, d.UpdatedAt
        }).ToList(),
        payments = p.PaymentLinks.Select(x => new { x.Id, x.TransactionId, x.Amount, x.Currency, x.LinkSource, x.Confidence }).ToList(),
        documents = p.Documents.Select(x => new { x.Id, x.DocumentType, x.OriginalFileName, x.MediaType, x.SizeBytes, x.Status, x.CreatedAt }).ToList()
    };

    private static object ToAllocationDto(TransactionAllocation x) => new { x.Id, x.TransactionId, x.CategoryId, x.Amount, x.Note, x.PurchaseItemId, kind = x.PurchaseItemId.HasValue ? "article" : "category" };
    private static bool ValidCurrency(string? value) => value is { Length: 3 } && value.All(c => c is >= 'A' and <= 'Z');
    private static string CleanToken(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim().ToLowerInvariant();
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static decimal ScorePayment(Purchase purchase, decimal target, decimal amount, DateOnly? date, string? counterparty)
    {
        if (target <= 0 || amount <= 0) return 0m;
        var amountScore = Math.Max(0m, 1m - Math.Abs(Math.Abs(amount) - target) / Math.Max(1m, target));
        var merchantScore = !string.IsNullOrWhiteSpace(counterparty) && !string.IsNullOrWhiteSpace(purchase.Merchant) &&
            (counterparty.Contains(purchase.Merchant, StringComparison.OrdinalIgnoreCase) || purchase.Merchant.Contains(counterparty, StringComparison.OrdinalIgnoreCase)) ? 1m : 0m;
        var dateScore = date.HasValue && purchase.PurchaseDate.HasValue ? Math.Max(0m, 1m - Math.Abs(date.Value.DayNumber - purchase.PurchaseDate.Value.DayNumber) / 8m) : 0m;
        return Math.Clamp(amountScore * .70m + merchantScore * .15m + dateScore * .15m, 0m, 1m);
    }
}

public static class PurchaseWorkspaceEndpoints
{
    public static IEndpointRouteBuilder MapPurchaseWorkspaceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/purchases").WithTags("Purchases");
        group.MapGet("/paged", async (Guid fullWorthSpaceId, string? query, DateOnly? from, DateOnly? to, Guid? categoryId, Guid? productId, string? reviewState, bool? linked, bool? bookmarked, decimal? minAmount, decimal? maxAmount, string? source, int? offset, int? limit, FullWorth.Backend.Security.CurrentUserContext user, PurchaseWorkspaceService service, CancellationToken ct) =>
        { var value = await service.ListPagedAsync(user.RequireUserId(), fullWorthSpaceId, query, from, to, categoryId, productId, reviewState, linked, bookmarked, minAmount, maxAmount, source, offset ?? 0, limit ?? 100, ct); return value is null ? Results.NotFound() : Results.Ok(value); });
        group.MapGet("/{id:guid}/workspace", async (Guid id, Guid fullWorthSpaceId, FullWorth.Backend.Security.CurrentUserContext user, PurchaseWorkspaceService service, CancellationToken ct) => { var value = await service.GetWorkspaceAsync(user.RequireUserId(), fullWorthSpaceId, id, ct); return value is null ? Results.NotFound() : Results.Ok(value); });
        group.MapDelete("/{id:guid}", async (Guid id, Guid fullWorthSpaceId, FullWorth.Backend.Security.CurrentUserContext user, PurchaseWorkspaceService service, CancellationToken ct) => Mutation(await service.DeletePurchaseAsync(user.RequireUserId(), fullWorthSpaceId, id, ct)));
        group.MapPost("/{id:guid}/items", async (Guid id, Guid fullWorthSpaceId, PurchaseItemPatch request, FullWorth.Backend.Security.CurrentUserContext user, PurchaseWorkspaceService service, CancellationToken ct) => Outcome(await service.AddItemAsync(user.RequireUserId(), fullWorthSpaceId, id, request, ct), true));
        group.MapPatch("/{id:guid}/items/{itemId:guid}", async (Guid id, Guid itemId, Guid fullWorthSpaceId, PurchaseItemPatch request, FullWorth.Backend.Security.CurrentUserContext user, PurchaseWorkspaceService service, CancellationToken ct) => Outcome(await service.UpdateItemAsync(user.RequireUserId(), fullWorthSpaceId, id, itemId, request, ct)));
        group.MapDelete("/{id:guid}/items/{itemId:guid}", async (Guid id, Guid itemId, Guid fullWorthSpaceId, FullWorth.Backend.Security.CurrentUserContext user, PurchaseWorkspaceService service, CancellationToken ct) => Mutation(await service.DeleteItemAsync(user.RequireUserId(), fullWorthSpaceId, id, itemId, ct)));
        group.MapPut("/{id:guid}/items/reorder", async (Guid id, Guid fullWorthSpaceId, List<Guid> itemIds, FullWorth.Backend.Security.CurrentUserContext user, PurchaseWorkspaceService service, CancellationToken ct) => Mutation(await service.ReorderItemsAsync(user.RequireUserId(), fullWorthSpaceId, id, itemIds, ct)));
        group.MapPost("/{id:guid}/items/{itemId:guid}/match-product", async (Guid id, Guid itemId, Guid productId, Guid fullWorthSpaceId, FullWorth.Backend.Security.CurrentUserContext user, PurchaseWorkspaceService service, CancellationToken ct) => Mutation(await service.MatchItemProductAsync(user.RequireUserId(), fullWorthSpaceId, id, itemId, productId, ct)));
        group.MapPost("/{id:guid}/items/{itemId:guid}/unlink-product", async (Guid id, Guid itemId, Guid fullWorthSpaceId, FullWorth.Backend.Security.CurrentUserContext user, PurchaseWorkspaceService service, CancellationToken ct) => Mutation(await service.UnlinkItemProductAsync(user.RequireUserId(), fullWorthSpaceId, id, itemId, ct)));
        group.MapGet("/{id:guid}/payments", async (Guid id, Guid fullWorthSpaceId, FullWorth.Backend.Security.CurrentUserContext user, PurchaseWorkspaceService service, CancellationToken ct) => { var value = await service.PaymentsAsync(user.RequireUserId(), fullWorthSpaceId, id, ct); return value is null ? Results.NotFound() : Results.Ok(value); });
        group.MapGet("/{id:guid}/payment-candidates", async (Guid id, Guid fullWorthSpaceId, FullWorth.Backend.Security.CurrentUserContext user, PurchaseWorkspaceService service, CancellationToken ct) => { var value = await service.PaymentCandidatesAsync(user.RequireUserId(), fullWorthSpaceId, id, ct); return value is null ? Results.NotFound() : Results.Ok(value); });
        group.MapPost("/{id:guid}/payments", async (Guid id, Guid fullWorthSpaceId, PurchasePaymentWrite request, FullWorth.Backend.Security.CurrentUserContext user, PurchaseWorkspaceService service, CancellationToken ct) => Outcome(await service.AddPaymentAsync(user.RequireUserId(), fullWorthSpaceId, id, request, ct), true));
        group.MapPatch("/{id:guid}/payments/{linkId:guid}", async (Guid id, Guid linkId, Guid fullWorthSpaceId, PurchasePaymentPatch request, FullWorth.Backend.Security.CurrentUserContext user, PurchaseWorkspaceService service, CancellationToken ct) => Outcome(await service.UpdatePaymentAsync(user.RequireUserId(), fullWorthSpaceId, id, linkId, request, ct)));
        group.MapDelete("/{id:guid}/payments/{linkId:guid}", async (Guid id, Guid linkId, Guid fullWorthSpaceId, FullWorth.Backend.Security.CurrentUserContext user, PurchaseWorkspaceService service, CancellationToken ct) => Mutation(await service.DeletePaymentAsync(user.RequireUserId(), fullWorthSpaceId, id, linkId, ct)));
        group.MapPost("/{id:guid}/payments/auto-link", async (Guid id, Guid fullWorthSpaceId, FullWorth.Backend.Security.CurrentUserContext user, PurchaseWorkspaceService service, CancellationToken ct) => { var value = await service.AutoLinkAsync(user.RequireUserId(), fullWorthSpaceId, id, ct); return value.Result == PurchaseMutationResult.NotFound ? Results.NotFound() : Results.Ok(new { value.Linked }); });
        group.MapPost("/{id:guid}/reconciliation/accept-difference", async (Guid id, Guid fullWorthSpaceId, DifferenceAcceptanceWrite request, FullWorth.Backend.Security.CurrentUserContext user, PurchaseWorkspaceService service, CancellationToken ct) => Outcome(await service.AcceptDifferenceAsync(user.RequireUserId(), fullWorthSpaceId, id, request, ct), true));
        group.MapDelete("/{id:guid}/reconciliation/accept-difference/{kind}", async (Guid id, string kind, Guid fullWorthSpaceId, FullWorth.Backend.Security.CurrentUserContext user, PurchaseWorkspaceService service, CancellationToken ct) => Mutation(await service.ClearDifferenceAsync(user.RequireUserId(), fullWorthSpaceId, id, kind, ct)));
        group.MapPut("/{id:guid}/visibility", async (Guid id, Guid fullWorthSpaceId, PurchaseVisibilityWrite request, FullWorth.Backend.Security.CurrentUserContext user, PurchaseWorkspaceService service, CancellationToken ct) => Mutation(await service.SetVisibilityAsync(user.RequireUserId(), fullWorthSpaceId, id, request, ct)));
        group.MapPost("/{id:guid}/items/{itemId:guid}/returns", async (Guid id, Guid itemId, Guid fullWorthSpaceId, PurchaseReturnWrite request, FullWorth.Backend.Security.CurrentUserContext user, PurchaseWorkspaceService service, CancellationToken ct) => Outcome(await service.RecordReturnAsync(user.RequireUserId(), fullWorthSpaceId, id, itemId, request, ct), true));

        app.MapPost("/api/transactions/{transactionId:guid}/allocations/from-purchase/{purchaseId:guid}", async (Guid transactionId, Guid purchaseId, Guid fullWorthSpaceId, PurchaseAllocationImportRequest request, FullWorth.Backend.Security.CurrentUserContext user, PurchaseWorkspaceService service, CancellationToken ct) => Outcome(await service.ImportAllocationsFromPurchaseAsync(user.RequireUserId(), fullWorthSpaceId, transactionId, purchaseId, request, ct)));
        app.MapPost("/api/transactions/{transactionId:guid}/allocations/clear", async (Guid transactionId, Guid fullWorthSpaceId, FullWorth.Backend.Security.CurrentUserContext user, PurchaseWorkspaceService service, CancellationToken ct) => Mutation(await service.ClearAllocationsAsync(user.RequireUserId(), fullWorthSpaceId, transactionId, ct)));
        return app;
    }

    private static IResult Mutation(PurchaseMutationResult result) => result switch
    {
        PurchaseMutationResult.Success => Results.NoContent(), PurchaseMutationResult.Invalid => Results.BadRequest(),
        PurchaseMutationResult.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden), _ => Results.NotFound()
    };
    private static IResult Outcome((PurchaseMutationResult Result, object? Value, string? Error) outcome, bool created = false) => outcome.Result switch
    {
        PurchaseMutationResult.Success when created => Results.Created(string.Empty, outcome.Value), PurchaseMutationResult.Success => Results.Ok(outcome.Value),
        PurchaseMutationResult.Invalid when outcome.Value is not null => Results.Conflict(new { error = outcome.Error, detail = outcome.Value }),
        PurchaseMutationResult.Invalid => Results.BadRequest(new { error = outcome.Error }), PurchaseMutationResult.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden), _ => Results.NotFound()
    };
}
