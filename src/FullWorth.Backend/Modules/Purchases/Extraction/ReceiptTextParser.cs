using System.Globalization;
using System.Text.RegularExpressions;
using FullWorth.Backend.Validation;

namespace FullWorth.Backend.Modules.Purchases.Extraction;

/// <summary>
/// Pure heuristic parser that turns raw OCR text (from any local OCR engine) into the same canonical
/// shape used by the GPT path. It is deliberately conservative: tax is informational, discounts are
/// positive saved amounts, deposits are separate from merchandise, and rounding stays signed.
/// </summary>
public static partial class ReceiptTextParser
{
    private static readonly string[] TotalKeywords = ["summe", "gesamt", "total", "zu zahlen", "zahlbetrag", "endbetrag"];
    private static readonly string[] NonItemKeywords =
    [
        "summe", "gesamt", "total", "zwischensumme", "subtotal", "mwst", "ust", "steuer", "vat", "netto", "brutto",
        "rückgeld", "rueckgeld", "gegeben", "ec", "visa", "mastercard", "kartenzahlung",
        "trinkgeld", "tip", "pfand", "deposit", "rabatt", "coupon", "ersparnis", "promotion",
        "rundung", "rounding", "versand", "shipping", "gebühr", "gebuehr", "fee"
    ];

    public static ReceiptExtractionResult Parse(string text, string? currencyHint = null)
    {
        text ??= string.Empty;
        var lines = text.Replace("\r", "").Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0).ToList();

        var merchant = lines.FirstOrDefault(line => line.Any(char.IsLetter) && MoneyAmountRegex().Matches(line).Count == 0);
        var purchaseDate = FindDate(text);
        var currency = text.Contains('€') || text.Contains("EUR", StringComparison.OrdinalIgnoreCase)
            ? "EUR"
            : (currencyHint is not null && Validate.IsCurrency(currencyHint) ? currencyHint.Trim().ToUpperInvariant() : null);

        decimal? total = null;
        decimal? subtotal = null;
        decimal? deposits = null;
        decimal? taxes = null;
        decimal? rounding = null;
        decimal? tip = null;
        decimal? shipping = null;
        decimal? fees = null;
        var discounts = new List<ReceiptDiscount>();

        foreach (var line in lines)
        {
            var lower = line.ToLowerInvariant();
            var amount = LastAmount(line);
            if (!amount.HasValue) continue;

            if (lower.Contains("zwischensumme") || lower.Contains("subtotal"))
            {
                subtotal ??= Math.Abs(amount.Value);
                continue;
            }
            if (LooksLikeDiscountLine(lower) && !LooksLikePaymentBalance(lower))
            {
                var saved = Math.Abs(amount.Value);
                if (saved > 0m)
                    discounts.Add(new ReceiptDiscount(
                        Type: DiscountType(lower),
                        Label: line,
                        Amount: saved,
                        RawText: line,
                        Confidence: 0.55m));
                continue;
            }
            if (LooksLikePaymentBalance(lower)) continue;
            if (lower.Contains("pfand") || lower.Contains("deposit"))
            {
                deposits = (deposits ?? 0m) + Math.Abs(amount.Value);
                continue;
            }
            if (lower.Contains("rundung") || lower.Contains("rounding"))
            {
                rounding = (rounding ?? 0m) + amount.Value;
                continue;
            }
            if (lower.Contains("trinkgeld") || Regex.IsMatch(lower, @"\btip\b", RegexOptions.CultureInvariant))
            {
                tip = (tip ?? 0m) + Math.Abs(amount.Value);
                continue;
            }
            if (lower.Contains("versand") || lower.Contains("shipping") || lower.Contains("postage"))
            {
                shipping = (shipping ?? 0m) + Math.Abs(amount.Value);
                continue;
            }
            if (lower.Contains("gebühr") || lower.Contains("gebuehr") || Regex.IsMatch(lower, @"\bfee\b", RegexOptions.CultureInvariant))
            {
                fees = (fees ?? 0m) + Math.Abs(amount.Value);
                continue;
            }
            if (lower.Contains("mwst") || lower.Contains("ust") || lower.Contains("steuer") || Regex.IsMatch(lower, @"\bvat\b", RegexOptions.CultureInvariant))
            {
                taxes = (taxes ?? 0m) + Math.Abs(amount.Value);
                continue;
            }
            if (TotalKeywords.Any(lower.Contains))
            {
                total ??= Math.Abs(amount.Value);
            }
        }

        var items = new List<ReceiptLineItem>();
        foreach (var line in lines)
        {
            var lower = line.ToLowerInvariant();
            if (LooksLikeDiscountLine(lower) || LooksLikePaymentBalance(lower)) continue;
            // Match keywords on letter boundaries so product names that merely contain a keyword as a
            // substring (e.g. "Kaffee" contains "fee", "Kruste" contains "ust") are not dropped.
            if (NonItemKeywords.Any(keyword => ContainsKeyword(lower, keyword))) continue;
            if (IsPaymentOnlyLine(lower)) continue;
            if (lower.Contains("datum") || IsoDateRegex().IsMatch(line) || DottedDateRegex().IsMatch(line)) continue;
            var matches = MoneyAmountRegex().Matches(line);
            if (matches.Count == 0) continue;
            var priceMatch = matches[^1];
            var prefix = line[..priceMatch.Index].TrimEnd(' ', '.', '-', '−', '·', ':');
            if (prefix.Length < 2 || !prefix.Any(char.IsLetter)) continue;

            decimal? quantity = null;
            decimal? unitPrice = null;
            string? quantityUnit = null;
            var quantityMatch = QuantityPriceSuffixRegex().Match(prefix);
            var name = prefix;
            if (quantityMatch.Success)
            {
                var candidateName = quantityMatch.Groups["name"].Value.TrimEnd(' ', '.', '-', '·', ':');
                if (candidateName.Length >= 2 && candidateName.Any(char.IsLetter))
                {
                    name = candidateName;
                    quantity = ParseFlexibleDecimal(quantityMatch.Groups["quantity"].Value);
                    unitPrice = ParseFlexibleDecimal(quantityMatch.Groups["unitPrice"].Value);
                    quantityUnit = NormalizeQuantityUnit(quantityMatch.Groups["unit"].Value);
                }
            }

            var lineTotal = Math.Abs(ParseAmount(priceMatch));
            items.Add(new ReceiptLineItem(
                Name: name.Trim(),
                Quantity: quantity,
                UnitPrice: unitPrice,
                TotalPrice: lineTotal,
                CategoryHint: null,
                Confidence: quantityMatch.Success ? 0.5m : 0.4m,
                QuantityUnit: quantityUnit,
                LineType: "product"));
        }

        var discountTotal = discounts.Sum(x => x.Amount);
        if (!total.HasValue)
        {
            var merchandise = subtotal ?? items.Sum(item => Math.Max(0m, item.TotalPrice ?? 0m));
            var derived = merchandise - discountTotal + (deposits ?? 0m) + (tip ?? 0m) + (shipping ?? 0m) + (fees ?? 0m) + (rounding ?? 0m);
            if (derived > 0m) total = derived;
            else
            {
                var largest = MoneyAmountRegex().Matches(text).Select(match => Math.Abs(ParseAmount(match))).DefaultIfEmpty().Max();
                if (largest > 0m) total = largest;
            }
        }

        var signals = new[]
        {
            merchant is not null,
            purchaseDate is not null,
            total is not null,
            items.Count > 0,
            subtotal.HasValue || discounts.Count > 0 || deposits.HasValue || rounding.HasValue
        };
        var confidence = Math.Clamp(0.15m + 0.17m * signals.Count(signal => signal), 0m, 1m);

        return new ReceiptExtractionResult(
            Provider: "tesseract",
            Merchant: merchant,
            PurchaseDate: purchaseDate,
            Currency: currency,
            Total: total,
            Discounts: discountTotal > 0m ? discountTotal : null,
            Deposits: deposits,
            Taxes: taxes,
            Items: items,
            Confidence: confidence,
            Subtotal: subtotal,
            Rounding: rounding,
            Tip: tip,
            Shipping: shipping,
            Fees: fees,
            StructuredDiscounts: discounts);
    }

    private static bool IsPaymentOnlyLine(string lower)
    {
        var normalized = lower.Trim();
        if (normalized.StartsWith("bar ") || normalized == "bar") return true;
        if (normalized.StartsWith("karte ") || normalized == "karte") return true;
        return Regex.IsMatch(normalized, @"^(zahlart|zahlung|payment)\b", RegexOptions.CultureInvariant);
    }

    private static bool LooksLikeDiscountLine(string lower) =>
        lower.Contains("rabatt") || lower.Contains("coupon") || lower.Contains("aktionsgutschein") ||
        lower.Contains("gutscheinrabatt") || lower.Contains("ersparnis") || lower.Contains("promotion");

    private static bool LooksLikePaymentBalance(string lower) =>
        lower.Contains("guthaben") || lower.Contains("gift card balance") || lower.Contains("gift-card balance") ||
        lower.Contains("gift card applied") || lower.Contains("gift-card applied");

    private static string DiscountType(string lower)
    {
        if (lower.Contains("coupon") || lower.Contains("gutschein")) return "coupon";
        if (lower.Contains("promotion")) return "promotion";
        return "other";
    }

    private static DateOnly? FindDate(string text)
    {
        var iso = IsoDateRegex().Match(text);
        if (iso.Success && DateOnly.TryParse(iso.Value, CultureInfo.InvariantCulture, out var d1)) return d1;

        var dotted = DottedDateRegex().Match(text);
        if (dotted.Success)
        {
            var day = int.Parse(dotted.Groups[1].Value);
            var month = int.Parse(dotted.Groups[2].Value);
            var year = int.Parse(dotted.Groups[3].Value);
            if (year < 100) year += 2000;
            if (month is >= 1 and <= 12 && day is >= 1 and <= 31 && year is >= 2000 and <= 2100)
            {
                try { return new DateOnly(year, month, day); } catch { return null; }
            }
        }
        return null;
    }

    private static decimal? LastAmount(string line)
    {
        var matches = MoneyAmountRegex().Matches(line);
        return matches.Count == 0 ? null : ParseAmount(matches[^1]);
    }

    private static decimal ParseAmount(Match match)
    {
        var value = match.Groups["whole"].Value + "." + match.Groups["fraction"].Value;
        if (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount)) return 0m;
        if (match.Groups["sign"].Value is "-" or "−" || match.Groups["trail"].Value is "-" or "−") amount = -amount;
        return amount;
    }

    private static decimal? ParseFlexibleDecimal(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return decimal.TryParse(value.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) && parsed > 0m
            ? parsed
            : null;
    }

    // True when the (already lower-cased) line contains the keyword delimited by non-letters on both
    // sides, so keywords never match inside a longer product word.
    private static bool ContainsKeyword(string lower, string keyword) =>
        Regex.IsMatch(lower, $@"(?<!\p{{L}}){Regex.Escape(keyword)}(?!\p{{L}})", RegexOptions.CultureInvariant);

    private static string NormalizeQuantityUnit(string value) => value.Trim().ToLowerInvariant() switch
    {
        "kg" => "kg",
        "g" => "g",
        "l" => "l",
        "ml" => "ml",
        "st" or "stk" or "stück" => "piece",
        _ => "piece"
    };

    [GeneratedRegex(@"(?<!\d)(?<sign>[-−]?)(?<whole>\d{1,6})[.,](?<fraction>\d{2})(?<trail>[-−]?)(?!\d)")]
    private static partial Regex MoneyAmountRegex();

    [GeneratedRegex(@"^(?<name>.+?)\s+(?<quantity>\d+(?:[.,]\d{1,3})?)\s*(?<unit>kg|g|l|ml|st|stk|stück)?\s*[xX*]\s*(?<unitPrice>\d+(?:[.,]\d{2}))\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex QuantityPriceSuffixRegex();

    [GeneratedRegex(@"\b\d{4}-\d{2}-\d{2}\b")]
    private static partial Regex IsoDateRegex();

    [GeneratedRegex(@"\b(\d{1,2})\.(\d{1,2})\.(\d{2,4})\b")]
    private static partial Regex DottedDateRegex();
}
