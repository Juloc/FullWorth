using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.Purchases;
using FullWorth.Backend.Security;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Transactions;

public sealed class FinanceTransaction
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AccountId { get; set; }
    public Guid? CategoryId { get; set; }
    public string ExternalKey { get; set; } = string.Empty;
    public string? ProviderTransactionId { get; set; }
    public string Status { get; set; } = "BOOK";
    public DateOnly? BookingDate { get; set; }
    public DateOnly? ValueDate { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "EUR";
    public string? Counterparty { get; set; }
    public string? NormalizedCounterparty { get; set; }
    public string? Description { get; set; }
    public string? MerchantCategoryCode { get; set; }
    public string? EntryReference { get; set; }
    public string? UserNote { get; set; }
    public bool IsIgnored { get; set; }
    public bool IsTransfer { get; set; }
    public Guid? TransferGroupId { get; set; }
    public string? TransferPurpose { get; set; }
    public Guid? RefundOfTransactionId { get; set; }
    public Guid? RefundCategoryId { get; set; }
    public string CategorizationSource { get; set; } = "none";
    public string RawJson { get; set; } = "{}";
    public DateTimeOffset FirstSeenAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

// A split allocation line on a transaction. Amount uses the ledger sign convention and all lines NET
// to the parent transaction amount. Most expense lines are negative; positive lines are valid explicit
// adjustments such as coupons/discounts. PurchaseItemId makes a generic split a concrete article split.
public sealed class TransactionAllocation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TransactionId { get; set; }
    public Guid? CategoryId { get; set; }
    public decimal Amount { get; set; }
    public string? Note { get; set; }
    public Guid? PurchaseItemId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum TransactionClassificationResult { Updated, NotFound, InvalidCategory }
public enum AllocationResult { Updated, NotFound, InvalidCategory, InvalidPurchaseItem, Unbalanced }
public enum RefundLinkResult { Updated, NotFound, Invalid }
public enum TransactionCreateResult { Created, NotFound, Forbidden, NotManual, InvalidCategory }
public enum TransactionDeleteResult { Deleted, NotFound, NotManual, Referenced }
public enum TransferLinkResult { Linked, NotFound, Invalid }
public enum TransferUnlinkResult { Unlinked, NotFound, NotLinked }

public sealed class TransactionStore(FullWorthDbContext db)
{
    public async Task<object> SearchForUserAsync(Guid userId, Guid? fullWorthSpaceId, TransactionQuery request, CancellationToken ct)
    {
        var q = AccessibleTransactions(userId, fullWorthSpaceId, requireOwner: false);
        if (request.AccountId.HasValue) q = q.Where(x => x.AccountId == request.AccountId.Value);
        if (request.CategoryId.HasValue)
        {
            var categoryId = request.CategoryId.Value;
            q = q.Where(x =>
                x.CategoryId == categoryId ||
                db.TransactionAllocations.Any(a => a.TransactionId == x.Id && a.CategoryId == categoryId) ||
                db.Purchases.Any(p =>
                    (p.TransactionId == x.Id || p.PaymentLinks.Any(link => link.TransactionId == x.Id)) &&
                    (p.Visibility != "private" || p.CreatedByUserId == userId) &&
                    p.Items.Any(i => i.CategoryId == categoryId)));
        }
        if (request.From.HasValue) q = q.Where(x => x.BookingDate >= request.From.Value);
        if (request.To.HasValue) q = q.Where(x => x.BookingDate <= request.To.Value);
        if (request.Direction == "income") q = q.Where(x => x.Amount > 0);
        if (request.Direction == "expense") q = q.Where(x => x.Amount < 0);
        if (request.IncludeIgnored != true) q = q.Where(x => !x.IsIgnored);
        if (request.TransfersOnly == true) q = q.Where(x => x.IsTransfer);
        if (!string.IsNullOrWhiteSpace(request.Query))
        {
            var pattern = $"%{request.Query.Trim()}%";
            q = q.Where(x =>
                (x.Counterparty != null && EF.Functions.ILike(x.Counterparty, pattern)) ||
                (x.NormalizedCounterparty != null && EF.Functions.ILike(x.NormalizedCounterparty, pattern)) ||
                (x.Description != null && EF.Functions.ILike(x.Description, pattern)) ||
                (x.UserNote != null && EF.Functions.ILike(x.UserNote, pattern)) ||
                db.Purchases.Any(p =>
                    (p.TransactionId == x.Id || p.PaymentLinks.Any(link => link.TransactionId == x.Id)) &&
                    (p.Visibility != "private" || p.CreatedByUserId == userId) &&
                    (EF.Functions.ILike(p.Merchant, pattern) || p.Items.Any(i => EF.Functions.ILike(i.Name, pattern)))));
        }

        var descending = !string.Equals(request.Order, "asc", StringComparison.OrdinalIgnoreCase);
        q = request.Sort?.ToLowerInvariant() switch
        {
            "amount" => descending ? q.OrderByDescending(x => x.Amount) : q.OrderBy(x => x.Amount),
            "counterparty" => descending ? q.OrderByDescending(x => x.Counterparty) : q.OrderBy(x => x.Counterparty),
            _ => descending ? q.OrderByDescending(x => x.BookingDate).ThenByDescending(x => x.UpdatedAt) : q.OrderBy(x => x.BookingDate).ThenBy(x => x.UpdatedAt)
        };

        var offset = Math.Max(0, request.Offset ?? 0);
        var limit = Math.Clamp(request.Limit ?? 200, 1, 5000);
        var total = await q.CountAsync(ct);
        var items = await q.Skip(offset).Take(limit).Select(x => new
        {
            x.Id,
            x.AccountId,
            Account = db.Accounts.Where(a => a.Id == x.AccountId).Select(a => a.DisplayName).FirstOrDefault(),
            x.BookingDate,
            x.ValueDate,
            x.Amount,
            x.Currency,
            x.Counterparty,
            x.NormalizedCounterparty,
            x.Description,
            x.MerchantCategoryCode,
            x.Status,
            x.CategoryId,
            Category = db.Categories
                .Where(c => c.Id == x.CategoryId && db.Accounts.Any(a => a.Id == x.AccountId && a.FullWorthSpaceId == c.FullWorthSpaceId))
                .Select(c => c.Name)
                .FirstOrDefault(),
            x.UserNote,
            x.IsIgnored,
            x.IsTransfer,
            x.CategorizationSource,
            x.UpdatedAt,
            PurchaseCount = db.Purchases.Count(p =>
                (p.TransactionId == x.Id || p.PaymentLinks.Any(link => link.TransactionId == x.Id)) &&
                (p.Visibility != "private" || p.CreatedByUserId == userId)),
            PurchaseItemCount = db.Purchases
                .Where(p =>
                    (p.TransactionId == x.Id || p.PaymentLinks.Any(link => link.TransactionId == x.Id)) &&
                    (p.Visibility != "private" || p.CreatedByUserId == userId))
                .SelectMany(p => p.Items).Count()
        }).ToListAsync(ct);
        return new { total, offset, limit, items };
    }

    public async Task<object?> GetForUserAsync(Guid userId, Guid fullWorthSpaceId, Guid id, CancellationToken ct)
    {
        var tx = await AccessibleTransactions(userId, fullWorthSpaceId, requireOwner: false)
            .Where(x => x.Id == id)
            .Select(x => new
            {
                x.Id,
                x.AccountId,
                Account = db.Accounts.Where(a => a.Id == x.AccountId).Select(a => a.DisplayName).FirstOrDefault(),
                x.BookingDate,
                x.ValueDate,
                x.Amount,
                x.Currency,
                x.Counterparty,
                x.NormalizedCounterparty,
                x.Description,
                x.MerchantCategoryCode,
                x.EntryReference,
                x.Status,
                x.CategoryId,
                Category = db.Categories
                    .Where(c => c.Id == x.CategoryId && db.Accounts.Any(a => a.Id == x.AccountId && a.FullWorthSpaceId == c.FullWorthSpaceId))
                    .Select(c => c.Name)
                    .FirstOrDefault(),
                x.UserNote,
                x.IsIgnored,
                x.IsTransfer,
                x.TransferPurpose,
                x.TransferGroupId,
                x.RefundOfTransactionId,
                x.RefundCategoryId,
                x.CategorizationSource,
                IsManual = x.ExternalKey.StartsWith("manual:"),
                x.FirstSeenAt,
                x.UpdatedAt
            }).SingleOrDefaultAsync(ct);
        if (tx is null) return null;

        // Only expose the transfer counterpart when the caller can actually see its account. Otherwise a
        // shared transfer would leak a transaction from a hidden/other-owner account.
        object? counterpart = tx.TransferGroupId is { } groupId
            ? await AccessibleTransactions(userId, fullWorthSpaceId, requireOwner: false)
                .Where(x => x.TransferGroupId == groupId && x.Id != id)
                .Select(x => new { x.Id, x.AccountId, Account = db.Accounts.Where(a => a.Id == x.AccountId).Select(a => a.DisplayName).FirstOrDefault(), x.Amount, x.Currency, x.BookingDate })
                .FirstOrDefaultAsync(ct)
            : null;

        var purchases = await db.Purchases.AsNoTracking()
            .Where(x =>
                x.FullWorthSpaceId == fullWorthSpaceId &&
                (x.TransactionId == id || x.PaymentLinks.Any(link => link.TransactionId == id)) &&
                (x.Visibility != "private" || x.CreatedByUserId == userId))
            .Include(x => x.Items)
            .OrderBy(x => x.PurchaseDate)
            .ThenBy(x => x.CreatedAt)
            .ToListAsync(ct);
        return new { transaction = tx, transferCounterpart = counterpart, purchases };
    }

    public Task<string?> GetOwnershipForUserAsync(Guid userId, Guid fullWorthSpaceId, Guid transactionId, CancellationToken ct) =>
        db.Transactions.AsNoTracking()
            .Where(transaction => transaction.Id == transactionId)
            .Join(db.Accounts.AsNoTracking(), transaction => transaction.AccountId, account => account.Id, (transaction, account) => new { transaction, account })
            .Where(x => x.account.FullWorthSpaceId == fullWorthSpaceId &&
                        db.FullWorthSpaceMembers.Any(member => member.FullWorthSpaceId == fullWorthSpaceId && member.UserId == userId))
            .Join(db.AccountOwners.AsNoTracking().Where(owner => owner.UserId == userId),
                x => x.account.Id,
                owner => owner.AccountId,
                (x, owner) => owner.OwnershipType)
            .SingleOrDefaultAsync(ct);

    public async Task<TransactionClassificationResult> ClassifyForOwnerAsync(Guid userId, Guid fullWorthSpaceId, Guid id, TransactionClassification request, CancellationToken ct)
    {
        var entity = await AccessibleTransactions(userId, fullWorthSpaceId, requireOwner: true).SingleOrDefaultAsync(x => x.Id == id, ct);
        if (entity is null) return TransactionClassificationResult.NotFound;
        if (request.CategoryId.HasValue && !await db.Categories.AsNoTracking().AnyAsync(x => x.Id == request.CategoryId.Value && x.FullWorthSpaceId == fullWorthSpaceId, ct))
            return TransactionClassificationResult.InvalidCategory;

        if (!request.IsTransfer && entity.IsTransfer && entity.TransferGroupId is { } releasedGroup)
        {
            // Demoting the pair also clears the counterpart's transfer state; refuse (as a non-leaking 404)
            // when the caller can no longer write every mate, so a revoked counterpart is never mutated.
            if (!await CanWriteAllTransferMatesAsync(userId, fullWorthSpaceId, releasedGroup, entity.Id, ct))
                return TransactionClassificationResult.NotFound;
            await ReleaseTransferGroupAsync(releasedGroup, entity.Id, ct);
        }

        entity.CategoryId = request.CategoryId;
        entity.IsIgnored = request.IsIgnored;
        entity.IsTransfer = request.IsTransfer;
        entity.TransferPurpose = request.IsTransfer ? Normalize(request.TransferPurpose) : null;
        if (!request.IsTransfer) entity.TransferGroupId = null;
        entity.UserNote = Normalize(request.UserNote);
        entity.CategorizationSource = "manual";
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return TransactionClassificationResult.Updated;
    }

    public async Task<TransferLinkResult> LinkTransferForOwnerAsync(Guid userId, Guid fullWorthSpaceId, Guid firstId, Guid secondId, CancellationToken ct)
    {
        if (firstId == secondId) return TransferLinkResult.Invalid;
        var owned = AccessibleTransactions(userId, fullWorthSpaceId, requireOwner: true);
        var first = await owned.SingleOrDefaultAsync(x => x.Id == firstId, ct);
        var second = await owned.SingleOrDefaultAsync(x => x.Id == secondId, ct);
        if (first is null || second is null) return TransferLinkResult.NotFound;
        if (first.TransferGroupId is not null || second.TransferGroupId is not null) return TransferLinkResult.Invalid;
        if (first.AccountId == second.AccountId) return TransferLinkResult.Invalid;
        if (!string.Equals(first.Currency, second.Currency, StringComparison.OrdinalIgnoreCase)) return TransferLinkResult.Invalid;
        if (first.Amount == 0m || first.Amount != -second.Amount) return TransferLinkResult.Invalid;

        var groupId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        foreach (var entity in new[] { first, second })
        {
            entity.TransferGroupId = groupId;
            entity.IsTransfer = true;
            entity.UpdatedAt = now;
        }
        await db.SaveChangesAsync(ct);
        return TransferLinkResult.Linked;
    }

    public async Task<TransferUnlinkResult> UnlinkTransferForOwnerAsync(Guid userId, Guid fullWorthSpaceId, Guid id, CancellationToken ct)
    {
        var entity = await AccessibleTransactions(userId, fullWorthSpaceId, requireOwner: true).SingleOrDefaultAsync(x => x.Id == id, ct);
        if (entity is null) return TransferUnlinkResult.NotFound;
        if (entity.TransferGroupId is not { } groupId) return TransferUnlinkResult.NotLinked;
        // Unlinking mutates the counterpart too; refuse (non-leaking 404) when a mate is no longer writable.
        if (!await CanWriteAllTransferMatesAsync(userId, fullWorthSpaceId, groupId, id, ct))
            return TransferUnlinkResult.NotFound;
        await ReleaseTransferGroupAsync(groupId, id, ct);
        entity.IsTransfer = false;
        entity.TransferGroupId = null;
        entity.TransferPurpose = null;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return TransferUnlinkResult.Unlinked;
    }

    // True only when the caller owns (write access) every other transaction in the transfer group, so
    // releasing/demoting the group never silently mutates a transaction in a hidden/revoked account.
    private async Task<bool> CanWriteAllTransferMatesAsync(Guid userId, Guid fullWorthSpaceId, Guid groupId, Guid excludeId, CancellationToken ct)
    {
        var mateIds = await db.Transactions.AsNoTracking()
            .Where(x => x.TransferGroupId == groupId && x.Id != excludeId)
            .Select(x => x.Id)
            .ToListAsync(ct);
        if (mateIds.Count == 0) return true;
        var writable = await AccessibleTransactions(userId, fullWorthSpaceId, requireOwner: true)
            .CountAsync(x => mateIds.Contains(x.Id), ct);
        return writable == mateIds.Count;
    }

    private async Task ReleaseTransferGroupAsync(Guid groupId, Guid excludeId, CancellationToken ct)
    {
        var mates = await db.Transactions.Where(x => x.TransferGroupId == groupId && x.Id != excludeId).ToListAsync(ct);
        var now = DateTimeOffset.UtcNow;
        foreach (var mate in mates)
        {
            mate.IsTransfer = false;
            mate.TransferGroupId = null;
            mate.TransferPurpose = null;
            mate.UpdatedAt = now;
        }
    }

    public async Task<object?> GetAllocationsForUserAsync(Guid userId, Guid fullWorthSpaceId, Guid id, CancellationToken ct)
    {
        var tx = await AccessibleTransactions(userId, fullWorthSpaceId, requireOwner: false)
            .Where(x => x.Id == id)
            .Select(x => new { x.Id, x.Amount, x.Currency })
            .SingleOrDefaultAsync(ct);
        if (tx is null) return null;

        var raw = await db.TransactionAllocations.AsNoTracking()
            .Where(a => a.TransactionId == id)
            .Select(a => new
            {
                a.Id, a.CategoryId, a.Amount, a.Note, a.PurchaseItemId, a.CreatedAt,
                ArticleName = a.PurchaseItemId.HasValue
                    ? db.PurchaseItems.Where(item => item.Id == a.PurchaseItemId.Value).Select(item => item.Name).FirstOrDefault()
                    : null
            }).ToListAsync(ct);
        var lines = raw.OrderBy(a => a.CreatedAt).Select(a => new { a.Id, a.CategoryId, a.Amount, a.Note, a.PurchaseItemId, a.ArticleName }).ToList();
        var allocated = lines.Sum(l => l.Amount);
        return new { transactionId = tx.Id, amount = tx.Amount, currency = tx.Currency, allocated, remaining = tx.Amount - allocated, lines };
    }

    public async Task<AllocationResult> ReplaceAllocationsForOwnerAsync(Guid userId, Guid fullWorthSpaceId, Guid id, IReadOnlyList<AllocationLine> lines, CancellationToken ct)
    {
        var tx = await AccessibleTransactions(userId, fullWorthSpaceId, requireOwner: true).SingleOrDefaultAsync(x => x.Id == id, ct);
        if (tx is null) return AllocationResult.NotFound;

        var purchaseItemIds = lines.Where(line => line.PurchaseItemId.HasValue).Select(line => line.PurchaseItemId!.Value).Distinct().ToList();
        var articleRows = await db.PurchaseItems.AsNoTracking()
            .Where(item =>
                purchaseItemIds.Contains(item.Id) &&
                item.Purchase.FullWorthSpaceId == fullWorthSpaceId &&
                (item.Purchase.Visibility != "private" || item.Purchase.CreatedByUserId == userId) &&
                (item.Purchase.TransactionId == id || item.Purchase.PaymentLinks.Any(link => link.TransactionId == id)))
            .Select(item => new { item.Id, item.CategoryId, item.Name })
            .ToListAsync(ct);
        if (articleRows.Count != purchaseItemIds.Count) return AllocationResult.InvalidPurchaseItem;
        var articles = articleRows.ToDictionary(item => item.Id);

        foreach (var line in lines.Where(line => line.PurchaseItemId.HasValue))
        {
            var article = articles[line.PurchaseItemId!.Value];
            if (line.CategoryId != article.CategoryId) return AllocationResult.InvalidPurchaseItem;
        }

        var categoryIds = lines.Where(l => l.CategoryId.HasValue).Select(l => l.CategoryId!.Value)
            .Concat(articleRows.Where(x => x.CategoryId.HasValue).Select(x => x.CategoryId!.Value))
            .Distinct().ToList();
        if (categoryIds.Count > 0)
        {
            var valid = await db.Categories.AsNoTracking()
                .Where(c => c.FullWorthSpaceId == fullWorthSpaceId && categoryIds.Contains(c.Id))
                .Select(c => c.Id).ToListAsync(ct);
            if (valid.Count != categoryIds.Count) return AllocationResult.InvalidCategory;
        }

        // Signed detail lines are valid as long as their NET equals the real ledger transaction.
        // Example expense: -15 products + 2 coupon = -13 bank charge.
        if (lines.Count > 0 && Math.Abs(lines.Sum(l => l.Amount) - tx.Amount) > PurchaseArticleCalculator.Tolerance(tx.Currency))
            return AllocationResult.Unbalanced;

        var existing = await db.TransactionAllocations.Where(a => a.TransactionId == id).ToListAsync(ct);
        db.TransactionAllocations.RemoveRange(existing);
        foreach (var line in lines)
        {
            var article = line.PurchaseItemId.HasValue ? articles[line.PurchaseItemId.Value] : null;
            db.TransactionAllocations.Add(new TransactionAllocation
            {
                TransactionId = id,
                CategoryId = article?.CategoryId ?? line.CategoryId,
                Amount = line.Amount,
                Note = Normalize(line.Note) ?? article?.Name,
                PurchaseItemId = line.PurchaseItemId
            });
        }
        tx.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return AllocationResult.Updated;
    }

    public async Task<RefundLinkResult> LinkRefundForOwnerAsync(Guid userId, Guid fullWorthSpaceId, Guid refundId, Guid? originalId, Guid? targetCategoryId, CancellationToken ct)
    {
        var refund = await AccessibleTransactions(userId, fullWorthSpaceId, requireOwner: true).SingleOrDefaultAsync(x => x.Id == refundId, ct);
        if (refund is null) return RefundLinkResult.NotFound;

        if (originalId is null)
        {
            refund.RefundOfTransactionId = null;
            refund.RefundCategoryId = null;
            refund.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return RefundLinkResult.Updated;
        }
        if (refund.Amount <= 0m || originalId.Value == refundId) return RefundLinkResult.Invalid;

        var original = await AccessibleTransactions(userId, fullWorthSpaceId, requireOwner: true)
            .Select(x => new { x.Id, x.Amount, x.Currency, x.IsTransfer })
            .SingleOrDefaultAsync(x => x.Id == originalId.Value, ct);
        if (original is null) return RefundLinkResult.NotFound;
        if (original.Amount >= 0m || original.IsTransfer || !string.Equals(original.Currency, refund.Currency, StringComparison.OrdinalIgnoreCase))
            return RefundLinkResult.Invalid;

        if (targetCategoryId is { } target)
        {
            var known = await AccessibleTransactions(userId, fullWorthSpaceId, requireOwner: false)
                            .AnyAsync(x => x.Id == originalId.Value && x.CategoryId == target, ct)
                        || await db.TransactionAllocations.AsNoTracking().AnyAsync(a => a.TransactionId == originalId.Value && a.CategoryId == target, ct)
                        || await db.Purchases.AsNoTracking().AnyAsync(p =>
                            p.FullWorthSpaceId == fullWorthSpaceId &&
                            (p.TransactionId == originalId.Value || p.PaymentLinks.Any(link => link.TransactionId == originalId.Value)) &&
                            (p.Visibility != "private" || p.CreatedByUserId == userId) &&
                            p.ReviewState == "confirmed" && p.Items.Any(i => i.CategoryId == target), ct);
            if (!known) return RefundLinkResult.Invalid;
        }

        refund.RefundOfTransactionId = originalId.Value;
        refund.RefundCategoryId = targetCategoryId;
        refund.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return RefundLinkResult.Updated;
    }

    public async Task<(TransactionCreateResult Result, Guid Id)> CreateManualForOwnerAsync(Guid userId, Guid fullWorthSpaceId, CreateTransactionRequest request, CancellationToken ct)
    {
        var magnitude = Math.Abs(request.Amount);
        if (magnitude == 0m) throw new ArgumentException("Amount must be greater than zero.");
        if (magnitude >= 1_000_000_000_000m) throw new ArgumentException("Amount must be less than 1,000,000,000,000.");
        var direction = (request.Direction ?? string.Empty).Trim().ToLowerInvariant();
        if (direction != "income" && direction != "expense") throw new ArgumentException("Direction must be 'income' or 'expense'.");
        var merchant = Normalize(request.Counterparty);
        if (merchant is null) throw new ArgumentException("A description is required.");

        var account = await db.Accounts.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == request.AccountId && x.FullWorthSpaceId == fullWorthSpaceId &&
            db.FullWorthSpaceMembers.Any(member => member.FullWorthSpaceId == fullWorthSpaceId && member.UserId == userId) &&
            x.Owners.Any(owner => owner.UserId == userId), ct);
        if (account is null) return (TransactionCreateResult.NotFound, Guid.Empty);
        var isOwner = await db.Set<AccountOwner>().AsNoTracking().AnyAsync(x => x.AccountId == request.AccountId && x.UserId == userId && x.OwnershipType == AccountOwnershipTypes.Owner, ct);
        if (!isOwner) return (TransactionCreateResult.Forbidden, Guid.Empty);
        if (account.Provider != "manual" || account.BankConnectionId is not null) return (TransactionCreateResult.NotManual, Guid.Empty);
        if (request.CategoryId.HasValue && !await db.Categories.AsNoTracking().AnyAsync(c => c.Id == request.CategoryId.Value && c.FullWorthSpaceId == fullWorthSpaceId, ct))
            return (TransactionCreateResult.InvalidCategory, Guid.Empty);

        var currency = string.IsNullOrWhiteSpace(request.Currency) ? account.Currency : NormalizeCurrency(request.Currency);
        var bookingDate = request.Date ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var entity = new FinanceTransaction
        {
            AccountId = account.Id,
            CategoryId = request.CategoryId,
            ExternalKey = "manual:" + Guid.NewGuid().ToString("N"),
            Status = "BOOK",
            BookingDate = bookingDate,
            ValueDate = bookingDate,
            Amount = direction == "expense" ? -magnitude : magnitude,
            Currency = currency,
            Counterparty = merchant,
            NormalizedCounterparty = merchant.ToUpperInvariant(),
            UserNote = Normalize(request.Note),
            CategorizationSource = request.CategoryId.HasValue ? "manual" : "none",
            RawJson = "{}"
        };
        db.Transactions.Add(entity);
        await db.SaveChangesAsync(ct);
        return (TransactionCreateResult.Created, entity.Id);
    }

    public async Task<TransactionDeleteResult> DeleteManualForOwnerAsync(Guid userId, Guid fullWorthSpaceId, Guid id, CancellationToken ct)
    {
        var entity = await AccessibleTransactions(userId, fullWorthSpaceId, requireOwner: true).SingleOrDefaultAsync(x => x.Id == id, ct);
        if (entity is null) return TransactionDeleteResult.NotFound;
        if (!entity.ExternalKey.StartsWith("manual:", StringComparison.Ordinal)) return TransactionDeleteResult.NotManual;
        db.Transactions.Remove(entity);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { return TransactionDeleteResult.Referenced; }
        return TransactionDeleteResult.Deleted;
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string NormalizeCurrency(string? currency)
    {
        if (string.IsNullOrWhiteSpace(currency)) return "EUR";
        var normalized = currency.Trim().ToUpperInvariant();
        if (normalized.Length != 3 || normalized.Any(character => character is < 'A' or > 'Z'))
            throw new ArgumentException("Currency must be a three-letter code.");
        return normalized;
    }

    private IQueryable<FinanceTransaction> AccessibleTransactions(Guid userId, Guid? fullWorthSpaceId, bool requireOwner)
    {
        var query = db.Transactions.AsQueryable();
        if (!requireOwner) query = query.AsNoTracking();
        return query.Where(transaction => db.Accounts.Any(account =>
            account.Id == transaction.AccountId &&
            (!fullWorthSpaceId.HasValue || account.FullWorthSpaceId == fullWorthSpaceId.Value) &&
            db.FullWorthSpaceMembers.Any(member => member.FullWorthSpaceId == account.FullWorthSpaceId && member.UserId == userId) &&
            account.Owners.Any(owner => owner.UserId == userId && (!requireOwner || owner.OwnershipType == AccountOwnershipTypes.Owner))));
    }
}

public sealed record TransactionQuery(Guid? AccountId, Guid? CategoryId, DateOnly? From, DateOnly? To, string? Direction, string? Query, bool? IncludeIgnored, bool? TransfersOnly, string? Sort, string? Order, int? Offset, int? Limit);
public sealed record TransactionClassification(Guid? CategoryId, bool IsIgnored, bool IsTransfer, string? TransferPurpose = null, string? UserNote = null);
public sealed record AllocationLine(Guid? CategoryId, decimal Amount, string? Note, Guid? PurchaseItemId = null);
public sealed record RefundLink(Guid? OriginalTransactionId, Guid? RefundCategoryId = null);
public sealed record CreateTransactionRequest(Guid AccountId, decimal Amount, string? Direction, DateOnly? Date, string? Currency, string? Counterparty, Guid? CategoryId, string? Note);
public sealed record TransferLinkRequest(Guid OtherTransactionId);

public static class TransactionEndpoints
{
    public static IEndpointRouteBuilder MapTransactionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/transactions").WithTags("Transactions");

        group.MapGet("/", async (Guid? fullWorthSpaceId, Guid? accountId, Guid? categoryId, DateOnly? from, DateOnly? to, string? direction, string? query, bool? includeIgnored, bool? transfersOnly, string? sort, string? order, int? offset, int? limit, CurrentUserContext currentUser, TransactionStore store, CancellationToken ct) =>
            Results.Ok(await store.SearchForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId,
                new(accountId, categoryId, from, to, direction, query, includeIgnored, transfersOnly, sort, order, offset, limit), ct)));

        group.MapGet("/{id:guid}", async (Guid id, Guid fullWorthSpaceId, CurrentUserContext currentUser, TransactionStore store, CancellationToken ct) =>
        {
            var item = await store.GetForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, ct);
            return item is null ? Results.NotFound() : Results.Ok(item);
        });

        group.MapPost("/", async (Guid fullWorthSpaceId, CreateTransactionRequest request, CurrentUserContext currentUser, TransactionStore store, CancellationToken ct) =>
        {
            try
            {
                var (result, id) = await store.CreateManualForOwnerAsync(currentUser.RequireUserId(), fullWorthSpaceId, request, ct);
                return result switch
                {
                    TransactionCreateResult.Created => Results.Created($"/api/transactions/{id}?fullWorthSpaceId={fullWorthSpaceId}", new { id }),
                    TransactionCreateResult.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
                    TransactionCreateResult.NotManual => Results.Conflict(new { error = "Only manual accounts accept hand-booked transactions." }),
                    TransactionCreateResult.InvalidCategory => Results.BadRequest(new { error = "Category must belong to the FullWorth Space." }),
                    _ => Results.NotFound()
                };
            }
            catch (ArgumentException exception) { return Results.BadRequest(new { error = exception.Message }); }
        });

        group.MapDelete("/{id:guid}", async (Guid id, Guid fullWorthSpaceId, CurrentUserContext currentUser, TransactionStore store, CancellationToken ct) =>
        {
            var userId = currentUser.RequireUserId();
            var ownership = await store.GetOwnershipForUserAsync(userId, fullWorthSpaceId, id, ct);
            if (ownership is null) return Results.NotFound();
            if (ownership != AccountOwnershipTypes.Owner) return Results.StatusCode(StatusCodes.Status403Forbidden);
            return await store.DeleteManualForOwnerAsync(userId, fullWorthSpaceId, id, ct) switch
            {
                TransactionDeleteResult.Deleted => Results.NoContent(),
                TransactionDeleteResult.NotManual => Results.Conflict(new { error = "Only manually booked transactions can be deleted." }),
                TransactionDeleteResult.Referenced => Results.Conflict(new { error = "This transaction is linked to a receipt; remove it first." }),
                _ => Results.NotFound()
            };
        });

        group.MapPatch("/{id:guid}/classification", async (Guid id, Guid fullWorthSpaceId, TransactionClassification request, CurrentUserContext currentUser, TransactionStore store, CancellationToken ct) =>
        {
            var userId = currentUser.RequireUserId();
            var ownership = await store.GetOwnershipForUserAsync(userId, fullWorthSpaceId, id, ct);
            if (ownership is null) return Results.NotFound();
            if (ownership != AccountOwnershipTypes.Owner) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var result = await store.ClassifyForOwnerAsync(userId, fullWorthSpaceId, id, request, ct);
            return result == TransactionClassificationResult.Updated ? Results.NoContent() : Results.NotFound();
        });

        group.MapPatch("/{id:guid}/refund", async (Guid id, Guid fullWorthSpaceId, RefundLink request, CurrentUserContext currentUser, TransactionStore store, CancellationToken ct) =>
        {
            var userId = currentUser.RequireUserId();
            var ownership = await store.GetOwnershipForUserAsync(userId, fullWorthSpaceId, id, ct);
            if (ownership is null) return Results.NotFound();
            if (ownership != AccountOwnershipTypes.Owner) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var result = await store.LinkRefundForOwnerAsync(userId, fullWorthSpaceId, id, request?.OriginalTransactionId, request?.RefundCategoryId, ct);
            return result switch
            {
                RefundLinkResult.Updated => Results.NoContent(),
                RefundLinkResult.Invalid => Results.BadRequest(new { error = "invalid_refund_link" }),
                _ => Results.NotFound()
            };
        });

        group.MapPost("/{id:guid}/transfer-link", async (Guid id, Guid fullWorthSpaceId, TransferLinkRequest request, CurrentUserContext currentUser, TransactionStore store, CancellationToken ct) =>
        {
            var result = await store.LinkTransferForOwnerAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, request.OtherTransactionId, ct);
            return result switch
            {
                TransferLinkResult.Linked => Results.NoContent(),
                TransferLinkResult.Invalid => Results.BadRequest(new { error = "invalid_transfer_link" }),
                _ => Results.NotFound()
            };
        });

        group.MapDelete("/{id:guid}/transfer-link", async (Guid id, Guid fullWorthSpaceId, CurrentUserContext currentUser, TransactionStore store, CancellationToken ct) =>
        {
            var result = await store.UnlinkTransferForOwnerAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, ct);
            return result switch
            {
                TransferUnlinkResult.Unlinked => Results.NoContent(),
                TransferUnlinkResult.NotLinked => Results.Conflict(new { error = "not_linked" }),
                _ => Results.NotFound()
            };
        });

        group.MapGet("/{id:guid}/allocations", async (Guid id, Guid fullWorthSpaceId, CurrentUserContext currentUser, TransactionStore store, CancellationToken ct) =>
        {
            var result = await store.GetAllocationsForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });

        group.MapPut("/{id:guid}/allocations", async (Guid id, Guid fullWorthSpaceId, List<AllocationLine> request, CurrentUserContext currentUser, TransactionStore store, CancellationToken ct) =>
        {
            var userId = currentUser.RequireUserId();
            var ownership = await store.GetOwnershipForUserAsync(userId, fullWorthSpaceId, id, ct);
            if (ownership is null) return Results.NotFound();
            if (ownership != AccountOwnershipTypes.Owner) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var result = await store.ReplaceAllocationsForOwnerAsync(userId, fullWorthSpaceId, id, request ?? [], ct);
            return result switch
            {
                AllocationResult.Updated => Results.NoContent(),
                AllocationResult.Unbalanced => Results.BadRequest(new { error = "Allocations must net to the transaction amount." }),
                AllocationResult.InvalidCategory => Results.BadRequest(new { error = "Allocation category must belong to the FullWorth Space." }),
                AllocationResult.InvalidPurchaseItem => Results.BadRequest(new { error = "Article must belong to a visible purchase linked to this transaction, and its category cannot be overridden here." }),
                _ => Results.NotFound()
            };
        });

        return app;
    }
}
