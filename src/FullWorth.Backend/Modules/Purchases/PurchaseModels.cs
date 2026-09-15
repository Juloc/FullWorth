using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Purchases;

public sealed class Purchase
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FullWorthSpaceId { get; set; }
    // Legacy single-payment link. Kept for backwards compatibility while PurchasePaymentLink is the
    // authoritative multi-payment model. New feature code writes the link table and mirrors this only
    // when exactly one payment is linked.
    public Guid? TransactionId { get; set; }
    public string Source { get; set; } = "receipt";
    public string Merchant { get; set; } = string.Empty;
    public Guid? MerchantId { get; set; }
    public string? MerchantRaw { get; set; }
    public string? ExternalOrderId { get; set; }
    public DateOnly? PurchaseDate { get; set; }
    public TimeOnly? PurchaseTime { get; set; }
    public string? TimeZone { get; set; }
    public decimal? SubtotalAmount { get; set; }
    /// <summary>Total recognized discount, stored as a positive saved amount.</summary>
    public decimal? DiscountAmount { get; set; }
    /// <summary>Total deposit/Pfand, stored as a positive amount added to the payment.</summary>
    public decimal? DepositAmount { get; set; }
    /// <summary>Explicit receipt/cash rounding. May be positive or negative.</summary>
    public decimal RoundingAmount { get; set; }
    public decimal? TaxAmount { get; set; }
    public decimal? TipAmount { get; set; }
    public decimal? ShippingAmount { get; set; }
    public decimal? FeeAmount { get; set; }
    public decimal TotalAmount { get; set; }
    public string Currency { get; set; } = "EUR";
    public string Status { get; set; } = "review";
    public string ReviewState { get; set; } = "needs_review";
    public decimal? MatchConfidence { get; set; }
    public string? ReceiptImagePath { get; set; }
    public string? ReceiptNumber { get; set; }
    public string? InvoiceNumber { get; set; }
    public string? PaymentMethodText { get; set; }
    public string? SourceReference { get; set; }
    public string? Notes { get; set; }
    public bool IsBookmarked { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public Guid? PaidByUserId { get; set; }
    public Guid? ForWhomUserId { get; set; }
    public string Visibility { get; set; } = "space";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public ICollection<PurchaseItem> Items { get; set; } = new List<PurchaseItem>();
    public ICollection<PurchasePaymentLink> PaymentLinks { get; set; } = new List<PurchasePaymentLink>();
    public ICollection<PurchaseDocument> Documents { get; set; } = new List<PurchaseDocument>();
    public ICollection<PurchaseDiscount> Discounts { get; set; } = new List<PurchaseDiscount>();
    public ICollection<PurchaseDifferenceAcceptance> AcceptedDifferences { get; set; } = new List<PurchaseDifferenceAcceptance>();
    public ICollection<PurchaseTagLink> Tags { get; set; } = new List<PurchaseTagLink>();
}

public sealed class PurchaseItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PurchaseId { get; set; }
    // Back-reference used by EF only; ignored for JSON so serializing a Purchase with its Items (e.g. the
    // transaction detail view) does not create a Purchase→Items→Purchase cycle.
    [JsonIgnore] public Purchase Purchase { get; set; } = null!;
    public Guid? ProductId { get; set; }
    public Product? Product { get; set; }
    public Guid? CategoryId { get; set; }
    public string RawName { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Brand { get; set; }
    public string? Sku { get; set; }
    public string? Barcode { get; set; }
    public string? Asin { get; set; }
    public decimal Quantity { get; set; } = 1m;
    public string QuantityUnit { get; set; } = "piece";
    public decimal? PackageQuantity { get; set; }
    public string? PackageUnit { get; set; }
    public decimal? PackageCount { get; set; }
    /// <summary>Effective charged unit price after item-level discounts.</summary>
    public decimal? UnitPrice { get; set; }
    /// <summary>Reference/original unit price only when explicitly shown or reliably imported.</summary>
    public decimal? OriginalUnitPrice { get; set; }
    public decimal? BaseUnitPrice { get; set; }
    /// <summary>Effective charged merchandise line total after item discount; deposit is separate.</summary>
    public decimal TotalPrice { get; set; }
    /// <summary>Positive amount saved on this item.</summary>
    public decimal? DiscountAmount { get; set; }
    public string? DiscountLabel { get; set; }
    /// <summary>Positive deposit/Pfand amount associated with this item.</summary>
    public decimal? DepositAmount { get; set; }
    public decimal? TaxRate { get; set; }
    public decimal? TaxAmount { get; set; }
    public string Currency { get; set; } = "EUR";
    public string LineType { get; set; } = "product";
    public string CategorizationSource { get; set; } = "none";
    public decimal? ExtractionConfidence { get; set; }
    public bool IsManuallyCorrected { get; set; }
    public bool TotalPriceOverridden { get; set; }
    public string? Notes { get; set; }
    public int SortOrder { get; set; }
    public DateOnly? ReturnDeadline { get; set; }
    public DateOnly? WarrantyEnd { get; set; }
    public string? SerialNumber { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public ICollection<PurchaseItemReturn> Returns { get; set; } = new List<PurchaseItemReturn>();
}
