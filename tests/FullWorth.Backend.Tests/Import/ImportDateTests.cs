using System.Globalization;
using FullWorth.Backend.Modules.Parity;

namespace FullWorth.Backend.Tests.Import;

/// <summary>
/// Every date parser ended in <c>DateOnly.TryParse(text)</c>, which parses with the HOST's culture.
/// Measured before the fix: <c>"03.04.2026"</c> — 3 April, as PayPal, Finanzguru and every German bank
/// export write it — read as 2026-04-03 under <c>de-DE</c> but as **2026-03-04** under <c>en-US</c> and
/// under the invariant culture a container runs with. The same file imported on two hosts produced
/// bookings a month apart, and every monthly figure over them was wrong.
///
/// This is the date half of the bug <see cref="ImportNumber"/> already fixes for amounts.
/// </summary>
public sealed class ImportDateTests
{
    /// <summary>The whole point: the answer may not depend on the machine.</summary>
    [Theory]
    [InlineData("de-DE")]
    [InlineData("en-US")]
    [InlineData("")]
    public void A_german_date_means_the_same_thing_on_every_host(string culture)
    {
        using var _ = Culture(culture);

        Assert.Equal(new DateOnly(2026, 4, 3), ImportDate.Parse("03.04.2026"));
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("en-US")]
    [InlineData("")]
    public void Every_supported_shape_means_the_same_thing_on_every_host(string culture)
    {
        using var _ = Culture(culture);

        Assert.Equal(new DateOnly(2026, 4, 3), ImportDate.Parse("2026-04-03"));
        Assert.Equal(new DateOnly(2026, 4, 3), ImportDate.Parse("2026/04/03"));
        Assert.Equal(new DateOnly(2026, 4, 3), ImportDate.Parse("3.4.2026"));
        Assert.Equal(new DateOnly(2026, 4, 3), ImportDate.Parse("20260403"));
        Assert.Equal(new DateOnly(2026, 4, 3), ImportDate.Parse("03-04-2026"));
        // A timestamp is a date with a time attached, not an unreadable value.
        Assert.Equal(new DateOnly(2026, 4, 3), ImportDate.Parse("2026-04-03T14:22:05Z"));
        Assert.Equal(new DateOnly(2026, 4, 3), ImportDate.Parse("2026-04-03 14:22:05"));
    }

    // Day-first, because that is the order the previous format list tried first: changing it would
    // silently re-read every import already committed.
    [Fact]
    public void A_slash_date_is_read_day_first()
    {
        Assert.Equal(new DateOnly(2026, 4, 3), ImportDate.Parse("03/04/2026"));
    }

    // A first field above 12 can only be a day, so month-first still resolves rather than failing.
    [Fact]
    public void A_slash_date_that_can_only_be_month_first_still_resolves()
    {
        Assert.Equal(new DateOnly(2026, 4, 13), ImportDate.Parse("04/13/2026"));
    }

    // No parser can resolve 04/03 — so the ambiguity is reportable instead of hidden.
    [Fact]
    public void An_ambiguous_slash_date_is_recognised_as_ambiguous()
    {
        Assert.True(ImportDate.IsAmbiguousSlashDate("04/03/2026"));
        Assert.False(ImportDate.IsAmbiguousSlashDate("04/13/2026"));
        Assert.False(ImportDate.IsAmbiguousSlashDate("03.04.2026"));
        // Same number twice is one date either way, so there is nothing to warn about.
        Assert.False(ImportDate.IsAmbiguousSlashDate("04/04/2026"));
    }

    // Never today, never DateOnly.MinValue: an unreadable date has to stay missing, or a booking lands
    // on a day nothing happened.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a date")]
    [InlineData("32.13.2026")]
    [InlineData("2026-13-45")]
    public void An_unreadable_date_is_missing_rather_than_guessed(string? value)
    {
        Assert.Null(ImportDate.TryParse(value));
        if (!string.IsNullOrWhiteSpace(value)) Assert.Throws<FormatException>(() => ImportDate.Parse(value));
    }

    // Spreadsheets store a date as a day count from 1899-12-30, but only the importer that reads
    // spreadsheets may treat a bare number as one - elsewhere a number is an amount.
    [Fact]
    public void An_excel_serial_is_only_a_date_where_a_spreadsheet_is_expected()
    {
        Assert.Equal(new DateOnly(2026, 4, 3), ImportDate.Parse("46115", allowExcelSerial: true));
        Assert.Null(ImportDate.TryParse("46115"));
    }

    // The bound keeps an amount from being read as a date: 1234.56 is money, not the year 1903.
    [Fact]
    public void An_amount_is_not_mistaken_for_an_excel_serial()
    {
        Assert.Null(ImportDate.TryParse("1234.56", allowExcelSerial: true));
        Assert.Null(ImportDate.TryParse("-42", allowExcelSerial: true));
    }

    private static CultureScope Culture(string name) => new(name);

    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo previous = CultureInfo.CurrentCulture;

        public CultureScope(string name) =>
            CultureInfo.CurrentCulture = name.Length == 0 ? CultureInfo.InvariantCulture : new CultureInfo(name);

        public void Dispose() => CultureInfo.CurrentCulture = previous;
    }
}
