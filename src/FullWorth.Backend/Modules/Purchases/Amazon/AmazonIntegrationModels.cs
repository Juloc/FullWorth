namespace FullWorth.Backend.Modules.Purchases.Amazon;

public sealed class AmazonIntegrationOptions
{
    public const string SectionName = "AmazonIntegration";
    public bool Enabled { get; set; } = true;
    public int InitialHistoryDays { get; set; } = 90;
    // Amazon buyer accounts cannot realistically predate Amazon. 100 years therefore means
    // "all available history" while still keeping the browser year loop bounded.
    public int MaxHistoryDays { get; set; } = 36500;
    public int LoginChallengeMinutes { get; set; } = 10;
    public int NavigationTimeoutSeconds { get; set; } = 45;
    public int SyncIntervalHours { get; set; } = 24;
    public int MaxOrdersPerSync { get; set; } = 5000;
}

public sealed class AmazonConnection
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FullWorthSpaceId { get; set; }
    public Guid UserId { get; set; }
    public string Marketplace { get; set; } = "amazon.de";
    public string EncryptedStorageState { get; set; } = string.Empty;
    public string Status { get; set; } = "connected";
    public DateTimeOffset? LastSyncAt { get; set; }
    public DateTimeOffset? LastSuccessfulSyncAt { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class AmazonOrderMetadata
{
    public Guid PurchaseId { get; set; }
    public string? ExternalStatus { get; set; }
    public decimal NonBankPaymentAmount { get; set; }
    public string NonBankPaymentSource { get; set; } = "amazon";
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class PurchaseTransactionLink
{
    public Guid PurchaseId { get; set; }
    public Guid TransactionId { get; set; }
    // Positive amount of this bank transaction allocated to this purchase. A single transaction may
    // therefore cover multiple Amazon orders, while one order may still have multiple transactions.
    public decimal AllocatedAmount { get; set; }
    public decimal? MatchConfidence { get; set; }
    public string Source { get; set; } = "manual";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class PurchaseRefund
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PurchaseId { get; set; }
    public string ExternalRefundId { get; set; } = string.Empty;
    public DateOnly? RefundDate { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "EUR";
    public string Status { get; set; } = "refund";
    public string? Description { get; set; }
    public Guid? TransactionId { get; set; }
    public decimal? MatchConfidence { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed record AmazonLoginStartRequest(string Email, string Password);
public sealed record AmazonLoginCompleteRequest(string? Otp);
public sealed record AmazonSyncRequest(int? HistoryDays);
public sealed record AmazonPaymentLinkRequest(Guid TransactionId, decimal? Confidence, decimal? AllocatedAmount);
public sealed record AmazonNonBankPaymentRequest(decimal Amount);

public sealed record AmazonConnectionStatus(
    bool Connected,
    string Status,
    DateTimeOffset? LastSyncAt,
    DateTimeOffset? LastSuccessfulSyncAt,
    string? LastError);

public sealed record AmazonLoginResult(
    string Status,
    Guid? ChallengeId = null,
    string? Message = null);

public sealed record AmazonSyncResult(
    int OrdersFound,
    int OrdersImported,
    int PaymentsLinked,
    int RefundsLinked,
    DateTimeOffset SyncedAt);

public sealed record AmazonBrowserReadResult(
    IReadOnlyList<AmazonOrderSnapshot> Orders,
    string StorageState);

public sealed record AmazonOrderSnapshot(
    string OrderId,
    DateOnly PurchaseDate,
    decimal TotalAmount,
    string Currency,
    decimal NonBankPaymentAmount,
    string? ExternalStatus,
    string DetailUrl,
    IReadOnlyList<AmazonOrderItemSnapshot> Items,
    IReadOnlyList<AmazonRefundSnapshot> Refunds,
    decimal? SubtotalAmount = null,
    decimal? ShippingAmount = null,
    IReadOnlyList<AmazonDiscountSnapshot>? Discounts = null);

public sealed record AmazonOrderItemSnapshot(
    string Name,
    string? Asin,
    decimal Quantity,
    decimal? UnitPrice,
    decimal TotalPrice,
    decimal? OriginalUnitPrice = null,
    decimal? DiscountAmount = null,
    string? DiscountLabel = null);

public sealed record AmazonDiscountSnapshot(
    string Type,
    string Label,
    decimal Amount,
    string? CouponCode = null,
    string? RawText = null);

public sealed record AmazonRefundSnapshot(
    string ExternalRefundId,
    DateOnly? RefundDate,
    decimal Amount,
    string Currency,
    string Status,
    string? Description);

public sealed record AmazonPaymentCandidateView(
    Guid TransactionId,
    DateOnly? BookingDate,
    decimal Amount,
    decimal AvailableAmount,
    decimal SuggestedAllocation,
    string? Counterparty,
    string? Description,
    decimal Confidence);

public sealed record AmazonRefundCandidateView(
    Guid TransactionId,
    DateOnly? BookingDate,
    decimal Amount,
    string? Counterparty,
    string? Description,
    decimal Confidence);

public sealed record AmazonPurchaseDetails(
    string? ExternalStatus,
    decimal NonBankPaymentAmount,
    string NonBankPaymentSource,
    IReadOnlyList<AmazonTransactionLinkView> Payments,
    IReadOnlyList<AmazonRefundView> Refunds);

public sealed record AmazonTransactionLinkView(
    Guid TransactionId,
    DateOnly? BookingDate,
    decimal Amount,
    decimal AllocatedAmount,
    string? Counterparty,
    decimal? MatchConfidence,
    string Source);

public sealed record AmazonRefundView(
    Guid Id,
    string ExternalRefundId,
    DateOnly? RefundDate,
    decimal Amount,
    string Currency,
    string Status,
    string? Description,
    Guid? TransactionId,
    decimal? MatchConfidence);