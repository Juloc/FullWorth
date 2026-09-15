namespace FullWorth.Backend.Modules.Contracts;

public static class ContractIdentity
{
    private static readonly HashSet<string> LegalSuffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "AG", "GMBH", "KG", "OHG", "SE", "SA", "SAS", "BV", "NV", "INC", "LTD", "LLC", "PLC", "AB"
    };

    public static string Normalize(string? value)
    {
        var folded = (value ?? string.Empty)
            .Trim()
            .ToUpperInvariant()
            .Replace("Ä", "AE", StringComparison.Ordinal)
            .Replace("Ö", "OE", StringComparison.Ordinal)
            .Replace("Ü", "UE", StringComparison.Ordinal)
            .Replace("ẞ", "SS", StringComparison.Ordinal)
            .Replace("ß", "SS", StringComparison.Ordinal);

        var normalized = new string(folded
            .Select(character => char.IsLetterOrDigit(character) ? character : ' ')
            .ToArray());
        var tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (tokens.Count > 1 && LegalSuffixes.Contains(tokens[^1])) tokens.RemoveAt(tokens.Count - 1);
        return string.Join(' ', tokens);
    }

    public static bool Matches(string normalizedIdentity, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(normalizedIdentity) || string.IsNullOrWhiteSpace(candidate)) return false;
        var normalizedCandidate = Normalize(candidate);
        if (normalizedCandidate.Length == 0) return false;
        if (string.Equals(normalizedIdentity, normalizedCandidate, StringComparison.Ordinal)) return true;

        var left = normalizedIdentity.Replace(" ", string.Empty, StringComparison.Ordinal);
        var right = normalizedCandidate.Replace(" ", string.Empty, StringComparison.Ordinal);
        return left.Length >= 6 && right.Length >= 6 &&
               (left.Contains(right, StringComparison.Ordinal) || right.Contains(left, StringComparison.Ordinal));
    }
}
