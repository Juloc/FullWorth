using System.Text.RegularExpressions;

namespace FullWorth.Banking.Services;

/// <summary>
/// Aus dem Verwendungszweck einer Bankbuchung das machen, was ein Mensch lesen will.
///
/// Banken liefern in diesem Feld zwei Dinge gemischt: den eigentlichen Zweck und technische
/// Referenzen, die nur die Bank interessieren. Beides kommt zusammengeklebt an, und die Trennzeichen
/// unterscheiden sich je Institut - mal <c>|</c>, mal ein Zeilenumbruch, mal <c>;</c>.
///
/// Zwei Regeln halten das auseinander:
///
/// Ein Stueck, das mit einer technischen Kennung beginnt (<c>mandatereference:</c>,
/// <c>creditorid:</c>, <c>eref+</c>, …), faellt ganz weg - dahinter steht nie etwas Lesbares.
///
/// Ein Stueck, das mit einer BESCHRIFTUNG beginnt (<c>remittanceinformation:</c>, <c>SVWZ+</c>),
/// verliert nur die Beschriftung. Dahinter steht der Zweck selbst; wer das Stueck wegwirft, wirft
/// die Information weg, die der Benutzer sehen wollte.
///
/// Die Rohdaten bleiben unangetastet - sie stehen weiter im gespeicherten Payload und in der
/// Detailansicht. Hier entsteht nur die Zeile unter der Buchung.
///
/// Dieselbe Regel gibt es ein zweites Mal in JavaScript, in
/// <c>wwwroot/pages/transactions/page.js</c> (<c>transactionListPurpose</c>), weil die Liste sie auch
/// auf bereits gespeicherte Buchungen anwenden muss. Wer hier etwas aendert, aendert es dort mit.
/// </summary>
public static class TransactionPurpose
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(100);

    /// <summary>Kennungen, hinter denen nie ein lesbarer Zweck steht - das Stueck faellt weg.</summary>
    private const string TechnicalPattern =
        @"^(?:EREF|MREF|KREF|CRED|DEBT|ABWA|ABWE|PURP|COAM|ENDTOEND\w*|MANDATE\w*|CREDITOR\w*|DEBTOR\w*|TRANSACTION(?:\s+ID)?|TXID|REFERENCE|REF)\s*[:+=]";

    /// <summary>Beschriftungen, hinter denen der Zweck steht - nur die Beschriftung faellt weg.</summary>
    private const string LabelPattern =
        @"^(?:REMITTANCE(?:INFORMATION)?|SVWZ|VERWENDUNGSZWECK|PURPOSE)\s*[:+=]\s*";

    /// <summary>Womit Banken die Stuecke trennen.</summary>
    private static readonly char[] Separators = ['|', ';', '\n', '\r'];

    public static string? Normalize(string? raw, string? counterparty)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        // SVWZ+ ist der ausdrueckliche SEPA-Zweck; steht er da, ist die Frage beantwortet.
        if (TryExtractSepaPurpose(raw) is { Length: > 0 } sepaPurpose) return Limit(sepaPurpose);

        var candidates = new List<string>();
        foreach (var piece in raw.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var text = StripLabel(NormalizePart(piece));
            if (string.IsNullOrWhiteSpace(text) || IsTechnical(text)) continue;
            if (!string.IsNullOrWhiteSpace(counterparty) &&
                string.Equals(text, NormalizePart(counterparty), StringComparison.OrdinalIgnoreCase))
                continue;
            if (candidates.Contains(text, StringComparer.OrdinalIgnoreCase)) continue;

            candidates.Add(text);
            if (candidates.Count == 2) break;
        }

        return candidates.Count == 0 ? null : Limit(string.Join(" · ", candidates));
    }

    public static bool IsTechnical(string value) =>
        Regex.IsMatch(value.Trim(), TechnicalPattern, RegexOptions.IgnoreCase, Timeout)
        || Guid.TryParse(value.Trim(), out _)
        || Regex.IsMatch(value.Trim(), @"^\d{14,}$", RegexOptions.CultureInvariant, Timeout)
        || Regex.IsMatch(value.Trim(), @"^[0-9A-F]{20,}$", RegexOptions.IgnoreCase, Timeout);

    private static string? StripLabel(string? value) =>
        value is null ? null : NormalizePart(Regex.Replace(value, LabelPattern, string.Empty, RegexOptions.IgnoreCase, Timeout));

    private static string? TryExtractSepaPurpose(string raw)
    {
        var match = Regex.Match(
            raw,
            @"(?:^|\s)SVWZ\+(.*?)(?=\s+(?:EREF|MREF|KREF|CRED|DEBT|ABWA|ABWE|PURP|COAM)\+|\s*[|;]\s*|$)",
            RegexOptions.IgnoreCase | RegexOptions.Singleline,
            Timeout);
        return match.Success ? NormalizePart(match.Groups[1].Value) : null;
    }

    private static string? NormalizePart(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = Regex.Replace(value.Trim(), @"\s+", " ", RegexOptions.None, Timeout);
        return normalized.Length == 0 ? null : normalized;
    }

    private static string Limit(string value)
    {
        var normalized = NormalizePart(value) ?? string.Empty;
        const int max = 180;
        return normalized.Length <= max ? normalized : normalized[..(max - 1)].TrimEnd() + "…";
    }
}
