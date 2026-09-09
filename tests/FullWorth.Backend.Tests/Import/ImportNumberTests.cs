using FullWorth.Backend.Modules.Parity;

namespace FullWorth.Backend.Tests.Import;

/// <summary>
/// The parser every importer now shares. Before it, two importers parsed amounts with a fixed German
/// culture and NumberStyles.Number - which allows thousands grouping - before trying the invariant
/// culture, and .NET does not validate group sizes: "1234.56" parsed successfully as 123456 and was
/// committed a hundred times too large. For .xlsx that was guaranteed rather than locale-dependent,
/// because OOXML always stores cell values invariant.
/// </summary>
public sealed class ImportNumberTests
{
    // Unambiguous: both separators present, so the last one is the decimal separator.
    [Theory]
    [InlineData("1.234,56", 1234.56)]
    [InlineData("1,234.56", 1234.56)]
    [InlineData("-1.234,56", -1234.56)]
    [InlineData("1.234.567,89", 1234567.89)]
    [InlineData("1,234,567.89", 1234567.89)]
    public void Mixed_separators_take_the_last_one_as_the_decimal_separator(string text, decimal expected)
    {
        Assert.Equal(expected, ImportNumber.Parse(text, ImportNumber.ThreeDigitTail.Grouping));
        Assert.Equal(expected, ImportNumber.Parse(text, ImportNumber.ThreeDigitTail.Decimal));
    }

    // One separator with one or two trailing digits cannot be grouping, because a group is three digits.
    [Theory]
    [InlineData("1234.56", 1234.56)]
    [InlineData("1234,56", 1234.56)]
    [InlineData("0.99", 0.99)]
    [InlineData("0,5", 0.5)]
    [InlineData("-0.99", -0.99)]
    [InlineData("12.3456789", 12.3456789)]
    public void A_single_separator_that_cannot_be_grouping_is_the_decimal_separator(string text, decimal expected)
    {
        Assert.Equal(expected, ImportNumber.Parse(text, ImportNumber.ThreeDigitTail.Grouping));
        Assert.Equal(expected, ImportNumber.Parse(text, ImportNumber.ThreeDigitTail.Decimal));
    }

    // The genuinely ambiguous shape, and the reason the policy exists: "1.234" is 1234 in a German
    // statement and 1.234 in an English price list. The caller knows which field it is reading.
    [Theory]
    [InlineData("1.234")]
    [InlineData("1,234")]
    public void Three_trailing_digits_follow_the_callers_policy(string text)
    {
        Assert.Equal(1234m, ImportNumber.Parse(text, ImportNumber.ThreeDigitTail.Grouping));
        Assert.Equal(1.234m, ImportNumber.Parse(text, ImportNumber.ThreeDigitTail.Decimal));
    }

    [Theory]
    [InlineData("1234", 1234)]
    [InlineData("-1234", -1234)]
    [InlineData("0", 0)]
    public void Plain_integers_pass_through(string text, decimal expected)
    {
        Assert.Equal(expected, ImportNumber.Parse(text, ImportNumber.ThreeDigitTail.Grouping));
    }

    // Shapes that real bank exports use: currency symbols, the non-breaking spaces German and French
    // exports group with, and the trailing debit sign of MT940-derived files.
    [Theory]
    [InlineData("€ 1.234,56", 1234.56)]
    [InlineData("1 234,56 €", 1234.56)]
    [InlineData("$1,234.56", 1234.56)]
    [InlineData("1'234.56", 1234.56)]
    [InlineData("1.234,56-", -1234.56)]
    [InlineData("1.234,56+", 1234.56)]
    public void Decoration_and_trailing_signs_are_understood(string text, decimal expected)
    {
        Assert.Equal(expected, ImportNumber.Parse(text, ImportNumber.ThreeDigitTail.Grouping));
    }

    // A large zero-decimal currency: the case that used to be inflated silently.
    [Fact]
    public void Large_zero_decimal_amounts_keep_their_magnitude()
    {
        Assert.Equal(15000000m, ImportNumber.Parse("15.000.000", ImportNumber.ThreeDigitTail.Grouping));
        Assert.Equal(15000000m, ImportNumber.Parse("15,000,000", ImportNumber.ThreeDigitTail.Grouping));
        Assert.Equal(15000000m, ImportNumber.Parse("15000000", ImportNumber.ThreeDigitTail.Grouping));
    }

    [Fact]
    public void Blank_input_is_absent_rather_than_zero()
    {
        Assert.Null(ImportNumber.TryParse(null, ImportNumber.ThreeDigitTail.Grouping));
        Assert.Null(ImportNumber.TryParse("   ", ImportNumber.ThreeDigitTail.Grouping));
        Assert.Null(ImportNumber.TryParse("€", ImportNumber.ThreeDigitTail.Grouping));
        Assert.Throws<FormatException>(() => ImportNumber.Parse("", ImportNumber.ThreeDigitTail.Grouping));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("12abc")]
    [InlineData("1..2")]
    [InlineData("-")]
    public void Unparseable_text_throws_instead_of_becoming_a_number(string text)
    {
        Assert.Throws<FormatException>(() => ImportNumber.Parse(text, ImportNumber.ThreeDigitTail.Grouping));
    }
}
