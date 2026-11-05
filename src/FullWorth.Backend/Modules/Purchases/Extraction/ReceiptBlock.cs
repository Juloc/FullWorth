using FullWorth.Backend.Validation;
using System.Text.RegularExpressions;

namespace FullWorth.Backend.Modules.Purchases.Extraction;

/// <summary>
/// The block of a receipt that carries the money: every line above the total line. Items, the
/// discounts under them, deposits and rounding come from here; everything below the total (payment,
/// tax table, signature) is not read at all, so the tax table can never be mistaken for items.
/// </summary>
internal sealed partial class ReceiptBlock
{
    public List<ReceiptLineItem> Items { get; } = [];
    public List<ReceiptDiscount> Discounts { get; } = [];
    public decimal Deposits { get; private set; }
    public decimal Rounding { get; private set; }
    public decimal? Subtotal { get; private set; }
    public decimal Shipping { get; private set; }
    public decimal Fees { get; private set; }
    public decimal Tip { get; private set; }
    public decimal? Total { get; private set; }

    /// <summary>What the lines add up to: items - discounts + deposits + shipping + fees + tip + rounding. Equals <see cref="Total"/> on a well-read receipt.</summary>
    public decimal Net => Items.Sum(item => item.TotalPrice ?? 0m) - Discounts.Sum(discount => discount.Amount) + Deposits + Shipping + Fees + Tip + Rounding;

    public static ReceiptBlock Read(IReadOnlyList<string> lines)
    {
        var block = new ReceiptBlock();
        var totalIndex = FindTotalLine(lines, out var total);
        block.Total = total;
        var end = totalIndex >= 0 ? totalIndex : lines.Count;

        string? pendingName = null;
        for (var i = 0; i < end; i++)
        {
            var line = lines[i];
            var lower = line.ToLowerInvariant();
            if (SubtotalRegex().IsMatch(lower) && TrailingAmount(line, out _) is { } subtotal) { block.Subtotal ??= Math.Abs(subtotal); continue; }
            if (line[0] == '(' || InfoRegex().IsMatch(lower)) { pendingName = null; continue; }

            var amount = TrailingAmount(line, out var head);
            if (amount is null)
            {
                if (block.TryAddQuantityNote(line)) continue;
                pendingName = line.Any(char.IsLetter) ? line : pendingName;
                continue;
            }

            if (block.TryAddQuantityNote(line)) continue;
            block.Add(head, amount.Value, ref pendingName);
        }
        block.RepairOneDigit();
        return block;
    }

    /// <summary>True when one price had to be corrected to make the receipt add up - such a result is never confirmed by itself.</summary>
    public bool Repaired { get; private set; }

    /// <summary>
    /// A scan reads a 5 as a 9 now and then. When the lines do not add up to the total and exactly one
    /// price, changed in exactly one digit to a look-alike, makes them add up, that is the misread. If
    /// no such price exists - or more than one - nothing is touched and the receipt stays unsure.
    /// </summary>
    private void RepairOneDigit()
    {
        if (Total is not { } total || Items.Count == 0 || Math.Abs(Net - total) <= 0.005m) return;

        var fixes = new List<(int Index, bool Discount, decimal Value)>();
        for (var i = 0; i < Items.Count; i++)
            foreach (var alternative in LookAlikes(Items[i].TotalPrice ?? 0m))
                if (Math.Abs(Net - (Items[i].TotalPrice ?? 0m) + alternative - total) <= 0.005m) fixes.Add((i, false, alternative));
        for (var i = 0; i < Discounts.Count; i++)
            foreach (var alternative in LookAlikes(Discounts[i].Amount))
                if (Math.Abs(Net + Discounts[i].Amount - alternative - total) <= 0.005m) fixes.Add((i, true, alternative));
        if (fixes.Count != 1) return;

        var (index, discount, value) = fixes[0];
        if (discount) Discounts[index] = Discounts[index] with { Amount = value };
        else Items[index] = Items[index] with { TotalPrice = value, Confidence = 0.5m };
        Repaired = true;
    }

    private static readonly string[] LookAlikePairs = ["08", "06", "09", "17", "27", "35", "38", "49", "56", "59", "68", "89"];

    // One digit of the price in cents at a time, swapped for the digit it is most often misread as. Plain
    // integer arithmetic on the cents - no text is parsed here.
    private static IEnumerable<decimal> LookAlikes(decimal price)
    {
        var sign = price < 0m ? -1m : 1m;
        var cents = (long)Math.Round(Math.Abs(price) * 100m);
        var digitCount = cents.ToString(System.Globalization.CultureInfo.InvariantCulture).Length;
        long place = 1;
        for (var position = 0; position < digitCount; position++, place *= 10)
        {
            var digit = (int)(cents / place % 10);
            foreach (var pair in LookAlikePairs)
            {
                var index = pair.IndexOf((char)('0' + digit));
                if (index < 0) continue;
                var swapped = pair[1 - index] - '0';
                yield return sign * (cents + (swapped - digit) * place) / 100m;
            }
        }
    }

    private void Add(string head, decimal amount, ref string? pendingName)
    {
        var lower = head.ToLowerInvariant();
        var quantity = QuantityRegex().Match(head);
        var name = Clean(quantity.Success ? head[..quantity.Index] : head);
        if (name.Length == 0 && pendingName is not null) { name = Clean(pendingName); }
        pendingName = null;

        if (RoundingRegex().IsMatch(lower)) { Rounding += amount; return; }
        if (ShippingRegex().IsMatch(lower)) { Shipping += amount; return; }
        if (FeeRegex().IsMatch(lower)) { Fees += amount; return; }
        if (TipRegex().IsMatch(lower)) { Tip += amount; return; }
        if (DepositRegex().IsMatch(lower))
        {
            if (amount >= 0m) Deposits += amount;
            else Discounts.Add(new ReceiptDiscount("other", "Pfandrückgabe", -amount, RawText: head, Confidence: ConfidenceFor(), ItemIndex: null));
            return;
        }
        if (amount < 0m || DiscountRegex().IsMatch(lower))
        {
            Discounts.Add(new ReceiptDiscount(DiscountType(lower), Clean(head), Math.Abs(amount), RawText: head, Confidence: ConfidenceFor(), ItemIndex: Items.Count > 0 ? Items.Count - 1 : null));
            return;
        }
        if (name.Length < 2 || !name.Any(char.IsLetter)) return;

        decimal? qty = null, unit = null;
        if (quantity.Success)
        {
            qty = Number(quantity.Groups["q"].Value, ImportNumber.ThreeDigitTail.Decimal);
            unit = Number(quantity.Groups["u"].Value, ImportNumber.ThreeDigitTail.Decimal);
        }
        Items.Add(new ReceiptLineItem(name, qty, unit, amount, CategoryHint: null, Confidence: ConfidenceFor(),
            QuantityUnit: quantity.Groups["unit"].Success ? "kg" : null, LineType: "product"));
    }

    /// <summary>"2 Stk x 1,05" and "0,716 kg x 1,49 EUR/kg" under an item describe it; they carry no price of their own.</summary>
    private bool TryAddQuantityNote(string line)
    {
        var note = QuantityNoteRegex().Match(line);
        if (!note.Success || Items.Count == 0) return false;
        var last = Items[^1];
        var quantity = Number(note.Groups["q"].Value, ImportNumber.ThreeDigitTail.Decimal);
        var unit = Number(note.Groups["u"].Value, ImportNumber.ThreeDigitTail.Decimal);
        Items[^1] = last with { Quantity = quantity, UnitPrice = unit, QuantityUnit = note.Groups["unit"].Value.Contains("kg", StringComparison.OrdinalIgnoreCase) ? "kg" : last.QuantityUnit };
        return true;
    }

    private static decimal ConfidenceFor() => 0.9m;

    private static string DiscountType(string lower) =>
        lower.Contains("coupon") || lower.Contains("gutschein") ? "coupon"
        : lower.Contains("card") || lower.Contains("payback") ? "loyalty"
        : lower.Contains("rabatt") || lower.Contains("preis") ? "price_reduction"
        : "other";

    /// <summary>
    /// The total is the SUMME line. When its word is unreadable the paid amount stands in: the last line
    /// above the card payment that carries it, or the payment line itself. Returns the line's index so
    /// the block ends right above it.
    /// </summary>
    private static int FindTotalLine(IReadOnlyList<string> lines, out decimal? total)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            if (!SumRegex().IsMatch(lines[i]) && !LooksLikeSum(lines[i])) continue;
            if (TrailingAmount(lines[i], out _) is not { } amount) continue;
            total = Math.Abs(amount);
            return i;
        }

        var byPayment = FindTotalByPayment(lines, out total);
        if (byPayment >= 0) return byPayment;

        for (var i = 0; i < lines.Count; i++)
        {
            if (!PaymentRegex().IsMatch(lines[i]) || TrailingAmount(lines[i], out _) is not { } amount) continue;
            total = Math.Abs(amount);
            return i;
        }
        return -1;
    }

    /// <summary>
    /// The SUMME word may be unreadable ("JUNE 90,29") while the card payment below it is not: the total
    /// is then the last line above the payment that carries the paid amount.
    /// </summary>
    private static int FindTotalByPayment(IReadOnlyList<string> lines, out decimal? total)
    {
        total = null;
        for (var paid = 0; paid < lines.Count; paid++)
        {
            if (!PaymentRegex().IsMatch(lines[paid]) || TrailingAmount(lines[paid], out _) is not { } amount) continue;
            for (var i = paid - 1; i >= 0; i--)
            {
                if (TrailingAmount(lines[i], out _) != amount) continue;
                total = Math.Abs(amount);
                return i;
            }
        }
        return -1;
    }

    /// <summary>The scan misreads the word itself now and then ("SUMMI-", "SME"): two letters off is still SUMME.</summary>
    private static bool LooksLikeSum(string line)
    {
        var word = FirstWordRegex().Match(line).Value.ToUpperInvariant();
        return word.Length is >= 3 and <= 7 && word[0] == 'S' && EditDistance(word, "SUMME") <= 2;
    }

    private static int EditDistance(string a, string b)
    {
        var row = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var diagonal = row[0];
            row[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var above = row[j];
                row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), diagonal + (a[i - 1] == b[j - 1] ? 0 : 1));
                diagonal = above;
            }
        }
        return row[b.Length];
    }

    /// <summary>The last amount on a line, when only an optional currency and a tax class follow it. <paramref name="head"/> is what stands before it.</summary>
    private static decimal? TrailingAmount(string line, out string head)
    {
        head = line;
        var matches = AmountRegex().Matches(line);
        if (matches.Count == 0) return null;
        var last = matches[^1];
        if (!TailRegex().IsMatch(line[(last.Index + last.Length)..])) return null;
        head = line[..last.Index].TrimEnd();
        var amount = Number(last.Groups["amount"].Value);
        if (amount is null) return null;
        return last.Groups["sign"].Value is "-" or "−" || last.Groups["trail"].Value is "-" or "−" ? -amount : amount;
    }

    /// <summary>The shared reader instead of a culture parse: "1.234,56" is one thousand two hundred, "1,402" is a weight.</summary>
    private static decimal? Number(string value, ImportNumber.ThreeDigitTail tail = ImportNumber.ThreeDigitTail.Grouping)
    {
        try { return ImportNumber.TryParse(value, tail); }
        catch (FormatException) { return null; }
    }

    private static string Clean(string name) => WhitespaceRegex().Replace(name.Trim(' ', '.', '-', '−', '·', ':', '*', '|', '_', '=', '~'), " ");

    [GeneratedRegex(@"(?<!\d)(?<sign>[-−]?)(?<amount>(?:\d{1,3}(?:[.,]\d{3})+|\d{1,6})[.,]\d{2})(?<trail>[-−]?)(?<glued>[8B](?=\s|$))?(?![\d.,])")]
    private static partial Regex AmountRegex();

    // After the amount only a currency and a tax class (A, B, 8B, "A *") may follow. A tax class glued to the
    // cents ("0,70B" read as "0,708") is the optional group 'glued'.
    [GeneratedRegex(@"^\s*(?:EUR|€)?(?:\s*[A-Za-z0-9*]){0,3}\s*$")]
    private static partial Regex TailRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"^\W*\p{L}+")]
    private static partial Regex FirstWordRegex();

    // "4 * 1,69", "3%*1,79", "2 + 1,99" at the end of the part before the price.
    [GeneratedRegex(@"(?:^|\s)(?<q>\d{1,3}(?:[.,]\d{3})?)\s*(?<unit>kg)?\s*[*x×+%]+\s*(?<u>\d{1,4}[.,]\d{2})\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex QuantityRegex();

    [GeneratedRegex(@"^\s*(?<q>\d+(?:[.,]\d{1,3})?)\s*(?<unit>kg|stk\.?|st\.?|stück)?\s*[xX*×]\s*(?<u>\d+[.,]\d{2,3})\s*(?:eur|€)?(?:/\s*(?:kg|l|st))?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex QuantityNoteRegex();

    [GeneratedRegex(@"(?<!\p{L})(sie sparen|ersparnis|mwst|mehrwertsteuer|ust|steuer|netto|brutto|uid|tel|telefon|kundenbeleg|datum|uhrzeit|beleg|bon-nr|tse|terminal|trace|vu-nr|visa|mastercard|maestro|girocard|contactless|kasse|kassierer|bediener|filiale|rückgeld|rueckgeld|gegeben|artikelanzahl|positionen|payback|punkte|guthaben)(?!\p{L})|^artikel\s+\d")]
    private static partial Regex InfoRegex();

    [GeneratedRegex(@"(?<!\p{L})(rabatt|coupon|gutschein|preisvorteil|aktion|vorteil)")]
    private static partial Regex DiscountRegex();

    [GeneratedRegex(@"(?<!\p{L})(pfand|leerg|einweg|mehrweg|ew)(?!\p{L}{2})")]
    private static partial Regex DepositRegex();

    [GeneratedRegex(@"(?<!\p{L})(versand|shipping|postage)")]
    private static partial Regex ShippingRegex();

    [GeneratedRegex(@"(?<!\p{L})(gebühr|gebuehr|fee)(?!\p{L})")]
    private static partial Regex FeeRegex();

    [GeneratedRegex(@"(?<!\p{L})(trinkgeld|tip)(?!\p{L})")]
    private static partial Regex TipRegex();

    [GeneratedRegex(@"(?<!\p{L})(zwischensumme|subtotal)")]
    private static partial Regex SubtotalRegex();

    [GeneratedRegex(@"(?<!\p{L})(rundung|rounding)")]
    private static partial Regex RoundingRegex();

    [GeneratedRegex(@"^\s*(summe|zu zahlen|zahlbetrag|gesamtbetrag|gesamt|total|endbetrag)(?!\p{L})", RegexOptions.IgnoreCase)]
    private static partial Regex SumRegex();

    [GeneratedRegex(@"(geg\.|kartenzahlung|(?<!\p{L})bar(?!\p{L})|ec-?karte|girocard|visa|m[aä]stercard|maestro|(?<!\p{L})betrag)", RegexOptions.IgnoreCase)]
    private static partial Regex PaymentRegex();
}
