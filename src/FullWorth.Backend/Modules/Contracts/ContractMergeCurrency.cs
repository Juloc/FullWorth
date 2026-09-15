namespace FullWorth.Backend.Modules.Contracts;

// A merge must only be refused for a real conflict. A contract row can carry no currency at all
// (older imports, hand-written rows, anything created before the write path required a code), and an
// unknown currency is not a different one - it stays compatible with any code in the selection. Two
// genuinely different codes remain a conflict, and the refusal names both.
public static class ContractMergeCurrency
{
    public static string Normalize(string? currency) =>
        string.IsNullOrWhiteSpace(currency) ? string.Empty : currency.Trim().ToUpperInvariant();

    /// <summary>
    /// The single currency a selection agrees on. Contracts without a currency do not vote; the result
    /// is empty when nothing in the selection knows its currency. Returns false only when two known
    /// codes disagree - that is the one case a merge must refuse.
    /// </summary>
    public static bool TryResolve(IEnumerable<string?> currencies, out string resolved)
    {
        var known = KnownCodes(currencies);
        resolved = known.Length == 1 ? known[0] : string.Empty;
        return known.Length <= 1;
    }

    public static string[] KnownCodes(IEnumerable<string?> currencies) =>
        currencies
            .Select(Normalize)
            .Where(currency => currency.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(currency => currency, StringComparer.Ordinal)
            .ToArray();

    public static string ConflictError(IEnumerable<string?> currencies)
    {
        var known = KnownCodes(currencies);
        return known.Length > 1
            ? $"Contracts with different currencies cannot be merged ({string.Join(", ", known)})."
            : "Contracts with different currencies cannot be merged.";
    }
}
