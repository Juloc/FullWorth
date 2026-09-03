namespace FullWorth.Backend.Modules.Intelligence;

/// <summary>
/// Pure GTIN (barcode) normalization helper. Produces a stable, check-digit-validated
/// <c>gtin:{digits}</c> subject key from a raw barcode string. This is a small local utility used by
/// product learning; it carries no cloud contribution or knowledge-pack machinery.
/// </summary>
public static class GtinKey
{
    public static bool TryCreateGtinSubjectKey(string? barcode, out string? subjectKey)
    {
        subjectKey = null;
        if (string.IsNullOrWhiteSpace(barcode)) return false;
        var raw = barcode.Trim();
        if (raw.Any(ch => !char.IsDigit(ch) && ch is not (' ' or '-'))) return false;
        var digits = new string(raw.Where(char.IsDigit).ToArray());
        if (digits.Length is not (8 or 12 or 13 or 14)) return false;
        if (!HasValidGtinCheckDigit(digits)) return false;
        subjectKey = $"gtin:{digits}";
        return true;
    }

    private static bool HasValidGtinCheckDigit(string digits)
    {
        var sum = 0;
        var weightThree = true;
        for (var i = digits.Length - 2; i >= 0; i--)
        {
            var digit = digits[i] - '0';
            sum += digit * (weightThree ? 3 : 1);
            weightThree = !weightThree;
        }
        var expected = (10 - (sum % 10)) % 10;
        return digits[^1] - '0' == expected;
    }
}
