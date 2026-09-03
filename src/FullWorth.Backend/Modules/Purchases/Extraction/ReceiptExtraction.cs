using FullWorth.Backend.Validation;
using Microsoft.Extensions.Options;

namespace FullWorth.Backend.Modules.Purchases.Extraction;

/// <summary>
/// A normalized line item detected on a receipt. Existing providers can keep supplying the original
/// six fields; the optional fields expose the same canonical price/deposit semantics used by GPT.
/// </summary>
public sealed record ReceiptLineItem(
    string Name,
    decimal? Quantity,
    decimal? UnitPrice,
    decimal? TotalPrice,
    string? CategoryHint,
    decimal Confidence,
    string? QuantityUnit = null,
    decimal? OriginalUnitPrice = null,
    decimal? DiscountAmount = null,
    string? DiscountLabel = null,
    decimal? DepositAmount = null,
    string? LineType = null);

/// <summary>
/// Canonical positive saving detected by a provider. ItemIndex, when present, is relative to that
/// provider result's Items collection. Normalization remaps it when provider adjustment rows are removed;
/// queue merging resolves it again only after overlap de-duplication.
/// </summary>
public sealed record ReceiptDiscount(
    string Type,
    string? Label,
    decimal Amount,
    decimal? Percentage = null,
    string? CouponCode = null,
    string? RawText = null,
    decimal Confidence = 0m,
    int? ItemIndex = null);

/// <summary>
/// The provider-agnostic result of extracting structured data from a receipt. The original positional
/// contract remains source-compatible; optional canonical totals/discount rows let local providers use
/// exactly the same persistence path as GPT without manufacturing fake item rows.
/// </summary>
public sealed record ReceiptExtractionResult(
    string Provider,
    string? Merchant,
    DateOnly? PurchaseDate,
    string? Currency,
    decimal? Total,
    decimal? Discounts,
    decimal? Deposits,
    decimal? Taxes,
    IReadOnlyList<ReceiptLineItem> Items,
    decimal Confidence,
    decimal? Subtotal = null,
    decimal? Rounding = null,
    decimal? Tip = null,
    decimal? Shipping = null,
    decimal? Fees = null,
    IReadOnlyList<ReceiptDiscount>? StructuredDiscounts = null)
{
    public static ReceiptExtractionResult Empty(string provider) =>
        new(provider, null, null, null, null, null, null, null, [], 0m, StructuredDiscounts: []);
}

/// <summary>A receipt to extract from — raw bytes plus what little context the caller has.</summary>
public sealed record ReceiptExtractionRequest(byte[] Content, string ContentType, string FileName, string? CurrencyHint = null);

/// <summary>A pluggable receipt extraction provider. Implementations name themselves via <see cref="Provider"/>.</summary>
public interface IReceiptExtractor
{
    string Provider { get; }
    Task<ReceiptExtractionResult> ExtractAsync(ReceiptExtractionRequest request, CancellationToken ct);
}

/// <summary>
/// The default, always-available extractor: extracts nothing. It keeps the app fully functional with
/// no OCR/AI vendor configured (manual entry still works), and is the safe fallback the service uses
/// when no real provider is selected.
/// </summary>
public sealed class NullReceiptExtractor : IReceiptExtractor
{
    public string Provider => "none";

    public Task<ReceiptExtractionResult> ExtractAsync(ReceiptExtractionRequest request, CancellationToken ct) =>
        Task.FromResult(ReceiptExtractionResult.Empty(Provider));
}

public sealed class ReceiptExtractionOptions
{
    public const string SectionName = "ReceiptExtraction";

    /// <summary>Name of the extractor to use (matches <see cref="IReceiptExtractor.Provider"/>). Default: none.</summary>
    public string Provider { get; set; } = "none";

    /// <summary>Tesseract OCR languages (e.g. "deu+eng"). Only used by the tesseract provider.</summary>
    public string Languages { get; set; } = "deu+eng";

    /// <summary>Path/name of the tesseract executable. Only used by the tesseract provider.</summary>
    public string TesseractPath { get; set; } = "tesseract";
}

/// <summary>
/// Selects the configured <see cref="IReceiptExtractor"/> and normalizes its output. Provider
/// implementations stay free of persistence/review concerns; this service only picks one, runs it,
/// and cleans the result. All recognized discounts/deposits are canonical positive amounts; rounding
/// remains signed because it can move the payable amount in either direction.
/// </summary>
public sealed class ReceiptExtractionService
{
    private static readonly HashSet<string> DiscountTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "price_reduction", "percentage", "coupon", "loyalty", "multibuy", "bundle",
        "employee", "promotion", "other"
    };

    private readonly IReadOnlyDictionary<string, IReceiptExtractor> extractorsByProvider;
    private readonly string configuredProvider;

    public ReceiptExtractionService(IEnumerable<IReceiptExtractor> extractors, IOptions<ReceiptExtractionOptions> options)
    {
        extractorsByProvider = extractors
            .GroupBy(extractor => extractor.Provider, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
        var configured = options.Value.Provider;
        configuredProvider = string.IsNullOrWhiteSpace(configured) ? "none" : configured.Trim();
    }

    /// <summary>The provider that will actually run, or "none" when the configured one is not registered.</summary>
    public string ActiveProvider => extractorsByProvider.ContainsKey(configuredProvider) ? configuredProvider : "none";

    public bool IsProviderAvailable => extractorsByProvider.ContainsKey(configuredProvider) && !string.Equals(configuredProvider, "none", StringComparison.OrdinalIgnoreCase);

    public async Task<ReceiptExtractionResult> ExtractAsync(ReceiptExtractionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!extractorsByProvider.TryGetValue(configuredProvider, out var extractor))
            return ReceiptExtractionResult.Empty(configuredProvider); // graceful: unknown/unconfigured provider
        return Normalize(await extractor.ExtractAsync(request, ct));
    }

    /// <summary>
    /// Clean a raw provider result into a canonical, safe-to-persist shape. Provider rows whose type is
    /// deposit/discount/shipping/fee/tip are compatibility input only: they are rolled into the dedicated
    /// amount fields and removed from Items. If an aggregate field was supplied by the provider, that
    /// aggregate is authoritative so the same visible adjustment cannot be counted twice.
    /// </summary>
    public static ReceiptExtractionResult Normalize(ReceiptExtractionResult raw)
    {
        ArgumentNullException.ThrowIfNull(raw);

        var items = new List<ReceiptLineItem>();
        var originalToCanonicalIndex = new Dictionary<int, int>();
        decimal compatibilityDeposits = 0m;
        decimal compatibilityTip = 0m;
        decimal compatibilityShipping = 0m;
        decimal compatibilityFees = 0m;
        var compatibilityDiscounts = new List<ReceiptDiscount>();

        var rawItems = raw.Items ?? [];
        for (var originalIndex = 0; originalIndex < rawItems.Count; originalIndex++)
        {
            var item = NormalizeItem(rawItems[originalIndex]);
            if (item is null) continue;
            var type = NormalizeLineType(item.LineType);
            switch (type)
            {
                case "deposit":
                    compatibilityDeposits += PositiveValue(item.DepositAmount ?? item.TotalPrice);
                    break;
                case "discount":
                {
                    var amount = PositiveValue(item.DiscountAmount ?? item.TotalPrice);
                    if (amount > 0m)
                        compatibilityDiscounts.Add(new ReceiptDiscount(
                            "other",
                            item.DiscountLabel ?? item.Name,
                            amount,
                            RawText: item.Name,
                            Confidence: item.Confidence));
                    break;
                }
                case "shipping":
                    compatibilityShipping += PositiveValue(item.TotalPrice);
                    break;
                case "fee":
                    compatibilityFees += PositiveValue(item.TotalPrice);
                    break;
                case "tip":
                    compatibilityTip += PositiveValue(item.TotalPrice);
                    break;
                default:
                    originalToCanonicalIndex[originalIndex] = items.Count;
                    items.Add(item with { LineType = type });
                    break;
            }
        }

        var structuredDiscounts = new List<ReceiptDiscount>();
        foreach (var candidate in raw.StructuredDiscounts ?? [])
        {
            var normalized = NormalizeDiscount(candidate);
            if (normalized is null) continue;
            if (normalized.ItemIndex.HasValue)
                normalized = normalized with
                {
                    ItemIndex = originalToCanonicalIndex.TryGetValue(normalized.ItemIndex.Value, out var canonicalIndex)
                        ? canonicalIndex
                        : null
                };
            AddDiscountIfDistinct(structuredDiscounts, normalized);
        }
        foreach (var compatibility in compatibilityDiscounts)
            AddDiscountIfDistinct(structuredDiscounts, compatibility);

        // Item mirrors are also real savings when a provider does not emit a separate structured row.
        // Do not add them twice when a structured discount already points at the same canonical item.
        var itemMirrorDiscountTotal = 0m;
        for (var index = 0; index < items.Count; index++)
        {
            var amount = PositiveValue(items[index].DiscountAmount);
            if (amount <= 0m) continue;
            var represented = structuredDiscounts.Any(x => x.ItemIndex == index && Math.Abs(x.Amount - amount) <= 0.01m);
            if (!represented) itemMirrorDiscountTotal += amount;
        }

        var structuredTotal = structuredDiscounts.Sum(x => x.Amount) + itemMirrorDiscountTotal;
        var aggregateDiscount = raw.Discounts.HasValue
            ? PositiveMoney(raw.Discounts)
            : structuredTotal > 0m ? Round(structuredTotal) : null;

        var productDeposits = items.Sum(x => PositiveValue(x.DepositAmount));
        var deposits = raw.Deposits.HasValue
            ? PositiveMoney(raw.Deposits)
            : PositiveMoney(productDeposits + compatibilityDeposits);
        var tip = raw.Tip.HasValue ? PositiveMoney(raw.Tip) : PositiveMoney(compatibilityTip);
        var shipping = raw.Shipping.HasValue ? PositiveMoney(raw.Shipping) : PositiveMoney(compatibilityShipping);
        var fees = raw.Fees.HasValue ? PositiveMoney(raw.Fees) : PositiveMoney(compatibilityFees);

        return raw with
        {
            Merchant = Trimmed(raw.Merchant),
            Currency = raw.Currency is not null && Validate.IsCurrency(raw.Currency) ? raw.Currency.Trim().ToUpperInvariant() : null,
            Total = raw.Total.HasValue ? Round(Math.Abs(raw.Total.Value)) : null,
            Discounts = aggregateDiscount,
            Deposits = deposits,
            Taxes = PositiveMoney(raw.Taxes),
            Subtotal = PositiveMoney(raw.Subtotal),
            Rounding = Round(raw.Rounding),
            Tip = tip,
            Shipping = shipping,
            Fees = fees,
            StructuredDiscounts = structuredDiscounts,
            Items = items,
            Confidence = Clamp(raw.Confidence),
        };
    }

    private static ReceiptLineItem? NormalizeItem(ReceiptLineItem item)
    {
        var name = Trimmed(item.Name);
        if (name is null) return null; // a line item with no name is unusable

        var quantity = item.Quantity is > 0m ? item.Quantity : null;
        var total = item.TotalPrice ?? (quantity is { } q && item.UnitPrice is { } unit ? q * unit : null);
        var lineType = NormalizeLineType(item.LineType);
        return item with
        {
            Name = name,
            Quantity = quantity,
            QuantityUnit = NormalizeUnit(item.QuantityUnit),
            UnitPrice = Round(item.UnitPrice),
            OriginalUnitPrice = Round(item.OriginalUnitPrice),
            TotalPrice = Round(total),
            DiscountAmount = PositiveMoney(item.DiscountAmount),
            DiscountLabel = Trimmed(item.DiscountLabel),
            DepositAmount = PositiveMoney(item.DepositAmount),
            LineType = lineType,
            CategoryHint = Trimmed(item.CategoryHint),
            Confidence = Clamp(item.Confidence),
        };
    }

    private static ReceiptDiscount? NormalizeDiscount(ReceiptDiscount discount)
    {
        var amount = Math.Abs(discount.Amount);
        if (amount <= 0m) return null;
        var type = string.IsNullOrWhiteSpace(discount.Type) ? "other" : discount.Type.Trim().ToLowerInvariant();
        if (!DiscountTypes.Contains(type)) type = "other";
        return discount with
        {
            Type = type,
            Label = Trimmed(discount.Label),
            Amount = Math.Round(amount, 2, MidpointRounding.AwayFromZero),
            Percentage = discount.Percentage.HasValue ? Math.Clamp(discount.Percentage.Value, 0m, 100m) : null,
            CouponCode = Trimmed(discount.CouponCode),
            RawText = Trimmed(discount.RawText),
            Confidence = Clamp(discount.Confidence),
            ItemIndex = discount.ItemIndex is >= 0 ? discount.ItemIndex : null
        };
    }

    private static void AddDiscountIfDistinct(List<ReceiptDiscount> rows, ReceiptDiscount candidate)
    {
        if (rows.Any(x =>
                x.ItemIndex == candidate.ItemIndex &&
                string.Equals(x.Type, candidate.Type, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.Label ?? string.Empty, candidate.Label ?? string.Empty, StringComparison.OrdinalIgnoreCase) &&
                Math.Abs(x.Amount - candidate.Amount) <= 0.01m))
            return;
        rows.Add(candidate);
    }

    private static string NormalizeLineType(string? value)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? "product" : value.Trim().ToLowerInvariant();
        return normalized switch
        {
            "pfand" => "deposit",
            "coupon" => "discount",
            "product" or "deposit" or "discount" or "shipping" or "fee" or "tip" or "unknown" => normalized,
            _ => "product"
        };
    }

    private static string? NormalizeUnit(string? value) => string.IsNullOrWhiteSpace(value)
        ? null
        : value.Trim().ToLowerInvariant();

    private static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static decimal? Round(decimal? value) => value is { } d ? Math.Round(d, 2, MidpointRounding.AwayFromZero) : null;
    private static decimal? PositiveMoney(decimal? value) => value.HasValue ? Math.Round(Math.Abs(value.Value), 2, MidpointRounding.AwayFromZero) : null;
    private static decimal PositiveValue(decimal? value) => value.HasValue ? Math.Round(Math.Abs(value.Value), 2, MidpointRounding.AwayFromZero) : 0m;
    private static decimal Clamp(decimal confidence) => Math.Clamp(confidence, 0m, 1m);
}