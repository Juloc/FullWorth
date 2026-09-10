using FullWorth.Backend.Modules.Fx;

namespace FullWorth.Backend.Tests.Fx;

/// <summary>
/// A converted total was a bare number. Nobody could tell which rate produced it, and the snapshot
/// accepts a fixing up to fourteen days old for a date with no exact entry (weekends and holidays have
/// no ECB fixing) — so a two-week-old rate looked exactly like this morning's.
///
/// The lookback stays: shortening it would turn conversions that work today into missing numbers, which
/// trades a stated uncertainty for no answer at all. What changed is that the conversion now says what
/// it used.
/// </summary>
public sealed class FxConversionProvenanceTests
{
    private static readonly DateOnly Monday = new(2026, 9, 7);

    [Fact]
    public void A_conversion_reports_the_rate_and_the_fixing_date_it_used()
    {
        var snapshot = new FxSnapshot("EUR", [(Monday, "USD", 1.25m)]);

        var conversion = snapshot.ConvertToBase(100m, "USD", Monday);

        Assert.NotNull(conversion);
        Assert.Equal(80m, conversion.Amount);
        Assert.Equal("USD", conversion.From);
        // 1 EUR = 1.25 USD, so one USD is worth 0.8 EUR - the rate the user was actually charged.
        Assert.Equal(0.8m, conversion.Rate);
        Assert.Equal(Monday, conversion.RateDate);
        Assert.Equal(0, conversion.AgeInDays(Monday));
    }

    // A weekend has no fixing, so Friday's rate is used - and the answer says so instead of implying
    // the rate is Sunday's.
    [Fact]
    public void A_weekend_conversion_names_the_friday_fixing_it_fell_back_to()
    {
        var friday = new DateOnly(2026, 9, 4);
        var sunday = new DateOnly(2026, 9, 6);
        var snapshot = new FxSnapshot("EUR", [(friday, "USD", 1.25m)]);

        var conversion = snapshot.ConvertToBase(100m, "USD", sunday);

        Assert.Equal(friday, conversion!.RateDate);
        Assert.Equal(2, conversion.AgeInDays(sunday));
        Assert.True(conversion.AgeInDays(sunday) <= FxSnapshot.StaleAfterDays, "a weekend is not stale");
    }

    // This is the case that used to be invisible.
    [Fact]
    public void A_fixing_older_than_the_stale_threshold_is_still_used_but_reports_its_age()
    {
        var old = Monday.AddDays(-10);
        var snapshot = new FxSnapshot("EUR", [(old, "USD", 1.25m)]);

        var conversion = snapshot.ConvertToBase(100m, "USD", Monday);

        Assert.Equal(80m, conversion!.Amount);
        Assert.Equal(10, conversion.AgeInDays(Monday));
        Assert.True(conversion.AgeInDays(Monday) > FxSnapshot.StaleAfterDays);
    }

    // Beyond the lookback there is no rate, and there must be no number either: never 1:1, never 0.
    [Fact]
    public void Beyond_the_lookback_there_is_no_conversion_at_all()
    {
        var snapshot = new FxSnapshot("EUR", [(Monday.AddDays(-30), "USD", 1.25m)]);

        Assert.Null(snapshot.ConvertToBase(100m, "USD", Monday));
        Assert.Null(snapshot.ToBaseOn(100m, "USD", Monday));
    }

    // A cross-rate goes through EUR and therefore uses two fixings. A total is only as current as its
    // stalest input, so the older date is the one reported.
    [Fact]
    public void A_cross_rate_reports_the_older_of_the_two_fixings()
    {
        var usdDate = Monday;
        var chfDate = Monday.AddDays(-3);
        var snapshot = new FxSnapshot("CHF", [(usdDate, "USD", 1.25m), (chfDate, "CHF", 0.95m)]);

        var conversion = snapshot.ConvertToBase(100m, "USD", Monday);

        Assert.Equal(chfDate, conversion!.RateDate);
        Assert.Equal(76m, conversion.Amount);
        Assert.Equal(0.76m, conversion.Rate);
    }

    // An amount already in the base currency was not converted. Reporting a rate of 1 "as of" some
    // fixing would invent provenance for a number nobody converted.
    [Fact]
    public void An_amount_already_in_the_base_currency_reports_no_conversion()
    {
        var snapshot = new FxSnapshot("EUR", [(Monday, "USD", 1.25m)]);

        Assert.Null(snapshot.ConvertToBase(100m, "EUR", Monday));
        // The plain conversion still answers, because the amount itself is valid in the base currency.
        Assert.Equal(100m, snapshot.ToBaseOn(100m, "EUR", Monday));
    }

    // The old entry point must keep behaving exactly as before, or every existing caller shifts.
    [Fact]
    public void The_plain_conversion_still_returns_the_same_amount()
    {
        var snapshot = new FxSnapshot("EUR", [(Monday, "USD", 1.25m)]);

        Assert.Equal(80m, snapshot.ToBaseOn(100m, "USD", Monday));
        Assert.Equal(snapshot.ConvertToBase(100m, "USD", Monday)!.Amount, snapshot.ToBaseOn(100m, "USD", Monday));
    }

    // A rate of zero is not a rate. Dividing by it would produce infinity, not a balance.
    [Fact]
    public void A_zero_rate_converts_nothing()
    {
        var snapshot = new FxSnapshot("EUR", [(Monday, "USD", 0m)]);

        Assert.Null(snapshot.ConvertToBase(100m, "USD", Monday));
    }
}
