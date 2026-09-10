using FullWorth.Backend.Modules.Accounts;

namespace FullWorth.Backend.Tests.Accounts;

/// <summary>
/// The balance-type preference used to exist several times over in several shapes — an int switch, a
/// string CASE inside an EF ordering, and a hand-written copy of that CASE inlined into a projection —
/// and nothing covered any type beyond closingBooked / closingAvailable / manual. So the types a real
/// bank actually mixes into one response (interimAvailable, interimBooked, expected) were carried by
/// code nobody had ever exercised.
///
/// These are pure tests over the one remaining rule. No database: the rule is the ordering, and the
/// ordering has to hold before any query is involved.
/// </summary>
public sealed class BalanceTypePreferenceTests
{
    private static readonly Guid Account = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset Capture = new(2026, 9, 9, 6, 12, 0, TimeSpan.Zero);

    private static AccountBalance Row(
        string balanceType,
        decimal amount = 100m,
        string currency = "EUR",
        DateTimeOffset? capturedAt = null,
        string? source = null) =>
        new(Account, amount, currency, balanceType, null, capturedAt ?? Capture, source);

    [Fact]
    public void The_preference_order_is_the_documented_one_and_strictly_ranked()
    {
        Assert.Equal(
            ["interimAvailable", "closingAvailable", "closingBooked", "interimBooked", "expected"],
            CurrentBalances.PreferenceOrder);

        var ranks = CurrentBalances.PreferenceOrder.Select(CurrentBalances.Rank).ToArray();
        Assert.Equal(ranks.OrderBy(rank => rank).ToArray(), ranks);
        Assert.Equal(ranks.Distinct().Count(), ranks.Length);
    }

    // A type FullWorth does not know must be usable but never preferred: a provider extension, or the
    // "manual"/"manualCurrent" anchors written by the app itself.
    [Theory]
    [InlineData("manual")]
    [InlineData("manualCurrent")]
    [InlineData("openingBooked")]
    [InlineData("")]
    [InlineData(null)]
    public void An_unknown_type_ranks_behind_every_known_one(string? balanceType)
    {
        Assert.Equal(CurrentBalances.PreferenceOrder.Count, CurrentBalances.Rank(balanceType));
        Assert.All(
            CurrentBalances.PreferenceOrder,
            known => Assert.True(CurrentBalances.Rank(known) < CurrentBalances.Rank(balanceType)));
    }

    // The case that matters most in practice: one sync stamps EVERY type the bank sent with an
    // identical CapturedAt, so the tiebreak is the only thing standing between the user and a balance
    // that changes meaning between two syncs of the same data.
    [Fact]
    public void All_types_at_the_same_capture_resolve_to_the_most_preferred_one()
    {
        var rows = CurrentBalances.PreferenceOrder
            .Select(type => Row(type))
            .Append(Row("manual"))
            .ToList();

        Assert.Equal("interimAvailable", Assert.Single(CurrentBalances.Pick(rows)).BalanceType);
    }

    // Every type has to be usable on its own. interimAvailable / interimBooked / expected had no test at
    // all, so an account whose bank sends only one of them was carried by unexercised code.
    [Theory]
    [InlineData("interimAvailable")]
    [InlineData("closingAvailable")]
    [InlineData("closingBooked")]
    [InlineData("interimBooked")]
    [InlineData("expected")]
    [InlineData("manual")]
    [InlineData("manualCurrent")]
    public void A_single_type_is_always_picked_whatever_it_is(string balanceType)
    {
        var picked = Assert.Single(CurrentBalances.Pick([Row(balanceType, amount: 42m)]));

        Assert.Equal(balanceType, picked.BalanceType);
        Assert.Equal(42m, picked.Amount);
    }

    // Recency first, preference only as the tiebreak. A fresh booked balance is the current one even
    // though "available" is the preferred KIND.
    [Fact]
    public void A_newer_capture_beats_a_more_preferred_type()
    {
        var rows = new List<AccountBalance>
        {
            Row("interimAvailable", amount: 100m),
            Row("closingBooked", amount: 250m, capturedAt: Capture.AddHours(6))
        };

        var picked = Assert.Single(CurrentBalances.Pick(rows));
        Assert.Equal("closingBooked", picked.BalanceType);
        Assert.Equal(250m, picked.Amount);
    }

    // Row order out of the database is not guaranteed. If the pick depended on it the displayed balance
    // would flip between two identical requests, which is exactly the reported symptom.
    [Fact]
    public void The_pick_never_depends_on_the_order_the_rows_arrive_in()
    {
        var rows = new List<AccountBalance>
        {
            Row("expected", amount: 1m),
            Row("interimBooked", amount: 2m),
            Row("closingBooked", amount: 3m),
            Row("closingAvailable", amount: 4m),
            Row("interimAvailable", amount: 5m),
            Row("manual", amount: 6m)
        };

        var picks = Permutations(rows)
            .Select(order => Assert.Single(CurrentBalances.Pick(order)))
            .Select(balance => $"{balance.BalanceType}:{balance.Amount}")
            .Distinct()
            .ToArray();

        Assert.Equal(["interimAvailable:5"], picks);
    }

    // Two types FullWorth does not know share the last rank, so the type name breaks the tie. Any
    // deterministic answer will do; an ARBITRARY one will not.
    [Fact]
    public void Two_unknown_types_at_one_capture_still_resolve_deterministically()
    {
        var rows = new List<AccountBalance> { Row("zzzCustom", amount: 1m), Row("aaaCustom", amount: 2m) };

        var picks = Permutations(rows)
            .Select(order => Assert.Single(CurrentBalances.Pick(order)).BalanceType)
            .Distinct()
            .ToArray();

        Assert.Equal(["aaaCustom"], picks);
    }

    // The preference is per (account, CURRENCY). A wallet account gets one answer per wallet, and one
    // wallet's type must not decide another wallet's.
    [Fact]
    public void Each_currency_resolves_its_own_preferred_type()
    {
        var rows = new List<AccountBalance>
        {
            Row("closingBooked", amount: 10m, currency: "EUR"),
            Row("interimAvailable", amount: 11m, currency: "EUR"),
            Row("closingBooked", amount: 20m, currency: "USD"),
            Row("expected", amount: 21m, currency: "USD")
        };

        var picked = CurrentBalances.Pick(rows)
            .ToDictionary(balance => balance.Currency, balance => (balance.BalanceType, balance.Amount));

        Assert.Equal(2, picked.Count);
        Assert.Equal(("interimAvailable", 11m), picked["EUR"]);
        Assert.Equal(("closingBooked", 20m), picked["USD"]);
    }

    // The history back-cast walks over BOOKED transactions, so it needs the settled figure. Anchoring
    // it on an available balance shifts every past day by the pending amount.
    [Fact]
    public void PickBooked_takes_the_settled_figure_and_prefers_the_closing_one()
    {
        var rows = new List<AccountBalance>
        {
            Row("interimAvailable", amount: 90m),
            Row("interimBooked", amount: 110m),
            Row("closingBooked", amount: 100m)
        };

        var booked = Assert.Single(CurrentBalances.PickBooked(rows));
        Assert.Equal("closingBooked", booked.BalanceType);
        Assert.Equal(100m, booked.Amount);
    }

    // No settled figure means no anchor, not a substitute one. Returning the available balance here
    // would silently move the whole curve.
    [Theory]
    [InlineData("interimAvailable")]
    [InlineData("closingAvailable")]
    [InlineData("expected")]
    [InlineData("manual")]
    [InlineData("manualCurrent")]
    public void PickBooked_returns_nothing_for_a_type_that_is_not_settled_money(string balanceType)
    {
        Assert.Empty(CurrentBalances.PickBooked([Row(balanceType)]));
    }

    [Theory]
    [InlineData("interimAvailable", BalanceMeanings.Available)]
    [InlineData("closingAvailable", BalanceMeanings.Available)]
    [InlineData("closingBooked", BalanceMeanings.Booked)]
    [InlineData("interimBooked", BalanceMeanings.Booked)]
    [InlineData("expected", BalanceMeanings.Expected)]
    [InlineData("manual", BalanceMeanings.Recorded)]
    [InlineData("manualCurrent", BalanceMeanings.Recorded)]
    [InlineData("somethingNew", BalanceMeanings.Recorded)]
    [InlineData("", BalanceMeanings.Recorded)]
    [InlineData(null, BalanceMeanings.Recorded)]
    public void The_meaning_of_every_type_is_the_one_the_row_will_show(string? balanceType, string meaning)
    {
        Assert.Equal(meaning, CurrentBalances.Meaning(balanceType));
        Assert.Equal(meaning, new BalanceView(1m, "EUR", balanceType ?? "", Capture).Meaning);
    }

    // IsBooked and the label the user reads must be the same judgement. They were two independent
    // hand-written lists; if they disagree, the history anchors on money the row calls available.
    [Fact]
    public void IsBooked_and_the_meaning_can_never_disagree()
    {
        string?[] types =
            [.. CurrentBalances.PreferenceOrder, "manual", "manualCurrent", "somethingNew", "", null];

        Assert.All(types, type => Assert.Equal(
            CurrentBalances.Meaning(type) == BalanceMeanings.Booked,
            CurrentBalances.IsBooked(type)));
    }

    // The provenance is stored, the meaning is derived - so an imported statement's closing balance is
    // BOOKED money that came from a file, and both facts survive independently.
    [Fact]
    public void The_source_and_the_meaning_are_independent_facts()
    {
        var imported = Row("closingBooked", source: BalanceSources.Import);
        var typed = Row("manual", source: BalanceSources.Manual);
        var synced = Row("interimAvailable", source: BalanceSources.Provider);

        Assert.Equal(BalanceMeanings.Booked, CurrentBalances.Meaning(imported.BalanceType));
        Assert.Equal(BalanceSources.Import, imported.Source);
        Assert.Equal(BalanceMeanings.Recorded, CurrentBalances.Meaning(typed.BalanceType));
        Assert.Equal(BalanceMeanings.Available, CurrentBalances.Meaning(synced.BalanceType));
    }

    private static IEnumerable<List<AccountBalance>> Permutations(List<AccountBalance> rows)
    {
        if (rows.Count <= 1) { yield return rows; yield break; }
        for (var index = 0; index < rows.Count; index++)
        {
            var rest = rows.Where((_, position) => position != index).ToList();
            foreach (var tail in Permutations(rest))
                yield return [rows[index], .. tail];
        }
    }
}
