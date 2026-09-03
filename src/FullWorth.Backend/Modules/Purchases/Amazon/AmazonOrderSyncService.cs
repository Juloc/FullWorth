using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FullWorth.Backend.Modules.Purchases.Amazon;

public enum AmazonSyncState { Success, NotConnected, ReauthenticationRequired, Failed }
public sealed record AmazonSyncOutcome(AmazonSyncState State, AmazonSyncResult? Result = null, string? Error = null);

public sealed class AmazonOrderSyncService(
    FullWorthDbContext db,
    FieldCipher cipher,
    PurchaseAuthorizationStore authorization,
    AmazonBrowserAutomation browser,
    AmazonPurchaseMatchingService matching,
    AmazonLoginChallengeStore loginChallenges,
    AmazonSqlStore amazonStore,
    IOptions<AmazonIntegrationOptions> options,
    ILogger<AmazonOrderSyncService> logger)
{
    private readonly AmazonIntegrationOptions _options = options.Value;

    public async Task<AmazonConnectionStatus> GetStatusAsync(Guid userId, Guid fullWorthSpaceId, CancellationToken ct)
    {
        var connection = await amazonStore.GetConnectionAsync(userId, fullWorthSpaceId, ct);
        if (connection is null) return new(false, "disconnected", null, null, null);
        var sessionUsable = !string.IsNullOrWhiteSpace(connection.EncryptedStorageState) && connection.Status != "requires_reauth";
        return new(sessionUsable, connection.Status, connection.LastSyncAt, connection.LastSuccessfulSyncAt, connection.LastError);
    }

    public async Task<AmazonLoginResult> StartLoginAsync(Guid userId, Guid fullWorthSpaceId, AmazonLoginStartRequest request, CancellationToken ct)
    {
        if (!await authorization.IsFullWorthSpaceMemberAsync(userId, fullWorthSpaceId, ct)) return new("not_found");
        var attempt = await loginChallenges.StartAsync(userId, fullWorthSpaceId, request.Email, request.Password, ct);
        if (attempt.StorageState is not null) await SaveConnectionAsync(userId, fullWorthSpaceId, attempt.StorageState, ct);
        return new(attempt.Status, attempt.ChallengeId, attempt.Message);
    }

    public async Task<AmazonLoginResult> CompleteLoginAsync(Guid userId, Guid fullWorthSpaceId, Guid challengeId, AmazonLoginCompleteRequest request, CancellationToken ct)
    {
        if (!await authorization.IsFullWorthSpaceMemberAsync(userId, fullWorthSpaceId, ct)) return new("not_found");
        var attempt = await loginChallenges.CompleteAsync(challengeId, userId, fullWorthSpaceId, request.Otp, ct);
        if (attempt.StorageState is not null) await SaveConnectionAsync(userId, fullWorthSpaceId, attempt.StorageState, ct);
        return new(attempt.Status, attempt.ChallengeId, attempt.Message);
    }

    public async Task<bool> DisconnectAsync(Guid userId, Guid fullWorthSpaceId, CancellationToken ct) =>
        await amazonStore.DeleteConnectionAsync(userId, fullWorthSpaceId, ct) > 0;

    public async Task<AmazonSyncOutcome> SyncAsync(Guid userId, Guid fullWorthSpaceId, int? requestedHistoryDays, CancellationToken ct)
    {
        var connection = await amazonStore.GetConnectionAsync(userId, fullWorthSpaceId, ct);
        if (connection is null) return new(AmazonSyncState.NotConnected, Error: "Amazon is not connected.");
        if (!await authorization.IsFullWorthSpaceMemberAsync(userId, fullWorthSpaceId, ct))
            return new(AmazonSyncState.NotConnected, Error: "FullWorth Space not found.");

        var now = DateTimeOffset.UtcNow;
        await amazonStore.MarkSyncStartedAsync(userId, fullWorthSpaceId, now, ct);

        try
        {
            var state = cipher.Unprotect(connection.EncryptedStorageState);
            if (string.IsNullOrWhiteSpace(state)) return new(AmazonSyncState.NotConnected, Error: "Amazon session is missing.");
            var days = Math.Clamp(requestedHistoryDays ?? _options.InitialHistoryDays, 1, Math.Max(1, _options.MaxHistoryDays));
            var since = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(-days));
            var read = await browser.ReadOrdersAsync(state, since, Math.Max(1, _options.MaxOrdersPerSync), ct);
            await SaveConnectionAsync(userId, fullWorthSpaceId, read.StorageState, ct);

            var imported = 0;
            var paymentsLinked = 0;
            var refundsLinked = 0;
            var importedIds = new List<Guid>();
            foreach (var order in read.Orders)
            {
                ct.ThrowIfCancellationRequested();
                var purchaseId = await UpsertAmazonOrderAsync(userId, fullWorthSpaceId, order, ct);
                if (!purchaseId.HasValue) continue;
                imported++;
                importedIds.Add(purchaseId.Value);

                await amazonStore.UpsertOrderMetadataAsync(purchaseId.Value, order.ExternalStatus, order.NonBankPaymentAmount, DateTimeOffset.UtcNow, ct);
                await amazonStore.UpsertRefundsAsync(purchaseId.Value, order.Refunds, ct);
                paymentsLinked += await matching.TryMatchPaymentsAsync(userId, fullWorthSpaceId, purchaseId.Value, ct);
                refundsLinked += await matching.TryMatchRefundsAsync(userId, fullWorthSpaceId, purchaseId.Value, ct);
            }
            paymentsLinked += await matching.TryMatchCombinedPaymentsAsync(userId, fullWorthSpaceId, importedIds, ct);

            var completedAt = DateTimeOffset.UtcNow;
            await amazonStore.MarkSyncSuccessAsync(userId, fullWorthSpaceId, completedAt, ct);
            return new(AmazonSyncState.Success, new(read.Orders.Count, imported, paymentsLinked, refundsLinked, completedAt));
        }
        catch (AmazonReauthenticationRequiredException ex)
        {
            const string error = "Amazon login expired or Amazon requested verification.";
            await amazonStore.MarkSyncFailureAsync(userId, fullWorthSpaceId, "requires_reauth", error, DateTimeOffset.UtcNow, ct);
            logger.LogInformation("Amazon session requires reauthentication for connection {ConnectionId}: {Reason}", connection.Id, ex.Message);
            return new(AmazonSyncState.ReauthenticationRequired, Error: error);
        }
        catch (AmazonOrderLimitExceededException ex)
        {
            var error = $"Amazon sync reached the configured safety limit of {ex.Limit} orders. Increase AmazonIntegration:MaxOrdersPerSync and retry.";
            await amazonStore.MarkSyncFailureAsync(userId, fullWorthSpaceId, "error", error, DateTimeOffset.UtcNow, ct);
            logger.LogWarning("Amazon sync hit the order limit for connection {ConnectionId}: {Limit}", connection.Id, ex.Limit);
            return new(AmazonSyncState.Failed, Error: error);
        }
        catch (AmazonOrderParsingException ex)
        {
            var error = $"Amazon order {ex.OrderId} could not be read reliably. No partial import was accepted; retry after checking the Amazon order page.";
            await amazonStore.MarkSyncFailureAsync(userId, fullWorthSpaceId, "error", error, DateTimeOffset.UtcNow, ct);
            logger.LogWarning("Amazon sync stopped on an unreadable order for connection {ConnectionId}: {OrderId}", connection.Id, ex.OrderId);
            return new(AmazonSyncState.Failed, Error: error);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            const string error = "Amazon sync failed. Retry the sync; reconnect Amazon only if FullWorth explicitly asks for reauthentication.";
            await amazonStore.MarkSyncFailureAsync(userId, fullWorthSpaceId, "error", error, DateTimeOffset.UtcNow, ct);
            logger.LogWarning("Amazon sync failed for connection {ConnectionId}: {Type}", connection.Id, ex.GetType().Name);
            return new(AmazonSyncState.Failed, Error: error);
        }
    }

    public async Task<IReadOnlyList<AmazonPaymentCandidateView>?> GetPaymentCandidatesAsync(Guid userId, Guid fullWorthSpaceId, Guid purchaseId, CancellationToken ct)
    {
        if (await authorization.GetAccessAsync(userId, fullWorthSpaceId, purchaseId, ct) != PurchaseAccessLevel.Write) return null;
        var purchase = await db.Purchases.AsNoTracking().SingleOrDefaultAsync(x => x.Id == purchaseId && x.FullWorthSpaceId == fullWorthSpaceId && x.Source == "amazon", ct);
        if (purchase is null) return null;
        var date = purchase.PurchaseDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var metadata = await amazonStore.GetOrderMetadataAsync(purchaseId, ct);
        var bankTarget = Math.Max(0m, purchase.TotalAmount - Math.Clamp(metadata?.NonBankPaymentAmount ?? 0m, 0m, purchase.TotalAmount));
        var currentLinks = await amazonStore.ListPaymentLinksAsync(purchaseId, ct);
        var currentIds = currentLinks.Select(x => x.TransactionId).ToHashSet();
        var remaining = Math.Max(0m, bankTarget - currentLinks.Sum(x => x.AllocatedAmount));
        if (remaining <= .01m) return [];

        var allLinks = await amazonStore.ListAllPaymentLinksAsync(ct);
        var allocatedByTransaction = allLinks.GroupBy(x => x.TransactionId).ToDictionary(g => g.Key, g => g.Sum(x => x.AllocatedAmount));
        var rows = await db.Transactions.AsNoTracking()
            .Where(tx => tx.Amount < 0 && tx.Currency == purchase.Currency && tx.BookingDate >= date.AddDays(-3) && tx.BookingDate <= date.AddDays(365))
            .Where(tx => db.Accounts.Any(account => account.Id == tx.AccountId && account.FullWorthSpaceId == fullWorthSpaceId &&
                account.Owners.Any(owner => owner.UserId == userId && owner.OwnershipType == AccountOwnershipTypes.Owner)))
            .Where(tx => !currentIds.Contains(tx.Id))
            .Select(tx => new { tx.Id, tx.BookingDate, tx.Amount, tx.Counterparty, tx.Description })
            .ToListAsync(ct);

        return rows.Select(tx =>
        {
            var available = Math.Max(0m, Math.Abs(tx.Amount) - allocatedByTransaction.GetValueOrDefault(tx.Id));
            var suggested = Math.Min(available, remaining);
            var amountScore = remaining <= 0m ? 0m : Math.Max(0m, 1m - Math.Abs(available - remaining) / Math.Max(1m, remaining));
            var amazonScore = IsAmazon(tx.Counterparty) ? 1m : 0m;
            var dateScore = tx.BookingDate.HasValue ? Math.Max(0m, 1m - Math.Abs(tx.BookingDate.Value.DayNumber - date.DayNumber) / 369m) : 0m;
            var confidence = Math.Clamp(amountScore * .5m + amazonScore * .35m + dateScore * .15m, 0m, 1m);
            return new AmazonPaymentCandidateView(tx.Id, tx.BookingDate, tx.Amount, available, suggested, tx.Counterparty, tx.Description, confidence);
        }).Where(x => x.AvailableAmount > .01m)
          .OrderByDescending(x => x.Confidence).Take(25).ToList();
    }

    public async Task<bool> LinkPaymentAsync(Guid userId, Guid fullWorthSpaceId, Guid purchaseId, Guid transactionId, decimal? confidence, decimal? requestedAllocation, CancellationToken ct)
    {
        if (await authorization.GetAccessAsync(userId, fullWorthSpaceId, purchaseId, ct) != PurchaseAccessLevel.Write) return false;
        if (await authorization.GetTransactionAccessAsync(userId, fullWorthSpaceId, transactionId, ct) != PurchaseAccessLevel.Write) return false;
        var purchase = await db.Purchases.SingleOrDefaultAsync(x => x.Id == purchaseId && x.FullWorthSpaceId == fullWorthSpaceId && x.Source == "amazon", ct);
        if (purchase is null) return false;

        var candidate = await db.Transactions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == transactionId, ct);
        if (candidate is null || candidate.Amount >= 0 || !string.Equals(candidate.Currency, purchase.Currency, StringComparison.OrdinalIgnoreCase) || !candidate.BookingDate.HasValue)
            return false;
        var anchor = purchase.PurchaseDate ?? candidate.BookingDate.Value;
        if (candidate.BookingDate.Value < anchor.AddDays(-3) || candidate.BookingDate.Value > anchor.AddDays(365)) return false;

        var metadata = await amazonStore.GetOrderMetadataAsync(purchaseId, ct);
        var bankTarget = Math.Max(0m, purchase.TotalAmount - Math.Clamp(metadata?.NonBankPaymentAmount ?? 0m, 0m, purchase.TotalAmount));
        var existingForPurchase = await amazonStore.ListPaymentLinksAsync(purchaseId, ct);
        var currentOtherAllocated = existingForPurchase.Where(x => x.TransactionId != transactionId).Sum(x => x.AllocatedAmount);
        var remainingWithoutCandidate = Math.Max(0m, bankTarget - currentOtherAllocated);

        var allLinks = await amazonStore.ListAllPaymentLinksAsync(ct);
        var allocatedByOthers = allLinks.Where(x => x.TransactionId == transactionId && x.PurchaseId != purchaseId).Sum(x => x.AllocatedAmount);
        var available = Math.Max(0m, Math.Abs(candidate.Amount) - allocatedByOthers);
        var allocation = requestedAllocation ?? Math.Min(available, remainingWithoutCandidate);
        if (allocation <= .01m || allocation > available + .01m || allocation > remainingWithoutCandidate + .01m) return false;

        var normalizedConfidence = confidence.HasValue ? Math.Clamp(confidence.Value, 0m, 1m) : (decimal?)null;
        await amazonStore.UpsertPaymentLinkAsync(purchaseId, transactionId, allocation, normalizedConfidence, "manual", ct);
        if (!purchase.TransactionId.HasValue)
        {
            purchase.TransactionId = transactionId;
            purchase.MatchConfidence = normalizedConfidence;
        }
        await ReconcilePaymentStatusAsync(purchase, ct);
        return true;
    }

    public async Task<bool> UnlinkPaymentAsync(Guid userId, Guid fullWorthSpaceId, Guid purchaseId, Guid transactionId, CancellationToken ct)
    {
        if (await authorization.GetAccessAsync(userId, fullWorthSpaceId, purchaseId, ct) != PurchaseAccessLevel.Write) return false;
        var purchase = await db.Purchases.SingleOrDefaultAsync(x => x.Id == purchaseId && x.FullWorthSpaceId == fullWorthSpaceId && x.Source == "amazon", ct);
        if (purchase is null) return false;
        if (await amazonStore.DeletePaymentLinkAsync(purchaseId, transactionId, ct) == 0) return false;

        var remaining = await amazonStore.ListPaymentLinksAsync(purchaseId, ct);
        if (purchase.TransactionId == transactionId)
        {
            var replacement = remaining.OrderByDescending(x => x.MatchConfidence ?? 0m).ThenBy(x => x.CreatedAt).FirstOrDefault();
            purchase.TransactionId = replacement?.TransactionId;
            purchase.MatchConfidence = replacement?.MatchConfidence;
        }
        await ReconcilePaymentStatusAsync(purchase, ct);
        return true;
    }

    public async Task<bool> SetNonBankPaymentAsync(Guid userId, Guid fullWorthSpaceId, Guid purchaseId, decimal amount, CancellationToken ct)
    {
        if (await authorization.GetAccessAsync(userId, fullWorthSpaceId, purchaseId, ct) != PurchaseAccessLevel.Write) return false;
        var purchase = await db.Purchases.SingleOrDefaultAsync(x => x.Id == purchaseId && x.FullWorthSpaceId == fullWorthSpaceId && x.Source == "amazon", ct);
        if (purchase is null || amount < 0m || amount > purchase.TotalAmount + .01m) return false;
        await amazonStore.SetManualNonBankPaymentAsync(purchaseId, Math.Min(purchase.TotalAmount, amount), DateTimeOffset.UtcNow, ct);
        await ReconcilePaymentStatusAsync(purchase, ct);
        return true;
    }

    public async Task<IReadOnlyList<AmazonRefundCandidateView>?> GetRefundCandidatesAsync(Guid userId, Guid fullWorthSpaceId, Guid purchaseId, Guid refundId, CancellationToken ct)
    {
        if (await authorization.GetAccessAsync(userId, fullWorthSpaceId, purchaseId, ct) != PurchaseAccessLevel.Write) return null;
        var purchase = await db.Purchases.AsNoTracking().SingleOrDefaultAsync(x => x.Id == purchaseId && x.FullWorthSpaceId == fullWorthSpaceId && x.Source == "amazon", ct);
        if (purchase is null) return null;
        var refund = (await amazonStore.ListRefundsAsync(purchaseId, ct)).SingleOrDefault(x => x.Id == refundId);
        if (refund is null) return null;

        var anchor = refund.RefundDate ?? purchase.PurchaseDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var usedRefundIds = await amazonStore.ListAllLinkedRefundTransactionIdsAsync(ct);
        var usedPaymentIds = await amazonStore.ListAllLinkedPaymentTransactionIdsAsync(ct);
        if (refund.TransactionId.HasValue) usedRefundIds.Remove(refund.TransactionId.Value);

        var rows = await db.Transactions.AsNoTracking()
            .Where(tx => tx.Amount > 0 && tx.Currency == refund.Currency && tx.BookingDate >= anchor.AddDays(-7) && tx.BookingDate <= anchor.AddDays(60))
            .Where(tx => Math.Abs(tx.Amount - refund.Amount) <= .01m)
            .Where(tx => db.Accounts.Any(account => account.Id == tx.AccountId && account.FullWorthSpaceId == fullWorthSpaceId &&
                account.Owners.Any(owner => owner.UserId == userId && owner.OwnershipType == AccountOwnershipTypes.Owner)))
            .Where(tx => !usedRefundIds.Contains(tx.Id) && !usedPaymentIds.Contains(tx.Id) &&
                         !db.Purchases.Any(other => other.TransactionId == tx.Id))
            .Select(tx => new { tx.Id, tx.BookingDate, tx.Amount, tx.Counterparty, tx.Description })
            .ToListAsync(ct);

        return rows.Select(tx =>
        {
            var amazonScore = IsAmazon(tx.Counterparty) ? 1m : 0m;
            var dateScore = tx.BookingDate.HasValue ? Math.Max(0m, 1m - Math.Abs(tx.BookingDate.Value.DayNumber - anchor.DayNumber) / 68m) : 0m;
            return new AmazonRefundCandidateView(tx.Id, tx.BookingDate, tx.Amount, tx.Counterparty, tx.Description,
                Math.Clamp(.70m + amazonScore * .20m + dateScore * .10m, 0m, .99m));
        }).OrderByDescending(x => x.Confidence).Take(15).ToList();
    }

    public async Task<bool> LinkRefundAsync(Guid userId, Guid fullWorthSpaceId, Guid purchaseId, Guid refundId, Guid transactionId, decimal? confidence, CancellationToken ct)
    {
        if (await authorization.GetAccessAsync(userId, fullWorthSpaceId, purchaseId, ct) != PurchaseAccessLevel.Write) return false;
        if (await authorization.GetTransactionAccessAsync(userId, fullWorthSpaceId, transactionId, ct) != PurchaseAccessLevel.Write) return false;
        var purchase = await db.Purchases.AsNoTracking().SingleOrDefaultAsync(x => x.Id == purchaseId && x.FullWorthSpaceId == fullWorthSpaceId && x.Source == "amazon", ct);
        if (purchase is null) return false;
        var refund = (await amazonStore.ListRefundsAsync(purchaseId, ct)).SingleOrDefault(x => x.Id == refundId);
        if (refund is null) return false;

        var candidate = await db.Transactions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == transactionId, ct);
        if (candidate is null || candidate.Amount <= 0 || !string.Equals(candidate.Currency, refund.Currency, StringComparison.OrdinalIgnoreCase) ||
            Math.Abs(candidate.Amount - refund.Amount) > .01m || !candidate.BookingDate.HasValue)
            return false;
        var anchor = refund.RefundDate ?? purchase.PurchaseDate ?? candidate.BookingDate.Value;
        if (candidate.BookingDate.Value < anchor.AddDays(-7) || candidate.BookingDate.Value > anchor.AddDays(60)) return false;

        var usedRefundIds = await amazonStore.ListAllLinkedRefundTransactionIdsAsync(ct);
        if (refund.TransactionId.HasValue) usedRefundIds.Remove(refund.TransactionId.Value);
        if (usedRefundIds.Contains(transactionId)) return false;
        if ((await amazonStore.ListAllLinkedPaymentTransactionIdsAsync(ct)).Contains(transactionId)) return false;
        if (await db.Purchases.AsNoTracking().AnyAsync(other => other.TransactionId == transactionId, ct)) return false;

        return await amazonStore.SetRefundTransactionManualAsync(purchaseId, refundId, transactionId,
            confidence.HasValue ? Math.Clamp(confidence.Value, 0m, 1m) : null, DateTimeOffset.UtcNow, ct) > 0;
    }

    public async Task<bool> UnlinkRefundAsync(Guid userId, Guid fullWorthSpaceId, Guid purchaseId, Guid refundId, CancellationToken ct)
    {
        if (await authorization.GetAccessAsync(userId, fullWorthSpaceId, purchaseId, ct) != PurchaseAccessLevel.Write) return false;
        return await amazonStore.ClearRefundTransactionAsync(purchaseId, refundId, ct) > 0;
    }

    public async Task<AmazonPurchaseDetails?> GetDetailsAsync(Guid userId, Guid fullWorthSpaceId, Guid purchaseId, CancellationToken ct)
    {
        if (await authorization.GetAccessAsync(userId, fullWorthSpaceId, purchaseId, ct) == PurchaseAccessLevel.None) return null;
        var purchase = await db.Purchases.AsNoTracking().SingleOrDefaultAsync(x => x.Id == purchaseId && x.FullWorthSpaceId == fullWorthSpaceId && x.Source == "amazon", ct);
        if (purchase is null) return null;

        var links = await amazonStore.ListPaymentLinksAsync(purchaseId, ct);
        var transactionIds = links.Select(x => x.TransactionId).Distinct().ToArray();
        var txs = await db.Transactions.AsNoTracking().Where(x => transactionIds.Contains(x.Id)).ToListAsync(ct);
        var payments = links.Join(txs, link => link.TransactionId, tx => tx.Id, (link, tx) => new AmazonTransactionLinkView(
                tx.Id, tx.BookingDate, tx.Amount, link.AllocatedAmount, tx.Counterparty, link.MatchConfidence, link.Source))
            .OrderBy(x => x.BookingDate).ToList();

        if (payments.Count == 0 && purchase.TransactionId.HasValue)
        {
            var fallback = await db.Transactions.AsNoTracking().Where(x => x.Id == purchase.TransactionId.Value)
                .Select(tx => new AmazonTransactionLinkView(tx.Id, tx.BookingDate, tx.Amount,
                    Math.Min(Math.Abs(tx.Amount), purchase.TotalAmount), tx.Counterparty, purchase.MatchConfidence, "legacy"))
                .SingleOrDefaultAsync(ct);
            if (fallback is not null) payments.Add(fallback);
        }

        var refunds = (await amazonStore.ListRefundsAsync(purchaseId, ct))
            .Select(x => new AmazonRefundView(x.Id, x.ExternalRefundId, x.RefundDate, x.Amount, x.Currency, x.Status, x.Description, x.TransactionId, x.MatchConfidence))
            .ToList();
        var metadata = await amazonStore.GetOrderMetadataAsync(purchaseId, ct);
        return new(metadata?.ExternalStatus, metadata?.NonBankPaymentAmount ?? 0m, metadata?.NonBankPaymentSource ?? "amazon", payments, refunds);
    }

    private async Task<Guid?> UpsertAmazonOrderAsync(Guid userId, Guid fullWorthSpaceId, AmazonOrderSnapshot order, CancellationToken ct)
    {
        var existingPurchaseId = await db.Purchases.AsNoTracking()
            .Where(x => x.FullWorthSpaceId == fullWorthSpaceId && x.Source == "amazon" && x.ExternalOrderId == order.OrderId)
            .Select(x => (Guid?)x.Id)
            .SingleOrDefaultAsync(ct);
        if (existingPurchaseId.HasValue &&
            await authorization.GetAccessAsync(userId, fullWorthSpaceId, existingPurchaseId.Value, ct) != PurchaseAccessLevel.Write)
            return null;

        var oldItems = existingPurchaseId.HasValue
            ? await db.PurchaseItems.AsNoTracking().Where(x => x.PurchaseId == existingPurchaseId.Value).OrderBy(x => x.SortOrder).ThenBy(x => x.CreatedAt).ToListAsync(ct)
            : [];
        var oldByKey = oldItems.GroupBy(x => ItemKey(x.Asin, x.Name), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
        var consumed = new HashSet<Guid>();

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        Purchase purchase;
        var isNew = !existingPurchaseId.HasValue;
        if (existingPurchaseId.HasValue)
        {
            purchase = await db.Purchases.SingleAsync(x => x.Id == existingPurchaseId.Value && x.FullWorthSpaceId == fullWorthSpaceId, ct);
        }
        else
        {
            purchase = new Purchase
            {
                FullWorthSpaceId = fullWorthSpaceId,
                Source = "amazon",
                ExternalOrderId = order.OrderId,
                CreatedByUserId = userId,
                Visibility = "space",
                Status = "review",
                ReviewState = "needs_review",
                CreatedAt = DateTimeOffset.UtcNow
            };
            db.Purchases.Add(purchase);
        }

        var summaryChanged = isNew || purchase.PurchaseDate != order.PurchaseDate || purchase.TotalAmount != order.TotalAmount ||
            !string.Equals(purchase.Currency, order.Currency, StringComparison.OrdinalIgnoreCase) ||
            purchase.SubtotalAmount != order.SubtotalAmount || purchase.ShippingAmount != order.ShippingAmount ||
            !string.Equals(purchase.SourceReference, order.DetailUrl, StringComparison.Ordinal);
        purchase.Merchant = "Amazon";
        purchase.PurchaseDate = order.PurchaseDate;
        purchase.SubtotalAmount = order.SubtotalAmount;
        purchase.ShippingAmount = order.ShippingAmount;
        purchase.TotalAmount = order.TotalAmount;
        purchase.Currency = order.Currency;
        purchase.SourceReference = order.DetailUrl;
        if (summaryChanged)
        {
            purchase.Status = "review";
            purchase.ReviewState = "needs_review";
        }
        purchase.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        var purchaseId = purchase.Id;
        db.ChangeTracker.Clear();

        if (order.Items.Count > 0)
        {
            var writes = new List<PurchaseItemWrite>();
            foreach (var item in order.Items)
            {
                oldByKey.TryGetValue(ItemKey(item.Asin, item.Name), out var old);
                if (old is not null) consumed.Add(old.Id);
                writes.Add(ToAmazonWrite(item, old, order.Currency));
            }

            // A transient Amazon markup/parser change must never delete information the user already
            // reviewed. Keep unmatched old rows until Amazon explicitly gives us a safe identity for them.
            foreach (var old in oldItems.Where(x => !consumed.Contains(x.Id)))
                writes.Add(PreserveOldWrite(old));

            var itemOutcome = await authorization.ReplaceItemsForUserAsync(userId, fullWorthSpaceId, purchaseId, writes, ct);
            if (itemOutcome.Result != PurchaseMutationResult.Success)
            {
                await transaction.RollbackAsync(ct);
                return null;
            }
        }

        var imports = (order.Discounts ?? [])
            .Where(x => x.Amount > 0m)
            .Select(x => new PurchaseDiscountImport(
                PurchaseItemId: null,
                Type: x.Type,
                Label: x.Label,
                Amount: x.Amount,
                Percentage: null,
                CouponCode: x.CouponCode,
                RawText: x.RawText,
                Source: "amazon",
                Confidence: null))
            .ToList();
        var discountService = new PurchaseDiscountService(db, authorization);
        await discountService.ReplaceSourceDiscountsAsync(fullWorthSpaceId, purchaseId, "amazon", imports, ct);

        await transaction.CommitAsync(ct);
        return purchaseId;
    }

    private async Task ReconcilePaymentStatusAsync(Purchase purchase, CancellationToken ct)
    {
        var links = await amazonStore.ListPaymentLinksAsync(purchase.Id, ct);
        var metadata = await amazonStore.GetOrderMetadataAsync(purchase.Id, ct);
        var accounted = links.Sum(x => x.AllocatedAmount) + Math.Clamp(metadata?.NonBankPaymentAmount ?? 0m, 0m, purchase.TotalAmount);
        // Status can reflect payment accounting, but ReviewState is deliberately not promoted here.
        // Product-price history only accepts explicitly reviewed/confirmed purchase observations.
        purchase.Status = Math.Abs(accounted - purchase.TotalAmount) <= .01m ? "confirmed" : "review";
        purchase.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private Task SaveConnectionAsync(Guid userId, Guid fullWorthSpaceId, string storageState, CancellationToken ct)
    {
        var encrypted = cipher.Protect(storageState) ?? throw new InvalidOperationException("Amazon storage state encryption failed.");
        return amazonStore.UpsertConnectionAsync(userId, fullWorthSpaceId, encrypted, ct);
    }

    private static PurchaseItemWrite ToAmazonWrite(AmazonOrderItemSnapshot item, PurchaseItem? old, string currency)
    {
        var manuallyCorrected = old?.IsManuallyCorrected == true;
        var totalOverridden = old?.TotalPriceOverridden == true;
        return new PurchaseItemWrite(
            CategoryId: old?.CategorizationSource == "manual" ? old.CategoryId : null,
            Name: manuallyCorrected ? old!.Name : item.Name,
            Brand: old?.Brand,
            Sku: old?.Sku,
            Asin: item.Asin ?? old?.Asin,
            Quantity: manuallyCorrected ? old!.Quantity : item.Quantity,
            UnitPrice: manuallyCorrected ? old!.UnitPrice : item.UnitPrice ?? old?.UnitPrice,
            TotalPrice: totalOverridden ? old!.TotalPrice : item.TotalPrice,
            Currency: currency,
            Notes: old?.Notes,
            ProductId: old?.ProductId,
            RawName: item.Name,
            Barcode: old?.Barcode,
            QuantityUnit: old?.QuantityUnit,
            PackageQuantity: old?.PackageQuantity,
            PackageUnit: old?.PackageUnit,
            PackageCount: old?.PackageCount,
            BaseUnitPrice: null,
            DiscountAmount: item.DiscountAmount ?? (manuallyCorrected ? old?.DiscountAmount : null),
            DepositAmount: old?.DepositAmount,
            TaxRate: old?.TaxRate,
            TaxAmount: old?.TaxAmount,
            LineType: old?.LineType ?? "product",
            ExtractionConfidence: old?.ExtractionConfidence,
            IsManuallyCorrected: manuallyCorrected,
            TotalPriceOverridden: totalOverridden,
            SortOrder: old?.SortOrder,
            ReturnDeadline: old?.ReturnDeadline,
            WarrantyEnd: old?.WarrantyEnd,
            SerialNumber: old?.SerialNumber,
            OriginalUnitPrice: item.OriginalUnitPrice ?? (manuallyCorrected ? old?.OriginalUnitPrice : null),
            DiscountLabel: item.DiscountLabel ?? (manuallyCorrected ? old?.DiscountLabel : null));
    }

    private static PurchaseItemWrite PreserveOldWrite(PurchaseItem old) => new(
        CategoryId: old.CategoryId,
        Name: old.Name,
        Brand: old.Brand,
        Sku: old.Sku,
        Asin: old.Asin,
        Quantity: old.Quantity,
        UnitPrice: old.UnitPrice,
        TotalPrice: old.TotalPrice,
        Currency: old.Currency,
        Notes: old.Notes,
        ProductId: old.ProductId,
        RawName: old.RawName,
        Barcode: old.Barcode,
        QuantityUnit: old.QuantityUnit,
        PackageQuantity: old.PackageQuantity,
        PackageUnit: old.PackageUnit,
        PackageCount: old.PackageCount,
        BaseUnitPrice: old.BaseUnitPrice,
        DiscountAmount: old.DiscountAmount,
        DepositAmount: old.DepositAmount,
        TaxRate: old.TaxRate,
        TaxAmount: old.TaxAmount,
        LineType: old.LineType,
        ExtractionConfidence: old.ExtractionConfidence,
        IsManuallyCorrected: old.IsManuallyCorrected,
        TotalPriceOverridden: old.TotalPriceOverridden,
        SortOrder: old.SortOrder,
        ReturnDeadline: old.ReturnDeadline,
        WarrantyEnd: old.WarrantyEnd,
        SerialNumber: old.SerialNumber,
        OriginalUnitPrice: old.OriginalUnitPrice,
        DiscountLabel: old.DiscountLabel);

    private static string ItemKey(string? asin, string name) => !string.IsNullOrWhiteSpace(asin)
        ? $"asin:{asin.Trim().ToUpperInvariant()}"
        : $"name:{string.Join(' ', name.Trim().ToUpperInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries))}";

    private static bool IsAmazon(string? counterparty) => !string.IsNullOrWhiteSpace(counterparty) &&
        (counterparty.Contains("amazon", StringComparison.OrdinalIgnoreCase) || counterparty.Contains("amzn", StringComparison.OrdinalIgnoreCase));
}