using FullWorth.Backend.Validation;
using System.Globalization;
using System.Text.RegularExpressions;

namespace FullWorth.Backend.Modules.Purchases.Extraction;

/// <summary>
/// Turns the text of one receipt into the canonical shape the GPT path also produces - without any
/// guessing model. A cash-register receipt is regular: a block of "name  price  tax class" lines, a
/// SUMME line, then payment and tax tables. The parser reads that block, and the arithmetic of the
/// receipt itself is the check: items minus discounts plus deposits must equal the printed total.
/// A result whose sums agree is trustworthy (Confidence 0.9); one that does not is reported with a
/// low Confidence so nothing downstream confirms it by itself.
/// </summary>
public static partial class ReceiptTextParser
{
    /// <summary>
    /// Which generation of the reading rules produced a result. Bump it whenever a change can alter what
    /// a receipt reads as, so receipts read by an older generation can be found and read again.
    /// </summary>
    public const string Version = "2";

    /// <summary>The least confidence at which a locally read receipt may be taken without a human look.</summary>
    public const decimal TrustedConfidence = 0.85m;

    internal const decimal ConsistentConfidence = 0.9m;
    internal const decimal InconsistentConfidence = 0.4m;
    internal const decimal RepairedConfidence = 0.7m;

    public static ReceiptExtractionResult Parse(string text, string? currencyHint = null)
    {
        var lines = ReadLines(text);
        var merchant = ReceiptMerchants.Find(lines);
        var purchaseDate = FindDate(lines);
        var currency = text.Contains('€') || text.Contains("EUR", StringComparison.OrdinalIgnoreCase)
            ? "EUR"
            : (currencyHint is not null && Validate.IsCurrency(currencyHint) ? currencyHint.Trim().ToUpperInvariant() : null);

        var parsed = ReceiptBlock.Read(lines);
        var consistent = parsed.Total is { } total && Math.Abs(parsed.Net - total) <= 0.005m && parsed.Items.Count > 0;
        var complete = merchant is not null && purchaseDate is not null;
        var confidence = !consistent ? InconsistentConfidence : parsed.Repaired ? RepairedConfidence : complete ? ConsistentConfidence : 0.75m;

        return new ReceiptExtractionResult(
            Provider: "tesseract",
            Merchant: merchant,
            PurchaseDate: purchaseDate,
            Currency: currency,
            Total: parsed.Total,
            Discounts: parsed.Discounts.Count > 0 ? parsed.Discounts.Sum(x => x.Amount) : null,
            Deposits: parsed.Deposits > 0m ? parsed.Deposits : null,
            Taxes: null,
            Items: parsed.Items,
            Confidence: lines.Count == 0 ? 0m : confidence,
            Subtotal: parsed.Subtotal,
            Rounding: parsed.Rounding != 0m ? parsed.Rounding : null,
            Tip: parsed.Tip > 0m ? parsed.Tip : null,
            Shipping: parsed.Shipping > 0m ? parsed.Shipping : null,
            Fees: parsed.Fees > 0m ? parsed.Fees : null,
            StructuredDiscounts: parsed.Discounts);
    }

    /// <summary>
    /// Trimmed, non-empty lines. OCR often breaks an amount over two lines ("13," / "29"); those are
    /// joined back so the amount is read as one.
    /// </summary>
    private static List<string> ReadLines(string? text)
    {
        // "16, 21" and "2 ,49" are amounts the scan put a space into.
        var raw = SpacedAmountRegex().Replace(text ?? string.Empty, ",$1$2").Replace("\r", "").Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0).ToList();
        var lines = new List<string>(raw.Count);
        for (var i = 0; i < raw.Count; i++)
        {
            if (i + 1 < raw.Count && BrokenAmountHeadRegex().IsMatch(raw[i]) && BrokenAmountTailRegex().IsMatch(raw[i + 1]))
            {
                lines.Add(raw[i] + raw[i + 1]);
                i++;
            }
            else lines.Add(raw[i]);
        }
        return lines;
    }

    private static DateOnly? FindDate(IReadOnlyList<string> lines)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        foreach (var line in lines)
        {
            var iso = IsoDateRegex().Match(line);
            if (iso.Success && TryDate(iso.Groups[1].Value, iso.Groups[2].Value, iso.Groups[3].Value, today) is { } d1) return d1;

            var dotted = DottedDateRegex().Match(line);
            if (dotted.Success && TryDate(dotted.Groups[3].Value, dotted.Groups[2].Value, dotted.Groups[1].Value, today) is { } d2) return d2;
        }
        return null;
    }

    /// <summary>A date counts only when it is a real day between 2000 and tomorrow - an OCR slip ("3025") must not become a purchase date.</summary>
    private static DateOnly? TryDate(string year, string month, string day, DateOnly today)
    {
        if (!int.TryParse(year, NumberStyles.None, CultureInfo.InvariantCulture, out var y)
            || !int.TryParse(month, NumberStyles.None, CultureInfo.InvariantCulture, out var m)
            || !int.TryParse(day, NumberStyles.None, CultureInfo.InvariantCulture, out var d)) return null;
        if (y < 100) y += 2000;
        if (y < 2000 || m is < 1 or > 12 || d < 1 || d > DateTime.DaysInMonth(y, m)) return null;
        var date = new DateOnly(y, m, d);
        return date <= today.AddDays(1) ? date : null;
    }

    [GeneratedRegex(@"(?<=\d)[ \t]*,[ \t]+(\d{2})(?!\d)|(?<=\d)[ \t]+,(\d{2})(?!\d)")]
    private static partial Regex SpacedAmountRegex();

    [GeneratedRegex(@"\d,$")]
    private static partial Regex BrokenAmountHeadRegex();

    [GeneratedRegex(@"^\d{2}$")]
    private static partial Regex BrokenAmountTailRegex();

    [GeneratedRegex(@"(20\d{2})-(\d{2})-(\d{2})")]
    private static partial Regex IsoDateRegex();

    [GeneratedRegex(@"(?<!\d)(\d{1,2})\.(\d{1,2})\.(\d{2}|\d{4})(?!\d)")]
    private static partial Regex DottedDateRegex();
}
