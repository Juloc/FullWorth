namespace FullWorth.Backend.Validation;

/// <summary>
/// Small, framework-free input-validation helpers. Each returns a human-readable error message, or
/// <c>null</c> when the value is valid, so modules share one definition of the common DTO rules
/// instead of re-implementing them (currency shape, required names, numeric ranges, date order,
/// pagination bounds).
/// </summary>
public static class Validate
{
    public static string? RequiredName(string? value, string field = "Name") =>
        string.IsNullOrWhiteSpace(value) ? $"{field} is required." : null;

    /// <summary>ISO-4217-shaped: exactly three ASCII letters (case-insensitive).</summary>
    public static string? Currency(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "Currency is required.";
        var code = value.Trim().ToUpperInvariant();
        return code.Length == 3 && code.All(character => character is >= 'A' and <= 'Z')
            ? null
            : "Currency must be a three-letter code.";
    }

    /// <summary>True when the value is a valid three-letter currency code (no message).</summary>
    public static bool IsCurrency(string? value) => Currency(value) is null;

    public static string? Positive(decimal value, string field) =>
        value <= 0 ? $"{field} must be greater than zero." : null;

    public static string? NonNegative(decimal value, string field) =>
        value < 0 ? $"{field} must not be negative." : null;

    public static string? DateOrder(DateOnly? start, DateOnly? end, string startField = "Start date", string endField = "End date") =>
        start is { } s && end is { } e && e < s ? $"{endField} must not be before {startField}." : null;

    /// <summary>Clamp a client-supplied page size into [1, max], falling back to <paramref name="fallback"/> when unset.</summary>
    public static int PageSize(int? requested, int fallback, int max) =>
        Math.Clamp(requested ?? fallback, 1, max);

    /// <summary>A non-negative offset from a client-supplied value.</summary>
    public static int Offset(int? requested) => Math.Max(0, requested ?? 0);
}
