using System.Globalization;

namespace FullWorth.Backend.Modules.Parity;

/// <summary>
/// The one date parser for imported and provider-supplied values — the counterpart to
/// <see cref="ImportNumber"/>, and it exists for the same reason.
///
/// Every caller used to fall back to <c>DateOnly.TryParse(text)</c>, which parses with the HOST's
/// current culture. Measured: <c>"03.04.2026"</c> (3 April, as PayPal, Finanzguru and every German bank
/// export write it) reads as
/// <list type="bullet">
///   <item>2026-04-03 under <c>de-DE</c> — correct;</item>
///   <item>2026-03-04 under <c>en-US</c> — day and month swapped;</item>
///   <item>2026-03-04 under the invariant culture, which is what a container runs with
///         <c>DOTNET_SYSTEM_GLOBALIZATION_INVARIANT</c> or no ICU data.</item>
/// </list>
/// So the same file imported on two hosts produced bookings a month apart, and every monthly figure
/// derived from them was wrong. Nothing about a stored booking date may depend on the host's locale.
///
/// The slash form <c>04/03/2026</c> is genuinely ambiguous and no parser can resolve it. Day-first is
/// kept because that is what the previous format list tried first, so existing imports keep their
/// meaning; <see cref="IsAmbiguousSlashDate"/> lets a caller say so rather than pretend otherwise.
/// </summary>
internal static class ImportDate
{
    /// <summary>Day-first before month-first: a European product, and what the old order already did.</summary>
    private static readonly string[] Formats =
    [
        "yyyy-MM-dd",
        "yyyy/MM/dd",
        "dd.MM.yyyy",
        "d.M.yyyy",
        "dd/MM/yyyy",
        "d/M/yyyy",
        "MM/dd/yyyy",
        "dd-MM-yyyy",
        "yyyyMMdd"
    ];

    /// <summary>Excel writes a date as a day count from 1899-12-30. Bounded so an amount is not read as one.</summary>
    private const double EarliestSerial = 20000;  // 1954-10-03
    private const double LatestSerial = 100000;   // far enough out to be unmistakable

    /// <summary>Null for blank or unparseable input. Never guesses, never falls back to today.</summary>
    internal static DateOnly? TryParse(string? value, bool allowExcelSerial = false)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();

        // An ISO timestamp is an ISO date with a time attached; take the date part rather than failing.
        var separator = text.IndexOfAny(['T', ' ']);
        if (separator == 10 && DateOnly.TryParseExact(
                text[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var isoPrefix))
            return isoPrefix;

        foreach (var format in Formats)
            if (DateOnly.TryParseExact(text, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                return parsed;

        if (allowExcelSerial &&
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial) &&
            serial is > EarliestSerial and < LatestSerial)
            return DateOnly.FromDateTime(new DateTime(1899, 12, 30).AddDays(serial));

        return null;
    }

    /// <summary>Parses, or throws <see cref="FormatException"/> naming the value the way the row showed it.</summary>
    internal static DateOnly Parse(string? value, bool allowExcelSerial = false)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new FormatException("Date is missing.");
        return TryParse(value, allowExcelSerial) ?? throw new FormatException($"Invalid date '{value}'.");
    }

    /// <summary>
    /// True when the text is a slash date whose first two fields are both 1-12, so day-first and
    /// month-first are both readings and the choice is a convention rather than a fact.
    /// </summary>
    internal static bool IsAmbiguousSlashDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var parts = value.Trim().Split('/');
        return parts.Length == 3
               && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var first)
               && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var second)
               && first is >= 1 and <= 12
               && second is >= 1 and <= 12
               && first != second;
    }
}
