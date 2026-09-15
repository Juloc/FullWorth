using System.Linq.Expressions;
using System.Text.Json;
using FullWorth.Backend.Modules.Merchants;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Transactions;

public sealed record TransactionStatusHistoryItem(
    string Status,
    string? FromStatus,
    DateTimeOffset ObservedAt);

internal sealed record TransactionStatusAuditRow(
    string Action,
    string? MetadataJson,
    DateTimeOffset OccurredAt);

public enum TransactionClassificationResult { Updated, NotFound, InvalidCategory }

public enum AllocationResult { Updated, NotFound, InvalidCategory, InvalidPurchaseItem, Unbalanced }

public enum RefundLinkResult { Updated, NotFound, Invalid }

public enum TransactionCreateResult { Created, NotFound, Forbidden, NotManual, InvalidCategory }

public enum TransactionDeleteResult { Deleted, NotFound, NotManual, Referenced }

public enum TransferLinkResult { Linked, NotFound, Invalid }

public enum TransferUnlinkResult { Unlinked, NotFound, NotLinked }

public sealed record TransactionListItem(
    Guid Id,
    Guid AccountId,
    string? Account,
    DateOnly? BookingDate,
    DateOnly? ValueDate,
    decimal Amount,
    string Currency,
    string? Counterparty,
    string? NormalizedCounterparty,
    string? Description,
    string? MerchantCategoryCode,
    string Status,
    Guid? CategoryId,
    string? CategoryName,
    string? CategoryIconKey,
    string? UserNote,
    bool IsIgnored,
    bool IsTransfer,
    string CategorizationSource,
    DateTimeOffset UpdatedAt,
    int PurchaseCount,
    int PurchaseItemCount,
    Guid? MerchantId,
    string? MerchantDisplayName,
    // The drawer read both of these off the row and neither existed, so `isManual` was always
    // undefined: the bank-details button rendered for EVERY transaction (and 404s on one that has no
    // provider pointer) while the delete action for a manual transaction rendered for none.
    bool IsManual = false,
    // Provider details are only fetchable with a provider transaction id, through a live non-FinTS
    // connection. FinTS details are already part of the imported transaction.
    bool HasProviderDetails = false);

public sealed record TransactionQuery(
    Guid? AccountId,
    Guid? CategoryId,
    DateOnly? From,
    DateOnly? To,
    string? Direction,
    string? Query,
    bool? IncludeIgnored,
    bool? TransfersOnly,
    string? Sort,
    string? Order,
    int? Offset,
    int? Limit,
    Guid? AccountGroupId = null,
    bool? IncludeDescendants = null,
    string? Merchant = null,
    decimal? MinAmount = null,
    decimal? MaxAmount = null,
    bool? RefundOnly = null,
    bool? HasReceipt = null,
    string? Status = null,
    bool? IgnoredOnly = null,
    Guid? MerchantId = null);

public sealed record TransactionClassification(Guid? CategoryId, bool IsIgnored, bool IsTransfer, string? TransferPurpose = null, string? UserNote = null);

public sealed record AllocationLine(Guid? CategoryId, decimal Amount, string? Note, Guid? PurchaseItemId = null);

public sealed record RefundLink(Guid? OriginalTransactionId, Guid? RefundCategoryId = null);

public sealed record CreateTransactionRequest(Guid AccountId, decimal Amount, string? Direction, DateOnly? Date, string? Currency, string? Counterparty, Guid? CategoryId, string? Note);

public sealed record TransferLinkRequest(Guid OtherTransactionId);
