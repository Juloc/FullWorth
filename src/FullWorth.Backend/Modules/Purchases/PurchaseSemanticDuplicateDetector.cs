using System.Globalization;
using System.Text;
using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Purchases;

/// <summary>
/// Detects likely duplicate purchases after extraction when the physical receipt bytes differ
/// (for example a receipt was photographed twice). This is warning-only by design: no purchase,
/// item or document is ever deleted/merged automatically from a similarity result.
/// </summary>
public sealed class PurchaseSemanticDuplicateDetector(FullWorthDbContext db)
{
    public async Task<IReadOnlyList<string>> DetectWarningsAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        Guid currentPurchaseId,
        PurchaseExtractionRequest request,
        CancellationToken ct)
    {
        var merchant = Normalize(request.Merchant);
        if (merchant.Length < 2 || !request.PurchaseDate.HasValue || request.TotalAmount <= 0m)
            return [];

        var date = request.PurchaseDate.Value;
        var currency = NormalizeCurrency(request.Currency);
        var tolerance = PurchaseArticleCalculator.Tolerance(currency);
        var currentNames = request.Items
            .Where(x => string.Equals(x.LineType ?? "product", "product", StringComparison.OrdinalIgnoreCase))
            .Select(x => Normalize(x.Name))
            .Where(x => x.Length >= 2)
            .ToList();

        // Use the same purchase/account visibility boundary as the ordinary read API. Even a generic
        // duplicate warning must never reveal the existence of a purchase linked only to an account
        // the caller cannot access.
        var candidates = await db.Purchases.AsNoTracking()
            .Where(x => x.Id != currentPurchaseId && x.FullWorthSpaceId == fullWorthSpaceId)
            .Where(x => db.FullWorthSpaceMembers.Any(m => m.FullWorthSpaceId == fullWorthSpaceId && m.UserId == userId))
            .Where(x => x.Visibility != "private" || x.CreatedByUserId == userId)
            .Where(x => !x.PaymentLinks.Any() || x.PaymentLinks.Any(link =>
                db.Transactions.Any(tx => tx.Id == link.TransactionId &&
                    db.Accounts.Any(account => account.Id == tx.AccountId && account.FullWorthSpaceId == fullWorthSpaceId &&
                        account.Owners.Any(owner => owner.UserId == userId)))))
            .Where(x => x.TransactionId == null || db.Transactions.Any(tx => tx.Id == x.TransactionId &&
                db.Accounts.Any(account => account.Id == tx.AccountId && account.FullWorthSpaceId == fullWorthSpaceId &&
                    account.Owners.Any(owner => owner.UserId == userId))))
            .Where(x => x.PurchaseDate == date && x.Currency == currency)
            .Where(x => Math.Abs(x.TotalAmount - request.TotalAmount) <= tolerance)
            .Include(x => x.Items)
            .OrderByDescending(x => x.CreatedAt)
            .Take(25)
            .ToListAsync(ct);

        foreach (var candidate in candidates)
        {
            if (!MerchantEquivalent(merchant, Normalize(candidate.Merchant), Normalize(candidate.MerchantRaw)))
                continue;

            if (!string.IsNullOrWhiteSpace(request.ReceiptNumber) &&
                !string.IsNullOrWhiteSpace(candidate.ReceiptNumber) &&
                string.Equals(NormalizeCode(request.ReceiptNumber), NormalizeCode(candidate.ReceiptNumber), StringComparison.Ordinal))
            {
                return ["Möglicher doppelter Beleg: Händler, Datum, Betrag und Bonnummer stimmen mit einem bereits gespeicherten Kauf überein. Bitte vor dem Bestätigen prüfen."];
            }

            var candidateNames = candidate.Items
                .Where(x => string.Equals(x.LineType, "product", StringComparison.OrdinalIgnoreCase))
                .Select(x => Normalize(x.Name))
                .Where(x => x.Length >= 2)
                .ToList();
            if (StrongItemOverlap(currentNames, candidateNames))
                return ["Möglicher doppelter Beleg: Händler, Datum, Betrag und mehrere Artikel stimmen mit einem bereits gespeicherten Kauf überein. Der Kauf wurde nicht automatisch zusammengeführt."];
        }

        return [];
    }

    internal static bool StrongItemOverlap(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        if (left.Count < 2 || right.Count < 2) return false;
        var used = new bool[right.Count];
        var matches = 0;
        foreach (var item in left)
        {
            for (var index = 0; index < right.Count; index++)
            {
                if (used[index] || !ItemEquivalent(item, right[index])) continue;
                used[index] = true;
                matches++;
                break;
            }
        }
        var smaller = Math.Min(left.Count, right.Count);
        return matches >= 2 && matches >= Math.Ceiling(smaller * 0.8m);
    }

    private static bool MerchantEquivalent(string expected, string actual, string raw) =>
        expected == actual || expected == raw ||
        (expected.Length >= 5 && (actual.Contains(expected, StringComparison.Ordinal) || expected.Contains(actual, StringComparison.Ordinal))) ||
        (expected.Length >= 5 && (raw.Contains(expected, StringComparison.Ordinal) || expected.Contains(raw, StringComparison.Ordinal)));

    private static bool ItemEquivalent(string left, string right) =>
        left == right ||
        (left.Length >= 5 && right.Length >= 5 &&
         (left.Contains(right, StringComparison.Ordinal) || right.Contains(left, StringComparison.Ordinal)));

    private static string NormalizeCurrency(string? value)
    {
        var currency = string.IsNullOrWhiteSpace(value) ? "EUR" : value.Trim().ToUpperInvariant();
        return currency.Length == 3 && currency.All(x => x is >= 'A' and <= 'Z') ? currency : "EUR";
    }

    private static string NormalizeCode(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var decomposed = value.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        var lastWasSpace = false;
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(ch);
                lastWasSpace = false;
            }
            else if (!lastWasSpace && builder.Length > 0)
            {
                builder.Append(' ');
                lastWasSpace = true;
            }
        }
        return builder.ToString().Trim().Normalize(NormalizationForm.FormC);
    }
}
