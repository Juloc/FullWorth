using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace FullWorth.Backend.Modules.Purchases.Amazon;

public static partial class AmazonPageParser
{
    [GeneratedRegex(@"\b\d{3}-\d{7}-\d{7}\b", RegexOptions.CultureInvariant)]
    private static partial Regex OrderIdRegex();

    [GeneratedRegex(@"/(?:dp|gp/product)/(?<asin>[A-Z0-9]{10})(?:[/?]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AsinRegex();

    [GeneratedRegex(@"(?:Menge|Quantity|Qty)\s*:?\s*(?<q>\d+(?:[.,]\d+)?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex QuantityRegex();

    [GeneratedRegex(@"(?:(?<iso>EUR|USD|GBP)\s*)?(?<amount>\d{1,3}(?:\.\d{3})*(?:,\d{2})|\d+(?:[.,]\d{2}))\s*(?<symbol>€|EUR|USD|GBP|£|\$)?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MoneyRegex();

    [GeneratedRegex(@"(?<sign>[-−]?)\s*(?:(?<iso>EUR|USD|GBP)\s*)?(?<amount>\d{1,3}(?:\.\d{3})*(?:,\d{2})|\d+(?:[.,]\d{2}))\s*(?<symbol>€|EUR|USD|GBP|£|\$)?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SignedMoneyRegex();

    public static string? FindOrderId(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var match = OrderIdRegex().Match(text);
        return match.Success ? match.Value : null;
    }

    public static string? FindAsin(string? href)
    {
        if (string.IsNullOrWhiteSpace(href)) return null;
        var match = AsinRegex().Match(href);
        return match.Success ? match.Groups["asin"].Value.ToUpperInvariant() : null;
    }

    public static decimal FindQuantity(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 1m;
        var match = QuantityRegex().Match(text);
        if (!match.Success) return 1m;
        return decimal.TryParse(match.Groups["q"].Value.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : 1m;
    }

    public static DateOnly? FindPurchaseDate(string text)
    {
        var patterns = new[]
        {
            @"(?:Bestellt am|Bestelldatum)\s*:?[\s\r\n]*(?<date>\d{1,2}[.]\s*[A-Za-zÄÖÜäöüß]+\s+\d{4})",
            @"(?:Ordered on|Order placed)\s*:?[\s\r\n]*(?<date>[A-Za-z]+\s+\d{1,2},\s*\d{4})",
            @"(?:Bestellt am|Bestelldatum)\s*:?[\s\r\n]*(?<date>\d{1,2}[.]\d{1,2}[.]\d{4})"
        };

        foreach (var pattern in patterns)
        {
            var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success) continue;
            var raw = Regex.Replace(match.Groups["date"].Value, @"\s+", " ").Trim();
            foreach (var culture in new[] { CultureInfo.GetCultureInfo("de-DE"), CultureInfo.GetCultureInfo("en-US") })
                if (DateOnly.TryParse(raw, culture, DateTimeStyles.AllowWhiteSpaces, out var date)) return date;
        }
        return null;
    }

    public static (decimal Amount, string Currency)? FindOrderTotal(string text)
    {
        foreach (var label in new[] { "Gesamtsumme", "Bestellsumme", "Gesamt", "Order Total", "Grand Total" })
        {
            var index = text.IndexOf(label, StringComparison.OrdinalIgnoreCase);
            if (index < 0) continue;
            var tail = text.Substring(index, Math.Min(220, text.Length - index));
            var money = FindFirstMoney(tail);
            if (money.HasValue) return money;
        }
        return null;
    }

    public static (decimal Amount, string Currency)? FindSubtotal(string text, string expectedCurrency) =>
        FindLabeledAmount(text,
            ["Zwischensumme", "Artikel-Zwischensumme", "Item(s) Subtotal", "Items Subtotal"],
            expectedCurrency,
            requirePositive: true);

    public static (decimal Amount, string Currency)? FindShippingAmount(string text, string expectedCurrency) =>
        FindLabeledAmount(text,
            ["Versandkosten", "Versand & Bearbeitung", "Versand und Bearbeitung", "Shipping & Handling", "Shipping and Handling", "Postage & Packing"],
            expectedCurrency,
            requirePositive: false);

    public static IReadOnlyList<AmazonDiscountSnapshot> FindDiscounts(string text, string expectedCurrency, decimal orderTotal)
    {
        if (string.IsNullOrWhiteSpace(text) || orderTotal <= 0m) return [];
        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var result = new Dictionary<string, AmazonDiscountSnapshot>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (!LooksLikeDiscount(line) || LooksLikePaymentBalance(line)) continue;

            // Prefer the labelled row itself. Amazon sometimes renders the amount in the immediately
            // following row, but a previous product/subtotal amount must never be mistaken for savings.
            var money = FindFirstSignedMoney(line);
            var raw = line;
            // Only borrow the amount from the following row when that row is a bare money line. A labelled
            // total (e.g. "Gesamtsumme: 49,99 €") after a heading-only discount label must never be
            // mistaken for the discount amount.
            if (!money.HasValue && i + 1 < lines.Length && !LooksLikeStructuralTotal(lines[i + 1]))
            {
                money = FindFirstSignedMoney(lines[i + 1]);
                raw = $"{line} {lines[i + 1]}";
            }
            if (!money.HasValue || !string.Equals(money.Value.Currency, expectedCurrency, StringComparison.OrdinalIgnoreCase)) continue;
            var amount = Math.Abs(money.Value.Amount);
            if (amount <= 0m || amount > orderTotal + .01m) continue;
            var type = DiscountType(line);
            var label = line.Length <= 250 ? line : line[..250];
            if (raw.Length > 1000) raw = raw[..1000];
            var key = $"{type}|{amount.ToString(CultureInfo.InvariantCulture)}|{NormalizeLabel(label)}";
            result.TryAdd(key, new AmazonDiscountSnapshot(type, label, amount, null, raw));
        }
        return result.Values.ToList();
    }

    public static (decimal Amount, string Currency)? FindFirstMoney(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        foreach (Match match in MoneyRegex().Matches(text))
        {
            if (!match.Success) continue;
            if (!match.Groups["iso"].Success && !match.Groups["symbol"].Success) continue;
            if (!TryParseAmount(match.Groups["amount"].Value, out var amount)) continue;
            return (amount, Currency(match.Groups["iso"].Value, match.Groups["symbol"].Value));
        }
        return null;
    }

    private static (decimal Amount, string Currency)? FindFirstSignedMoney(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        foreach (Match match in SignedMoneyRegex().Matches(text))
        {
            if (!match.Success) continue;
            if (!match.Groups["iso"].Success && !match.Groups["symbol"].Success) continue;
            if (!TryParseAmount(match.Groups["amount"].Value, out var amount)) continue;
            if (match.Groups["sign"].Value is "-" or "−") amount = -amount;
            return (amount, Currency(match.Groups["iso"].Value, match.Groups["symbol"].Value));
        }
        return null;
    }

    private static (decimal Amount, string Currency)? FindLabeledAmount(string text, IReadOnlyList<string> labels, string expectedCurrency, bool requirePositive)
    {
        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = 0; i < lines.Length; i++)
        {
            if (!labels.Any(label => lines[i].Contains(label, StringComparison.OrdinalIgnoreCase))) continue;
            var window = string.Join(" ", lines.Skip(i).Take(Math.Min(2, lines.Length - i)));
            var money = FindFirstSignedMoney(window);
            if (!money.HasValue || !string.Equals(money.Value.Currency, expectedCurrency, StringComparison.OrdinalIgnoreCase)) continue;
            var amount = requirePositive ? money.Value.Amount : Math.Abs(money.Value.Amount);
            if (amount < 0m) continue;
            return (amount, money.Value.Currency);
        }
        return null;
    }

    public static decimal FindNonBankPaymentAmount(string? text, string expectedCurrency, decimal orderTotal)
    {
        if (string.IsNullOrWhiteSpace(text) || orderTotal <= 0m) return 0m;
        var labels = new[]
        {
            "Geschenkgutschein-Guthaben",
            "Geschenkgutscheinguthaben",
            "Amazon-Guthaben",
            "Gift Card balance",
            "Gift-card balance",
            "Gift card applied"
        };
        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        decimal best = 0m;
        for (var i = 0; i < lines.Length; i++)
        {
            if (!labels.Any(label => lines[i].Contains(label, StringComparison.OrdinalIgnoreCase))) continue;
            var window = i + 1 < lines.Length ? lines[i] + " " + lines[i + 1] : lines[i];
            var money = FindFirstMoney(window);
            if (!money.HasValue || !string.Equals(money.Value.Currency, expectedCurrency, StringComparison.OrdinalIgnoreCase)) continue;
            if (money.Value.Amount <= 0m || money.Value.Amount > orderTotal + .01m) continue;
            best = Math.Max(best, money.Value.Amount);
        }
        return Math.Min(orderTotal, best);
    }

    public static string? FindExternalStatus(string text)
    {
        var statuses = new (string Needle, string Status)[]
        {
            ("Storniert", "cancelled"), ("Cancelled", "cancelled"),
            ("Rücksendung", "return"), ("Returned", "return"),
            ("Zugestellt", "delivered"), ("Delivered", "delivered"),
            ("Versandt", "shipped"), ("Shipped", "shipped"),
            ("Bestellung aufgegeben", "ordered"), ("Order placed", "ordered")
        };
        return statuses.FirstOrDefault(x => text.Contains(x.Needle, StringComparison.OrdinalIgnoreCase)).Status;
    }

    public static IReadOnlyList<AmazonRefundSnapshot> FindRefunds(string orderId, string text, string defaultCurrency)
    {
        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var refunds = new Dictionary<string, AmazonRefundSnapshot>(StringComparer.Ordinal);
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (!line.Contains("Erstattung", StringComparison.OrdinalIgnoreCase) &&
                !line.Contains("Rückerstattung", StringComparison.OrdinalIgnoreCase) &&
                !line.Contains("Refund", StringComparison.OrdinalIgnoreCase)) continue;

            var window = string.Join(" ", lines.Skip(i).Take(3));
            var money = FindFirstMoney(window);
            if (!money.HasValue) continue;
            var date = FindAnyDate(window);
            var externalId = StableId(orderId, "refund", date?.ToString("yyyy-MM-dd") ?? string.Empty, money.Value.Amount.ToString(CultureInfo.InvariantCulture), line);
            refunds[externalId] = new(externalId, date, money.Value.Amount, money.Value.Currency, "refund", line.Length <= 500 ? line : line[..500]);
        }

        if (refunds.Count == 0 && (text.Contains("Rücksendung", StringComparison.OrdinalIgnoreCase) || text.Contains("Return", StringComparison.OrdinalIgnoreCase)))
        {
            var externalId = StableId(orderId, "return");
            refunds[externalId] = new(externalId, null, 0m, defaultCurrency, "return", "Return recorded by Amazon");
        }
        return refunds.Values.ToList();
    }

    private static bool LooksLikeDiscount(string line) =>
        line.Contains("Rabatt", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("Aktionsgutschein", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("Coupon", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("Promotion", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("Ersparnis", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("Savings", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeStructuralTotal(string line) =>
        line.Contains("Gesamtsumme", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("Zwischensumme", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("Bestellsumme", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("Endsumme", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("Summe", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("Subtotal", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("Grand Total", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("Order Total", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("Total", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikePaymentBalance(string line) =>
        line.Contains("Guthaben", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("Gift Card", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("Gift-card", StringComparison.OrdinalIgnoreCase);

    private static string DiscountType(string line)
    {
        if (line.Contains("Coupon", StringComparison.OrdinalIgnoreCase) || line.Contains("Aktionsgutschein", StringComparison.OrdinalIgnoreCase)) return "coupon";
        if (line.Contains("Promotion", StringComparison.OrdinalIgnoreCase) || line.Contains("Aktion", StringComparison.OrdinalIgnoreCase)) return "promotion";
        return "other";
    }

    private static string NormalizeLabel(string value) => Regex.Replace(value.Trim().ToLowerInvariant(), @"\s+", " ");

    private static bool TryParseAmount(string raw, out decimal amount)
    {
        if (raw.Contains(',')) raw = raw.Replace(".", string.Empty).Replace(',', '.');
        return decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out amount);
    }

    private static string Currency(string iso, string symbol)
    {
        var token = (iso + symbol).ToUpperInvariant();
        return token.Contains("USD", StringComparison.Ordinal) || token.Contains('$') ? "USD"
            : token.Contains("GBP", StringComparison.Ordinal) || token.Contains('£') ? "GBP"
            : "EUR";
    }

    private static DateOnly? FindAnyDate(string text)
    {
        var match = Regex.Match(text, @"(?<date>\d{1,2}[.]\s*[A-Za-zÄÖÜäöüß]+\s+\d{4})", RegexOptions.CultureInvariant);
        if (match.Success && DateOnly.TryParse(Regex.Replace(match.Groups["date"].Value, @"\s+", " "), CultureInfo.GetCultureInfo("de-DE"), DateTimeStyles.AllowWhiteSpaces, out var deText)) return deText;
        match = Regex.Match(text, @"(?<date>\d{1,2}[.]\d{1,2}[.]\d{4})", RegexOptions.CultureInvariant);
        if (match.Success && DateOnly.TryParse(match.Groups["date"].Value, CultureInfo.GetCultureInfo("de-DE"), DateTimeStyles.None, out var de)) return de;
        match = Regex.Match(text, @"(?<date>[A-Za-z]+\s+\d{1,2},\s*\d{4})", RegexOptions.CultureInvariant);
        return match.Success && DateOnly.TryParse(match.Groups["date"].Value, CultureInfo.GetCultureInfo("en-US"), DateTimeStyles.None, out var en) ? en : null;
    }

    private static string StableId(params string[] values)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|', values)));
        return Convert.ToHexString(bytes)[..24];
    }
}