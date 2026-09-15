using System.Linq.Expressions;
using System.Text.Json;
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
    // Keyed lookup token for the creditor/debtor account identifier. Never stores a plaintext IBAN.
    public string? CounterpartyAccountLookup { get; set; }
    public string? UserNote { get; set; }
    public bool IsIgnored { get; set; }
    public bool IsTransfer { get; set; }
    // True only when this booking is trusted for reconstructing historical account balances.
    // Provider and user-entered bookings are trusted; raw imports stay false until explicitly linked.
    public bool UseForBalanceHistory { get; set; } = true;
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
