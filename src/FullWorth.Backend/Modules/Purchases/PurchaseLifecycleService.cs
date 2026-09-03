using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.Transactions;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Purchases;

public sealed record PurchaseDraftWrite(
    string Merchant,
    DateOnly? PurchaseDate,
    TimeOnly? PurchaseTime,
    decimal TotalAmount,
    string Currency,
    string Source = "manual",
    string? Notes = null,
    string Visibility = "space",
    Guid? PaidByUserId = null,
    Guid? ForWhomUserId = null,
    string? ReceiptNumber = null,
    string? InvoiceNumber = null,
    string? PaymentMethodText = null);

public sealed record PurchaseSummaryPatch(
    string Merchant,
    DateOnly? PurchaseDate,
    TimeOnly? PurchaseTime,
    decimal TotalAmount,
    string Currency,
    string? Notes,
    string? ReceiptNumber,
    string? InvoiceNumber,
    string? PaymentMethodText,
    decimal? SubtotalAmount,
    decimal? DiscountAmount,
    decimal? DepositAmount,
    decimal? TaxAmount,
    decimal? TipAmount,
    decimal? ShippingAmount,
    decimal? FeeAmount,
    decimal? RoundingAmount = null);

public sealed record ConfirmPurchaseRequest(bool CreateSafeAllocations = true, bool AllowUnlinked = true);
public sealed record BookmarkPurchaseRequest(bool Bookmarked);

public sealed class PurchaseLifecycleService(FullWorthDbContext db)
{
    public async Task<(PurchaseMutationResult Result, object? Value, string? Error)> CreateAsync(Guid userId, Guid fullWorthSpaceId, PurchaseDraftWrite request, CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, fullWorthSpaceId, ct)) return (PurchaseMutationResult.NotFound, null, null);
        var error = ValidateSummary(request.Merchant, request.TotalAmount, request.Currency, request.Visibility);
        if (error is not null) return (PurchaseMutationResult.Invalid, null, error);
        var entity = new Purchase
        {
            FullWorthSpaceId = fullWorthSpaceId,
            Source = string.IsNullOrWhiteSpace(request.Source) ? "manual" : request.Source.Trim().ToLowerInvariant(),
            Merchant = request.Merchant.Trim(), PurchaseDate = request.PurchaseDate, PurchaseTime = request.PurchaseTime,
            TotalAmount = request.TotalAmount, Currency = request.Currency.Trim().ToUpperInvariant(), Status = "review", ReviewState = "needs_review",
            Notes = Clean(request.Notes), Visibility = NormalizeVisibility(request.Visibility), CreatedByUserId = userId,
            PaidByUserId = request.PaidByUserId, ForWhomUserId = request.ForWhomUserId,
            ReceiptNumber = Clean(request.ReceiptNumber), InvoiceNumber = Clean(request.InvoiceNumber), PaymentMethodText = Clean(request.PaymentMethodText)
        };
        db.Purchases.Add(entity);
        await db.SaveChangesAsync(ct);
        return (PurchaseMutationResult.Success, Dto(entity), null);
    }

    public async Task<(PurchaseMutationResult Result, object? Value, string? Error)> UpdateSummaryAsync(Guid userId, Guid fullWorthSpaceId, Guid id, PurchaseSummaryPatch request, CancellationToken ct)
    {
        var entity = await Writable(userId, fullWorthSpaceId).SingleOrDefaultAsync(x => x.Id == id, ct);
        if (entity is null) return await Visible(userId, fullWorthSpaceId).AnyAsync(x => x.Id == id, ct)
            ? (PurchaseMutationResult.Forbidden, null, null) : (PurchaseMutationResult.NotFound, null, null);
        var error = ValidateSummary(request.Merchant, request.TotalAmount, request.Currency, entity.Visibility);
        if (error is not null) return (PurchaseMutationResult.Invalid, null, error);

        var currency = request.Currency.Trim().ToUpperInvariant();
        var canonicalDiscounts = await db.Set<PurchaseDiscount>().Where(x => x.PurchaseId == id).ToListAsync(ct);
        var canonicalDiscountTotal = PurchaseArticleCalculator.RoundMoney(canonicalDiscounts.Sum(x => x.Amount), currency);
        if (canonicalDiscounts.Count > 0 && request.DiscountAmount.HasValue &&
            Math.Abs(request.DiscountAmount.Value - canonicalDiscountTotal) > PurchaseArticleCalculator.Tolerance(currency))
            return (PurchaseMutationResult.Invalid, null, "Edit discounts through the purchase discount editor; the summary discount is a derived mirror.");

        // Backwards compatibility for older clients that only know the aggregate DiscountAmount field.
        // Convert it once into a basket-level manual source row rather than maintaining two truths.
        if (canonicalDiscounts.Count == 0 && request.DiscountAmount is > 0m)
        {
            var legacy = new PurchaseDiscount
            {
                PurchaseId = id,
                Type = "other",
                Label = "Manual discount",
                Amount = request.DiscountAmount.Value,
                Source = "manual",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            db.Add(legacy);
            canonicalDiscounts.Add(legacy);
            canonicalDiscountTotal = PurchaseArticleCalculator.RoundMoney(request.DiscountAmount.Value, currency);
        }

        var rounding = request.RoundingAmount ?? entity.RoundingAmount;
        var financialChanged = entity.TotalAmount != request.TotalAmount ||
            !string.Equals(entity.Currency, currency, StringComparison.OrdinalIgnoreCase) ||
            entity.SubtotalAmount != request.SubtotalAmount || entity.DiscountAmount != canonicalDiscountTotal ||
            entity.DepositAmount != request.DepositAmount || entity.RoundingAmount != rounding || entity.TaxAmount != request.TaxAmount ||
            entity.TipAmount != request.TipAmount || entity.ShippingAmount != request.ShippingAmount || entity.FeeAmount != request.FeeAmount;

        entity.Merchant = request.Merchant.Trim(); entity.PurchaseDate = request.PurchaseDate; entity.PurchaseTime = request.PurchaseTime;
        entity.TotalAmount = request.TotalAmount; entity.Currency = currency; entity.Notes = Clean(request.Notes);
        entity.ReceiptNumber = Clean(request.ReceiptNumber); entity.InvoiceNumber = Clean(request.InvoiceNumber); entity.PaymentMethodText = Clean(request.PaymentMethodText);
        entity.SubtotalAmount = request.SubtotalAmount; entity.DiscountAmount = canonicalDiscounts.Count > 0 ? canonicalDiscountTotal : request.DiscountAmount;
        entity.DepositAmount = request.DepositAmount; entity.RoundingAmount = rounding;
        entity.TaxAmount = request.TaxAmount; entity.TipAmount = request.TipAmount; entity.ShippingAmount = request.ShippingAmount; entity.FeeAmount = request.FeeAmount;
        if (financialChanged && (entity.Status == "confirmed" || entity.ReviewState == "confirmed"))
        {
            entity.Status = "review";
            entity.ReviewState = "needs_review";
            var accepted = await db.Set<PurchaseDifferenceAcceptance>().Where(x => x.PurchaseId == id).ToListAsync(ct);
            db.RemoveRange(accepted);
        }
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return (PurchaseMutationResult.Success, Dto(entity), null);
    }

    public async Task<(PurchaseMutationResult Result, object? Value, string? Error)> ConfirmAsync(Guid userId, Guid fullWorthSpaceId, Guid id, ConfirmPurchaseRequest request, CancellationToken ct)
    {
        var purchase = await Writable(userId, fullWorthSpaceId)
            .Include(x => x.Items)
            .Include(x => x.PaymentLinks)
            .Include(x => x.Discounts)
            .SingleOrDefaultAsync(x => x.Id == id, ct);
        if (purchase is null) return await Visible(userId, fullWorthSpaceId).AnyAsync(x => x.Id == id, ct)
            ? (PurchaseMutationResult.Forbidden, null, null) : (PurchaseMutationResult.NotFound, null, null);
        if (string.IsNullOrWhiteSpace(purchase.Merchant) || string.IsNullOrWhiteSpace(purchase.Currency))
            return (PurchaseMutationResult.Invalid, null, "Merchant and currency are required.");

        if (purchase.PaymentLinks.Count == 0 && purchase.TransactionId.HasValue)
        {
            // Compatibility with captures made through the legacy linker after the migration was applied.
            var tx = await db.Transactions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == purchase.TransactionId.Value, ct);
            if (tx is not null)
            {
                purchase.PaymentLinks.Add(new PurchasePaymentLink
                {
                    FullWorthSpaceId = fullWorthSpaceId, PurchaseId = purchase.Id, TransactionId = tx.Id,
                    Amount = Math.Min(Math.Abs(tx.Amount), Math.Abs(purchase.TotalAmount)), Currency = tx.Currency,
                    LinkSource = "legacy", Confidence = purchase.MatchConfidence, CreatedByUserId = userId
                });
                await db.SaveChangesAsync(ct);
            }
        }

        if (!request.AllowUnlinked && purchase.PaymentLinks.Count == 0 && !purchase.TransactionId.HasValue)
            return (PurchaseMutationResult.Invalid, null, "Link a payment or allow an unlinked/cash purchase before confirming.");

        var foreignCurrencyPayments = purchase.PaymentLinks
            .Where(x => !string.Equals(x.Currency, purchase.Currency, StringComparison.OrdinalIgnoreCase))
            .Select(x => new { x.Id, x.TransactionId, x.Amount, x.Currency })
            .ToList();
        if (foreignCurrencyPayments.Count > 0)
            return (PurchaseMutationResult.Invalid,
                new { conflict = "foreign_currency_payment", purchaseCurrency = purchase.Currency, payments = foreignCurrencyPayments },
                "Foreign-currency payments require an explicit exchange-rate conversion before this purchase can be confirmed.");

        foreach (var transactionId in purchase.PaymentLinks.Select(x => x.TransactionId).Distinct())
        {
            var transaction = await OwnedTransactions(userId, fullWorthSpaceId).AsNoTracking()
                .Where(x => x.Id == transactionId)
                .Select(x => new { x.Id, x.Amount, x.Currency })
                .SingleOrDefaultAsync(ct);
            if (transaction is null)
                return (PurchaseMutationResult.Invalid, new { conflict = "payment_transaction_missing", transactionId }, "A linked payment transaction is no longer available.");
            if (transaction.Amount >= 0m)
                return (PurchaseMutationResult.Invalid, new { conflict = "payment_direction", transactionId, transaction.Amount }, "A purchase payment must reference an expense transaction.");

            var allocatedAmount = await db.Set<PurchasePaymentLink>().AsNoTracking()
                .Where(x => x.TransactionId == transactionId)
                .SumAsync(x => (decimal?)x.Amount, ct) ?? 0m;
            var transactionAmount = Math.Abs(transaction.Amount);
            if (allocatedAmount - transactionAmount > PurchaseArticleCalculator.Tolerance(transaction.Currency))
                return (PurchaseMutationResult.Invalid,
                    new { conflict = "payment_overallocated", transactionId, transactionAmount, allocatedAmount, transaction.Currency },
                    "Purchases linked to this transaction allocate more than the transaction amount. Reduce or remove one of the payment allocations before confirming.");
        }

        var rec = PurchaseArticleCalculator.Reconcile(
            purchase.TotalAmount, purchase.Items, purchase.Discounts, purchase.PaymentLinks, purchase.Currency,
            purchase.SubtotalAmount, purchase.DiscountAmount, purchase.DepositAmount, purchase.RoundingAmount,
            purchase.TipAmount, purchase.ShippingAmount, purchase.FeeAmount);
        var acceptedItems = await db.Set<PurchaseDifferenceAcceptance>().AsNoTracking().AnyAsync(
            x => x.PurchaseId == purchase.Id && x.Kind == "items" && Math.Abs(x.Amount - rec.ItemDifference) <= rec.Tolerance, ct);
        var acceptedPayments = await db.Set<PurchaseDifferenceAcceptance>().AsNoTracking().AnyAsync(
            x => x.PurchaseId == purchase.Id && x.Kind == "payments" && Math.Abs(x.Amount - rec.PaymentDifference) <= rec.Tolerance, ct);
        if (purchase.Items.Count > 0 && !rec.ItemsReconciled && !acceptedItems)
            return (PurchaseMutationResult.Invalid, new { conflict = "item_difference", reconciliation = rec }, "Resolve or explicitly accept the item difference before confirming.");
        if (purchase.SubtotalAmount.HasValue && !rec.FormulaReconciled)
            return (PurchaseMutationResult.Invalid, new { conflict = "receipt_formula_difference", reconciliation = rec }, "Subtotal, discounts, deposit and rounding do not reconcile to the receipt total.");
        if (purchase.PaymentLinks.Count > 0 && !rec.PaymentsReconciled && !acceptedPayments)
            return (PurchaseMutationResult.Invalid, new { conflict = "payment_difference", reconciliation = rec }, "Resolve or explicitly accept the payment difference before confirming.");

        object? allocationResult = null;
        if (request.CreateSafeAllocations && purchase.Items.Count > 0 && purchase.PaymentLinks.Count == 1)
        {
            var link = purchase.PaymentLinks.Single();
            var tx = await OwnedTransactions(userId, fullWorthSpaceId).SingleOrDefaultAsync(x => x.Id == link.TransactionId, ct);
            if (tx is not null && !tx.IsTransfer && string.Equals(tx.Currency, purchase.Currency, StringComparison.OrdinalIgnoreCase) &&
                Math.Abs(Math.Abs(tx.Amount) - Math.Abs(purchase.TotalAmount)) <= PurchaseArticleCalculator.Tolerance(purchase.Currency) && rec.ItemsReconciled)
            {
                var existing = await db.TransactionAllocations.Where(x => x.TransactionId == tx.Id).ToListAsync(ct);
                var existingIds = existing.Select(x => x.Id).ToArray();
                var provenance = existingIds.Length == 0
                    ? []
                    : await db.Set<PurchaseAllocationLink>().AsNoTracking()
                        .Where(x => existingIds.Contains(x.TransactionAllocationId))
                        .ToListAsync(ct);
                var provenanceByAllocation = provenance.ToDictionary(x => x.TransactionAllocationId);
                // An allocation belongs to this purchase — and may therefore be rebuilt — when it either
                // carries provenance for this purchase or is tied to one of this purchase's own items.
                // Everything else (manual splits, other purchases) is preserved.
                var purchaseItemIds = purchase.Items.Select(item => item.Id).ToHashSet();
                var foreignOrManual = existing.Where(x =>
                    (!provenanceByAllocation.TryGetValue(x.Id, out var p) || p.PurchaseId != purchase.Id) &&
                    !(x.PurchaseItemId.HasValue && purchaseItemIds.Contains(x.PurchaseItemId.Value))).ToList();
                if (foreignOrManual.Count > 0)
                    return (PurchaseMutationResult.Invalid,
                        new { conflict = "existing_allocations", transactionId = tx.Id, existing = foreignOrManual },
                        "The linked transaction contains manual or unrelated allocations. Resolve that split before confirming.");

                var now = DateTimeOffset.UtcNow;
                var built = PurchaseAllocationBuilder.Build(purchase, tx, rec, now);
                var sum = PurchaseArticleCalculator.RoundMoney(built.Allocations.Sum(x => x.Amount), tx.Currency);
                if (Math.Abs(sum - tx.Amount) > PurchaseArticleCalculator.Tolerance(tx.Currency))
                    return (PurchaseMutationResult.Invalid,
                        new { conflict = "allocation_build_mismatch", transactionId = tx.Id, transactionAmount = tx.Amount, allocationTotal = sum, reconciliation = rec },
                        "The purchase breakdown cannot be converted into a balanced transaction split. Review discounts, deposit and rounding.");

                if (existing.Count > 0) db.TransactionAllocations.RemoveRange(existing);
                db.TransactionAllocations.AddRange(built.Allocations);
                db.Set<PurchaseAllocationLink>().AddRange(built.Links);
                allocationResult = new { transactionId = tx.Id, created = built.Allocations.Count, replaced = existing.Count };
            }
        }

        purchase.Status = "confirmed";
        purchase.ReviewState = "confirmed";
        purchase.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return (PurchaseMutationResult.Success, new { purchase = Dto(purchase), reconciliation = rec, allocationResult }, null);
    }

    public async Task<PurchaseMutationResult> SetBookmarkAsync(Guid userId, Guid fullWorthSpaceId, Guid id, bool value, CancellationToken ct)
    {
        var purchase = await Writable(userId, fullWorthSpaceId).SingleOrDefaultAsync(x => x.Id == id, ct);
        if (purchase is null) return await Visible(userId, fullWorthSpaceId).AnyAsync(x => x.Id == id, ct) ? PurchaseMutationResult.Forbidden : PurchaseMutationResult.NotFound;
        purchase.IsBookmarked = value; purchase.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return PurchaseMutationResult.Success;
    }

    private IQueryable<Purchase> Visible(Guid userId, Guid fullWorthSpaceId) => db.Purchases.Where(p =>
        p.FullWorthSpaceId == fullWorthSpaceId && db.FullWorthSpaceMembers.Any(m => m.FullWorthSpaceId == fullWorthSpaceId && m.UserId == userId) &&
        (p.Visibility != "private" || p.CreatedByUserId == userId) &&
        (!p.PaymentLinks.Any() || p.PaymentLinks.Any(link => db.Transactions.Any(tx => tx.Id == link.TransactionId && db.Accounts.Any(a => a.Id == tx.AccountId && a.Owners.Any(o => o.UserId == userId))))) &&
        (p.TransactionId == null || db.Transactions.Any(tx => tx.Id == p.TransactionId && db.Accounts.Any(a => a.Id == tx.AccountId && a.Owners.Any(o => o.UserId == userId)))));
    private IQueryable<Purchase> Writable(Guid userId, Guid fullWorthSpaceId) => db.Purchases.Where(p =>
        p.FullWorthSpaceId == fullWorthSpaceId && db.FullWorthSpaceMembers.Any(m => m.FullWorthSpaceId == fullWorthSpaceId && m.UserId == userId) &&
        (p.Visibility != "private" || p.CreatedByUserId == userId) &&
        (!p.PaymentLinks.Any() || p.PaymentLinks.All(link => db.Transactions.Any(tx => tx.Id == link.TransactionId && db.Accounts.Any(a => a.Id == tx.AccountId && a.Owners.Any(o => o.UserId == userId && o.OwnershipType == AccountOwnershipTypes.Owner))))) &&
        (p.TransactionId == null || db.Transactions.Any(tx => tx.Id == p.TransactionId && db.Accounts.Any(a => a.Id == tx.AccountId && a.Owners.Any(o => o.UserId == userId && o.OwnershipType == AccountOwnershipTypes.Owner)))));
    private IQueryable<FinanceTransaction> OwnedTransactions(Guid userId, Guid fullWorthSpaceId) => db.Transactions.Where(tx => db.Accounts.Any(a => a.Id == tx.AccountId && a.FullWorthSpaceId == fullWorthSpaceId && a.Owners.Any(o => o.UserId == userId && o.OwnershipType == AccountOwnershipTypes.Owner)));
    private Task<bool> IsMemberAsync(Guid userId, Guid fullWorthSpaceId, CancellationToken ct) => db.FullWorthSpaceMembers.AsNoTracking().AnyAsync(x => x.UserId == userId && x.FullWorthSpaceId == fullWorthSpaceId, ct);
    private static string? ValidateSummary(string merchant, decimal amount, string currency, string visibility)
    {
        if (string.IsNullOrWhiteSpace(merchant)) return "Merchant is required.";
        if (string.IsNullOrWhiteSpace(currency) || currency.Trim().Length != 3 || currency.Trim().ToUpperInvariant().Any(c => c is < 'A' or > 'Z')) return "Currency is invalid.";
        if (amount < 0m) return "Purchase total must not be negative.";
        if (NormalizeVisibility(visibility) is not "space" and not "private") return "Visibility is invalid.";
        return null;
    }
    private static string NormalizeVisibility(string? value) => string.Equals(value?.Trim(), "private", StringComparison.OrdinalIgnoreCase) ? "private" : "space";
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static object Dto(Purchase p) => new
    {
        p.Id, p.FullWorthSpaceId, p.Merchant, p.PurchaseDate, p.PurchaseTime, p.SubtotalAmount, p.DiscountAmount,
        p.DepositAmount, p.RoundingAmount, p.TotalAmount, p.Currency, p.Source, p.Status, p.ReviewState, p.Visibility,
        p.IsBookmarked, p.CreatedByUserId, p.UpdatedAt
    };
}

public static class PurchaseLifecycleEndpoints
{
    public static IEndpointRouteBuilder MapPurchaseLifecycleEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/purchases").WithTags("Purchases");
        group.MapPost("/manual", async (Guid fullWorthSpaceId, PurchaseDraftWrite request, FullWorth.Backend.Security.CurrentUserContext user, PurchaseLifecycleService service, CancellationToken ct) => Outcome(await service.CreateAsync(user.RequireUserId(), fullWorthSpaceId, request, ct), true));
        group.MapPatch("/{id:guid}/summary", async (Guid id, Guid fullWorthSpaceId, PurchaseSummaryPatch request, FullWorth.Backend.Security.CurrentUserContext user, PurchaseLifecycleService service, CancellationToken ct) => Outcome(await service.UpdateSummaryAsync(user.RequireUserId(), fullWorthSpaceId, id, request, ct)));
        group.MapPost("/{id:guid}/confirm", async (Guid id, Guid fullWorthSpaceId, ConfirmPurchaseRequest request, FullWorth.Backend.Security.CurrentUserContext user, PurchaseLifecycleService service, CancellationToken ct) => Outcome(await service.ConfirmAsync(user.RequireUserId(), fullWorthSpaceId, id, request, ct)));
        group.MapPut("/{id:guid}/bookmark", async (Guid id, Guid fullWorthSpaceId, BookmarkPurchaseRequest request, FullWorth.Backend.Security.CurrentUserContext user, PurchaseLifecycleService service, CancellationToken ct) => Mutation(await service.SetBookmarkAsync(user.RequireUserId(), fullWorthSpaceId, id, request.Bookmarked, ct)));
        return app;
    }
    private static IResult Mutation(PurchaseMutationResult result) => result switch
    { PurchaseMutationResult.Success => Results.NoContent(), PurchaseMutationResult.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden), PurchaseMutationResult.Invalid => Results.BadRequest(), _ => Results.NotFound() };
    private static IResult Outcome((PurchaseMutationResult Result, object? Value, string? Error) outcome, bool created = false) => outcome.Result switch
    {
        PurchaseMutationResult.Success when created => Results.Created(string.Empty, outcome.Value), PurchaseMutationResult.Success => Results.Ok(outcome.Value),
        PurchaseMutationResult.Invalid when outcome.Value is not null => Results.Conflict(new { error = outcome.Error, detail = outcome.Value }),
        PurchaseMutationResult.Invalid => Results.BadRequest(new { error = outcome.Error }), PurchaseMutationResult.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden), _ => Results.NotFound()
    };
}
