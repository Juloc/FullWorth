using System.Globalization;

namespace FullWorth.Backend.Modules.Parity;

/// <summary>
/// The one number parser for imported files.
///
/// Every importer used to bring its own, and two of them parsed with a fixed German culture and
/// <see cref="NumberStyles.Number"/> - which includes AllowThousands - before trying the invariant
/// culture. .NET does not validate group sizes, so "1234.56" parsed successfully in de-DE as
/// one-two-three-four-hundred-fifty-six: every dot-decimal amount was committed a hundred times too
/// large. For .xlsx that was not even locale-dependent, because OOXML always stores cell values
/// invariant, so an English spreadsheet was guaranteed to be misread.
///
/// The rule here decides from the separators in the text instead of from a culture:
/// <list type="bullet">
///   <item>Both separators present - the LAST one is the decimal separator ("1.234,56", "1,234.56").</item>
///   <item>One separator, repeated - grouping ("1.234.567").</item>
///   <item>One separator, once, with 1 or 2 trailing digits - decimal, because a group is always three
///         digits ("1234.5", "0,99").</item>
///   <item>One separator, once, with more than 3 trailing digits - decimal ("12.3456789").</item>
///   <item>One separator, once, with exactly 3 trailing digits - genuinely ambiguous ("1.234" is 1234
///         in a German export and 1.234 in an English one). The caller decides via
///         <see cref="ThreeDigitTail"/>, because the right answer differs by field: a statement amount
///         is money with two decimals, so three trailing digits mean grouping, while a unit price or a
///         share quantity legitimately carries three or four decimals.</item>
/// </list>
/// Nothing here is locale-dependent, so the same file imports the same way on every host.
/// </summary>
internal static class ImportNumber
{
    /// <summary>How to read a single separator followed by exactly three digits.</summary>
    internal enum ThreeDigitTail
    {
        /// <summary>"1.234" is 1234. Correct for statement amounts, which carry two decimals.</summary>
        Grouping,

        /// <summary>"1.234" is 1.234. Correct for unit prices and quantities.</summary>
        Decimal
    }

    /// <summary>Parses an amount, or throws <see cref="FormatException"/> with the original text.</summary>
    internal static decimal Parse(string? value, ThreeDigitTail tail)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new FormatException("Amount is missing.");
        return TryParse(value, tail) ?? throw new FormatException($"Invalid amount '{value}'.");
    }

    /// <summary>Returns null for blank input; throws <see cref="FormatException"/> for unparseable input.</summary>
    internal static decimal? TryParse(string? value, ThreeDigitTail tail)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        // Currency symbols and every kind of space a bank export uses as a group separator, including
        // the non-breaking and narrow no-break spaces that German and French exports emit.
        var text = new string(value
            .Where(character => character is not ('€' or '$' or '£' or '\'' or '_')
                                && !char.IsWhiteSpace(character)
                                && character != ' '
                                && character != ' ')
            .ToArray());
        if (text.Length == 0) return null;

        // A trailing sign ("1.234,56-") is how some MT940-derived exports mark a debit.
        var negative = false;
        if (text.EndsWith('-') || text.EndsWith('+'))
        {
            negative = text.EndsWith('-');
            text = text[..^1];
        }
        else if (text.StartsWith('-') || text.StartsWith('+'))
        {
            negative = text.StartsWith('-');
            text = text[1..];
        }
        if (text.Length == 0) return null;

        var commas = text.Count(character => character == ',');
        var dots = text.Count(character => character == '.');
        string normalized;

        if (commas > 0 && dots > 0)
        {
            var decimalSeparator = text.LastIndexOf(',') > text.LastIndexOf('.') ? ',' : '.';
            normalized = Recombine(text, decimalSeparator);
        }
        else if (commas + dots == 0)
        {
            normalized = text;
        }
        else
        {
            var separator = commas > 0 ? ',' : '.';
            var parts = text.Split(separator);

            // Every segment must be digits, and a grouping segment is exactly three of them. Checking
            // the segments rather than the recombined string is what rejects malformed input like
            // "1..2", which would otherwise recombine into a plausible 1.2.
            if (parts.Any(part => part.Length == 0 || !part.All(char.IsAsciiDigit)))
                throw new FormatException($"Invalid amount '{value}'.");

            var leadingLooksGrouped = parts[0].Length is >= 1 and <= 3;
            var middleAreGroups = parts[1..^1].All(part => part.Length == 3);
            var tailIsGroup = parts[^1].Length == 3;

            if (parts.Length > 2)
            {
                // Repeated separator, so it is grouping - except for the unusual but unambiguous shape
                // "1.234.567.89", where the last segment is too short to be a group and is therefore
                // the decimal part.
                if (!leadingLooksGrouped || !middleAreGroups)
                    throw new FormatException($"Invalid amount '{value}'.");
                normalized = tailIsGroup
                    ? string.Concat(parts)
                    : string.Concat(parts[..^1]) + "." + parts[^1];
            }
            else if (tailIsGroup && tail == ThreeDigitTail.Grouping && leadingLooksGrouped)
            {
                normalized = string.Concat(parts);
            }
            else
            {
                normalized = parts[0] + "." + parts[1];
            }
        }

        if (!decimal.TryParse(normalized, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount))
            throw new FormatException($"Invalid amount '{value}'.");
        return negative ? -amount : amount;
    }

    private static string Recombine(string text, char decimalSeparator)
    {
        var grouping = decimalSeparator == ',' ? '.' : ',';
        var withoutGrouping = text.Replace(grouping.ToString(), string.Empty);
        return decimalSeparator == ','
            ? withoutGrouping.Replace(',', '.')
            : withoutGrouping;
    }
}
