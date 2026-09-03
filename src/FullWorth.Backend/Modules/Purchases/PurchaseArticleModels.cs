using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using FullWorth.Backend.Modules.Transactions;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Purchases;

[Table("Products")]
[Index(nameof(FullWorthSpaceId), nameof(CanonicalName))]
public sealed class Product
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FullWorthSpaceId { get; set; }
    [MaxLength(500)] public string CanonicalName { get; set; } = string.Empty;
    [MaxLength(250)] public string? Brand { get; set; }
    public Guid? DefaultCategoryId { get; set; }
    [MaxLength(32)] public string? DefaultQuantityUnit { get; set; }
    [Precision(20, 6)] public decimal? DefaultPackageQuantity { get; set; }
    [MaxLength(32)] public string? DefaultPackageUnit { get; set; }
    [MaxLength(1000)] public string? ImageReference { get; set; }
    [MaxLength(2000)] public string? Notes { get; set; }
    public bool IsArchived { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public ICollection<ProductAlias> Aliases { get; set; } = new List<ProductAlias>();
    public ICollection<ProductBarcode> Barcodes { get; set; } = new List<ProductBarcode>();
    public ICollection<PurchaseItem> PurchaseItems { get; set; } = new List<PurchaseItem>();
}

[Table("ProductAliases")]
[Index(nameof(ProductId), nameof(NormalizedAlias))]
public sealed class ProductAlias
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProductId { get; set; }
    public Product Product { get; set; } = null!;
    public Guid? MerchantId { get; set; }
    [MaxLength(500)] public string Alias { get; set; } = string.Empty;
    [MaxLength(500)] public string NormalizedAlias { get; set; } = string.Empty;
    [MaxLength(32)] public string AliasType { get; set; } = "manual";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

[Table("ProductBarcodes")]
[Index(nameof(ProductId))]
[Index(nameof(Code), IsUnique = true)]
public sealed class ProductBarcode
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProductId { get; set; }
    public Product Product { get; set; } = null!;
    [MaxLength(64)] public string Code { get; set; } = string.Empty;
    [MaxLength(16)] public string Standard { get; set; } = "unknown";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

[Table("PurchasePaymentLinks")]
[Index(nameof(PurchaseId))]
[Index(nameof(TransactionId))]
[Index(nameof(PurchaseId), nameof(TransactionId), IsUnique = true)]
public sealed class PurchasePaymentLink
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FullWorthSpaceId { get; set; }
    public Guid PurchaseId { get; set; }
    public Purchase Purchase { get; set; } = null!;
    public Guid TransactionId { get; set; }
    [Precision(20, 8)] public decimal Amount { get; set; }
    [MaxLength(3)] public string Currency { get; set; } = "EUR";
    [MaxLength(32)] public string LinkSource { get; set; } = "manual";
    [Precision(5, 4)] public decimal? Confidence { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// A real promotion/discount observed on this purchase. Amount is always the positive amount saved;
/// assignment to an item is optional because basket coupons must remain basket-level source data.
/// </summary>
[Table("PurchaseDiscounts")]
[Index(nameof(PurchaseId), nameof(CreatedAt))]
[Index(nameof(PurchaseItemId))]
public sealed class PurchaseDiscount
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PurchaseId { get; set; }
    public Purchase Purchase { get; set; } = null!;
    public Guid? PurchaseItemId { get; set; }
    [DeleteBehavior(DeleteBehavior.SetNull)]
    public PurchaseItem? PurchaseItem { get; set; }
    [MaxLength(32)] public string Type { get; set; } = "other";
    [MaxLength(250)] public string Label { get; set; } = string.Empty;
    [Precision(20, 8)] public decimal Amount { get; set; }
    [Precision(8, 4)] public decimal? Percentage { get; set; }
    [MaxLength(120)] public string? CouponCode { get; set; }
    [MaxLength(1000)] public string? RawText { get; set; }
    [MaxLength(32)] public string Source { get; set; } = "manual";
    [Precision(5, 4)] public decimal? Confidence { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public ICollection<PurchaseAllocationLink> AllocationLinks { get; set; } = new List<PurchaseAllocationLink>();
}

/// <summary>
/// Provenance for TransactionAllocation rows created from a purchase. Manual transaction splits have
/// no row here. This lets confirmation safely replace its own article/coupon/rounding allocations while
/// never touching unrelated user-created splits.
/// </summary>
[Table("PurchaseAllocationLinks")]
[Index(nameof(TransactionAllocationId), IsUnique = true)]
[Index(nameof(PurchaseId))]
[Index(nameof(PurchaseDiscountId))]
public sealed class PurchaseAllocationLink
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TransactionAllocationId { get; set; }
    public TransactionAllocation TransactionAllocation { get; set; } = null!;
    public Guid PurchaseId { get; set; }
    public Purchase Purchase { get; set; } = null!;
    public Guid? PurchaseDiscountId { get; set; }
    [DeleteBehavior(DeleteBehavior.SetNull)]
    public PurchaseDiscount? PurchaseDiscount { get; set; }
    [MaxLength(32)] public string AllocationType { get; set; } = "article";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

[Table("PurchaseDocuments")]
[Index(nameof(PurchaseId))]
[Index(nameof(Sha256))]
public sealed class PurchaseDocument
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PurchaseId { get; set; }
    public Purchase Purchase { get; set; } = null!;
    [MaxLength(32)] public string DocumentType { get; set; } = "receipt";
    [MaxLength(500)] public string OriginalFileName { get; set; } = string.Empty;
    [MaxLength(150)] public string MediaType { get; set; } = "application/octet-stream";
    [MaxLength(1000)] public string StoragePath { get; set; } = string.Empty;
    [MaxLength(64)] public string Sha256 { get; set; } = string.Empty;
    [MaxLength(64)] public string? PerceptualHash { get; set; }
    public int? PageCount { get; set; }
    public long SizeBytes { get; set; }
    [MaxLength(32)] public string Status { get; set; } = "uploaded";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public ICollection<PurchaseExtractionRun> ExtractionRuns { get; set; } = new List<PurchaseExtractionRun>();
}

[Table("PurchaseExtractionRuns")]
[Index(nameof(PurchaseDocumentId), nameof(CreatedAt))]
public sealed class PurchaseExtractionRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PurchaseDocumentId { get; set; }
    public PurchaseDocument PurchaseDocument { get; set; } = null!;
    [MaxLength(64)] public string Provider { get; set; } = "none";
    [MaxLength(64)] public string? ProviderVersion { get; set; }
    [MaxLength(32)] public string Status { get; set; } = "processing";
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    [MaxLength(80)] public string? ErrorCode { get; set; }
    [MaxLength(500)] public string? ErrorMessageSafe { get; set; }
    public string? RawResultJson { get; set; }
    public string? NormalizedResultJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

[Table("PurchaseDifferenceAcceptances")]
[Index(nameof(PurchaseId), nameof(Kind), IsUnique = true)]
public sealed class PurchaseDifferenceAcceptance
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PurchaseId { get; set; }
    public Purchase Purchase { get; set; } = null!;
    [MaxLength(16)] public string Kind { get; set; } = "items";
    [Precision(20, 8)] public decimal Amount { get; set; }
    [MaxLength(32)] public string Reason { get; set; } = "other";
    [MaxLength(500)] public string? Note { get; set; }
    public Guid AcceptedByUserId { get; set; }
    public DateTimeOffset AcceptedAt { get; set; } = DateTimeOffset.UtcNow;
}

[Table("PurchaseItemReturns")]
[Index(nameof(PurchaseItemId))]
[Index(nameof(RefundTransactionId))]
public sealed class PurchaseItemReturn
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PurchaseItemId { get; set; }
    public PurchaseItem PurchaseItem { get; set; } = null!;
    public Guid? RefundTransactionId { get; set; }
    [Precision(20, 6)] public decimal Quantity { get; set; }
    [Precision(20, 8)] public decimal Amount { get; set; }
    [MaxLength(3)] public string Currency { get; set; } = "EUR";
    [MaxLength(32)] public string Status { get; set; } = "refunded";
    [MaxLength(500)] public string? Note { get; set; }
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

[Table("FinanceTags")]
[Index(nameof(FullWorthSpaceId), nameof(NormalizedName), IsUnique = true)]
public sealed class FinanceTag
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FullWorthSpaceId { get; set; }
    [MaxLength(100)] public string Name { get; set; } = string.Empty;
    [MaxLength(100)] public string NormalizedName { get; set; } = string.Empty;
    // Optional presentation colour shared with the category-intelligence tag UI (#RRGGBB or #RRGGBBAA).
    [MaxLength(9)] public string? Color { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public ICollection<PurchaseTagLink> PurchaseLinks { get; set; } = new List<PurchaseTagLink>();
}

[Table("PurchaseTagLinks")]
[Index(nameof(PurchaseId), nameof(TagId), IsUnique = true)]
public sealed class PurchaseTagLink
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PurchaseId { get; set; }
    public Purchase Purchase { get; set; } = null!;
    public Guid TagId { get; set; }
    public FinanceTag Tag { get; set; } = null!;
}
