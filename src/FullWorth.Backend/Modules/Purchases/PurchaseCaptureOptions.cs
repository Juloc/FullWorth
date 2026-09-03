namespace FullWorth.Backend.Modules.Purchases;

public sealed class PurchaseStorageOptions
{
    public const string SectionName = "PurchaseStorage";
    public string RootPath { get; set; } = "/data/purchases";
    public long MaxReceiptBytes { get; set; } = 20 * 1024 * 1024;
    // A user may select at most this many physical images/PDFs for one logical receipt. PDF expansion
    // is governed separately by MaxReceiptSources.
    public int MaxReceiptFiles { get; set; } = 20;
    // One logical receipt may consist of several physical photos/PDFs. Keep a separate aggregate cap
    // so a user cannot bypass the single-file limit by adding many sources to one draft scan.
    public long MaxReceiptSetBytes { get; set; } = 60 * 1024 * 1024;
    // Counts logical scan sources after PDF expansion. The UI limits physical selections to 20 files,
    // while a long PDF may expand to substantially more ordered pages. Keep the logical ceiling higher.
    public int MaxReceiptSources { get; set; } = 60;
}

/// <summary>
/// Provider-neutral structured receipt result. Optional fields are append-only so existing OCR/import
/// callers remain compatible. Discounts are canonical source rows; DiscountAmount remains the aggregate
/// mirror supplied by older providers and is never used to invent mechanics not present in Discounts.
/// </summary>
public sealed record PurchaseExtractionRequest(
    string Merchant,
    DateOnly? PurchaseDate,
    decimal TotalAmount,
    string Currency,
    IReadOnlyList<PurchaseItemWrite> Items,
    string? SourceReference,
    string? Notes,
    TimeOnly? PurchaseTime = null,
    decimal? SubtotalAmount = null,
    decimal? DiscountAmount = null,
    decimal? DepositAmount = null,
    decimal? TaxAmount = null,
    decimal? TipAmount = null,
    decimal? ShippingAmount = null,
    decimal? FeeAmount = null,
    string? ReceiptNumber = null,
    string? InvoiceNumber = null,
    string? PaymentMethodText = null,
    decimal? RoundingAmount = null,
    IReadOnlyList<PurchaseDiscountImport>? Discounts = null,
    string? DiscountSource = null,
    /// <summary>
    /// True means Item.TotalPrice already excludes deposit and item-level discounts are already reflected
    /// in the effective price. False is the compatibility contract for the pre-canonical queue mapper.
    /// </summary>
    bool AmountsAreCanonical = false);

public sealed record AmazonOrderImportRequest(
    string OrderId,
    DateOnly PurchaseDate,
    decimal TotalAmount,
    string Currency,
    IReadOnlyList<AmazonOrderItemImport> Items,
    string? SourceReference,
    decimal? SubtotalAmount = null,
    decimal? DiscountAmount = null,
    decimal? DepositAmount = null,
    decimal? TaxAmount = null,
    decimal? ShippingAmount = null,
    decimal? FeeAmount = null,
    decimal? RoundingAmount = null,
    IReadOnlyList<PurchaseDiscountImport>? Discounts = null);

public sealed record AmazonOrderItemImport(
    string Name,
    string? Brand,
    string? Asin,
    string? Sku,
    decimal Quantity,
    decimal? UnitPrice,
    decimal TotalPrice,
    Guid? CategoryId,
    decimal? OriginalUnitPrice = null,
    decimal? DiscountAmount = null,
    string? DiscountLabel = null,
    decimal? DepositAmount = null);
