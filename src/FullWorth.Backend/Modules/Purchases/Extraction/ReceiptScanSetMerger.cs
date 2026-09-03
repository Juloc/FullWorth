using System.Globalization;
using System.Text;

namespace FullWorth.Backend.Modules.Purchases.Extraction;

public sealed record ReceiptSourceExtraction(int SortOrder, ReceiptExtractionResult Result);

public sealed record ReceiptMergedLineItem(ReceiptLineItem Item, int SourceOrder);

public sealed record ReceiptMergedExtraction(
    string Provider,
    string? Merchant,
    DateOnly? PurchaseDate,
    string? Currency,
    decimal? Total,
    decimal? Discounts,
    decimal? Deposits,
    decimal? Taxes,
    IReadOnlyList<ReceiptMergedLineItem> Items,
    decimal Confidence,
    IReadOnlyList<string> Warnings);

public static class ReceiptScanSetMerger
{
    public static ReceiptMergedExtraction Merge(IReadOnlyList<ReceiptSourceExtraction> sourceResults)
    {
        if (sourceResults.Count == 0)
            return new("none", null, null, null, null, null, null, null, [], 0m, []);

        var ordered = sourceResults.OrderBy(x => x.SortOrder).ToList();
        var warnings = new List<string>();
        var mergedItems = new List<ReceiptMergedLineItem>();

        foreach (var source in ordered)
        {
            var incoming = (source.Result.Items ?? []).ToList();
            var overlap = FindBoundaryOverlap(mergedItems.Select(x => x.Item).ToList(), incoming);
            if (overlap >= 2)
            {
                warnings.Add($"Automatically removed {overlap} repeated overlap lines before source {source.SortOrder + 1}.");
                incoming = incoming.Skip(overlap).ToList();
            }
            else if (overlap == 1)
            {
                // One identical line is not enough evidence: buying the same product twice is legitimate.
                warnings.Add($"Possible one-line overlap before source {source.SortOrder + 1}; kept for review.");
            }

            mergedItems.AddRange(incoming.Select(item => new ReceiptMergedLineItem(item, source.SortOrder)));
        }

        var providers = ordered.Select(x => x.Result.Provider).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var confidenceValues = ordered.Select(x => x.Result.Confidence).Where(x => x > 0m).ToList();

        return new ReceiptMergedExtraction(
            Provider: providers.Count == 0 ? "none" : string.Join("+", providers),
            Merchant: ordered.Select(x => x.Result.Merchant).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)),
            PurchaseDate: ordered.Select(x => x.Result.PurchaseDate).FirstOrDefault(x => x.HasValue),
            Currency: ordered.Select(x => x.Result.Currency).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)),
            // Receipt-level totals normally live at the bottom of a long receipt. Prefer the last detected
            // value instead of summing pages, which would double-count totals repeated in photo overlap.
            Total: LastDetected(ordered, x => x.Result.Total),
            Discounts: LastDetected(ordered, x => x.Result.Discounts),
            Deposits: LastDetected(ordered, x => x.Result.Deposits),
            Taxes: LastDetected(ordered, x => x.Result.Taxes),
            Items: mergedItems,
            Confidence: confidenceValues.Count == 0 ? 0m : confidenceValues.Average(),
            Warnings: warnings);
    }

    public static int FindBoundaryOverlap(IReadOnlyList<ReceiptLineItem> previous, IReadOnlyList<ReceiptLineItem> next)
    {
        var max = Math.Min(10, Math.Min(previous.Count, next.Count));
        for (var length = max; length >= 1; length--)
        {
            var matches = true;
            for (var i = 0; i < length; i++)
            {
                if (!string.Equals(Signature(previous[previous.Count - length + i]), Signature(next[i]), StringComparison.Ordinal))
                {
                    matches = false;
                    break;
                }
            }
            if (matches) return length;
        }
        return 0;
    }

    private static decimal? LastDetected(IReadOnlyList<ReceiptSourceExtraction> ordered, Func<ReceiptSourceExtraction, decimal?> selector)
    {
        for (var index = ordered.Count - 1; index >= 0; index--)
        {
            var value = selector(ordered[index]);
            if (value.HasValue) return value;
        }
        return null;
    }

    private static string Signature(ReceiptLineItem item)
    {
        var name = Normalize(item.Name);
        return string.Join("|",
            name,
            Number(item.Quantity),
            Number(item.UnitPrice),
            Number(item.TotalPrice));
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var builder = new StringBuilder(value.Length);
        foreach (var character in value.Normalize(NormalizationForm.FormKC).ToUpperInvariant())
            if (char.IsLetterOrDigit(character)) builder.Append(character);
        return builder.ToString();
    }

    private static string Number(decimal? value) => value.HasValue
        ? Math.Round(value.Value, 2, MidpointRounding.AwayFromZero).ToString("0.00", CultureInfo.InvariantCulture)
        : string.Empty;
}
