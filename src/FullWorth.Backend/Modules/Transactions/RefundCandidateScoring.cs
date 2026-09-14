using FullWorth.Backend.Modules.Merchants;

namespace FullWorth.Backend.Modules.Transactions;

/// <summary>Eine Ausgabe, die zu dieser Erstattung passen koennte, und warum.</summary>
public sealed record RefundCandidate(
    Guid TransactionId,
    DateOnly Date,
    string? Counterparty,
    decimal Amount,
    string Currency,
    Guid? CategoryId,
    Guid? PurchaseId,
    decimal MatchStrength,
    string Strength,
    IReadOnlyList<string> Reasons);

/// <summary>
/// Wie gut passt eine Ausgabe zu einer Erstattung? Betrag, Haendler, Abstand in Tagen, Text - und
/// der eine harte Treffer: beide gehoeren zur selben Bestellung.
///
/// Das stand bis 2026-09-15 in einer einzigen Zeile im Handler, zusammen mit vier Abfragen. Die
/// Punktevergabe ist die Fachaussage dieser Route; sie gehoert an eine Stelle, an der man sie lesen
/// und einzeln pruefen kann.
/// </summary>
public static class RefundCandidateScoring
{
    /// <summary>Darunter ist es Rauschen und wird nicht vorgeschlagen.</summary>
    public const decimal MinimumScore = 20;

    public static IReadOnlyList<RefundCandidate> Rank(
        FinanceTransaction refund,
        DateOnly refundDate,
        IReadOnlyList<FinanceTransaction> expenses,
        IReadOnlyDictionary<Guid, RefundPurchaseRef> purchaseByTransaction)
    {
        var refundMerchant = MerchantNormalization.Normalize(refund.NormalizedCounterparty ?? refund.Counterparty);
        var refundPurchase = purchaseByTransaction.GetValueOrDefault(refund.Id);
        var hasRefundText = ContainsRefundSignal(refund.Description);

        var candidates = new List<RefundCandidate>();
        foreach (var original in expenses)
        {
            decimal score = 0;
            var reasons = new List<string>();

            if (Math.Abs(Math.Abs(original.Amount) - refund.Amount) <= 0.01m)
            {
                score += 45;
                reasons.Add("Exact amount");
            }
            else if (refund.Amount <= Math.Abs(original.Amount))
            {
                score += 15;
                reasons.Add("Possible partial refund");
            }

            var originalMerchant = MerchantNormalization.Normalize(original.NormalizedCounterparty ?? original.Counterparty);
            if (!string.IsNullOrWhiteSpace(refundMerchant)
                && string.Equals(refundMerchant, originalMerchant, StringComparison.OrdinalIgnoreCase))
            {
                score += 30;
                reasons.Add("Same merchant");
            }

            var originalDate = original.BookingDate ?? original.ValueDate ?? refundDate;
            var days = Math.Abs(refundDate.DayNumber - originalDate.DayNumber);
            var dayPoints = days switch { <= 7 => 15, <= 30 => 10, <= 90 => 5, _ => 0 };
            if (dayPoints > 0)
            {
                score += dayPoints;
                reasons.Add($"{days} days later");
            }

            if (hasRefundText)
            {
                score += 5;
                reasons.Add("Refund text");
            }

            var originalPurchase = purchaseByTransaction.GetValueOrDefault(original.Id);
            if (refundPurchase is not null && originalPurchase is not null
                && string.Equals(refundPurchase.ExternalOrderId, originalPurchase.ExternalOrderId, StringComparison.OrdinalIgnoreCase))
            {
                score += 50;
                reasons.Add("Same purchase");
            }

            if (score < MinimumScore) continue;

            candidates.Add(new RefundCandidate(
                original.Id,
                originalDate,
                original.Counterparty,
                original.Amount,
                original.Currency,
                original.CategoryId,
                originalPurchase?.Id,
                Math.Min(100, score),
                score >= 75 ? "strong" : score >= 45 ? "good" : "possible",
                reasons));
        }

        return candidates.OrderByDescending(candidate => candidate.MatchStrength).ToList();
    }

    private static bool ContainsRefundSignal(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var upper = text.ToUpperInvariant();
        return upper.Contains("REFUND") || upper.Contains("ERSTATT") || upper.Contains("RÜCKERSTATT")
            || upper.Contains("RUECKERSTATT") || upper.Contains("RETOURE") || upper.Contains("RETURN");
    }
}
