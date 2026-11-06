using FullWorth.Backend.Modules.Ingestion;

namespace FullWorth.Backend.Tests.Ingestion;

/// <summary>
/// Die Entscheidung, ob eine Buchung eine Vormerkung abgeloest hat, ohne Datenbank. Sie entscheidet, ob
/// eine echte Zahlung aus der Liste verschwindet - deshalb jede Bedingung einzeln, jede in der Richtung,
/// in der ein Fehler eine Zahlung kosten wuerde.
/// </summary>
public sealed class PendingSupersessionMatcherTests
{
    private static readonly DateOnly Day = new(2026, 11, 5);
    private static readonly DateTimeOffset Seen = new(2026, 11, 5, 8, 0, 0, TimeSpan.Zero);

    private static PendingCandidate Pending(int n = 1, decimal amount = -1.28m, int dayOffset = 0, string? shop = "PENNY", int seenMinutes = 0) =>
        new(Guid.Parse($"00000000-0000-0000-0000-{n:D12}"), amount, "EUR", Day.AddDays(dayOffset), shop, Seen.AddMinutes(seenMinutes));

    private static BookedCandidate Booked(int n = 101, decimal amount = -1.28m, int dayOffset = 1, string? shop = "PENNY SAGT DANKE", int seenMinutes = 60) =>
        new(Guid.Parse($"00000000-0000-0000-0000-{n:D12}"), amount, "EUR", Day.AddDays(dayOffset), shop, Seen.AddMinutes(seenMinutes));

    [Fact]
    public void A_booking_a_day_later_with_the_same_amount_and_shop_replaces_the_pending_row()
    {
        var pair = Assert.Single(PendingSupersession.Pair([Pending()], [Booked()]));

        Assert.Equal((Pending().Id, Booked().Id), pair);
    }

    [Theory]
    [InlineData(-1.29, 1, "PENNY", 60, "different cents")]
    [InlineData(-1.28, 12, "PENNY", 60, "booked too long after the pay date")]
    [InlineData(-1.28, -2, "PENNY", 60, "booked before the pay date")]
    [InlineData(-1.28, 1, "KAUFLAND", 60, "different shop")]
    [InlineData(-1.28, 1, null, 60, "booking without a counterparty")]
    [InlineData(-1.28, 1, "PENNY", -30, "booking that was already here before the pending row")]
    public void Any_single_difference_leaves_the_pending_row_alone(double amount, int dayOffset, string? shop, int seenMinutes, string reason)
    {
        var pairs = PendingSupersession.Pair([Pending()], [Booked(amount: (decimal)amount, dayOffset: dayOffset, shop: shop, seenMinutes: seenMinutes)]);

        Assert.True(pairs.Count == 0, reason);
    }

    [Fact]
    public void A_pending_row_without_a_usable_counterparty_never_matches()
    {
        Assert.Empty(PendingSupersession.Pair([Pending(shop: null)], [Booked(shop: null)]));
        Assert.Empty(PendingSupersession.Pair([Pending(shop: "DM")], [Booked(shop: "DM DROGERIE")]));
    }

    [Fact]
    public void Another_currency_is_never_the_same_payment()
    {
        var booked = Booked() with { Currency = "USD" };

        Assert.Empty(PendingSupersession.Pair([Pending()], [booked]));
    }

    // Two payments of 1.28 at Penny on one day: one booking replaces one of them, not both.
    [Fact]
    public void One_booking_replaces_at_most_one_pending_row()
    {
        var pairs = PendingSupersession.Pair([Pending(1), Pending(2)], [Booked(101)]);

        Assert.Single(pairs);
    }

    [Fact]
    public void Two_bookings_replace_two_pending_rows_each_exactly_once()
    {
        var pairs = PendingSupersession.Pair([Pending(1), Pending(2)], [Booked(101), Booked(102)]);

        Assert.Equal(2, pairs.Count);
        Assert.Equal(2, pairs.Select(pair => pair.PendingId).Distinct().Count());
        Assert.Equal(2, pairs.Select(pair => pair.BookedId).Distinct().Count());
    }

    // The oldest pending row picks first and takes the nearest booking, so a later pending row cannot
    // steal the booking that belongs to an earlier one.
    [Fact]
    public void The_oldest_pending_row_gets_the_nearest_booking()
    {
        var early = Pending(1, dayOffset: 0);
        var late = Pending(2, dayOffset: 3);
        var nearEarly = Booked(101, dayOffset: 1);
        var nearLate = Booked(102, dayOffset: 4);

        var pairs = PendingSupersession.Pair([late, early], [nearLate, nearEarly]);

        Assert.Contains((early.Id, nearEarly.Id), pairs);
        Assert.Contains((late.Id, nearLate.Id), pairs);
    }

    [Fact]
    public void The_answer_does_not_depend_on_the_order_the_rows_arrive_in()
    {
        var pending = new[] { Pending(1), Pending(2), Pending(3) };
        var booked = new[] { Booked(101), Booked(102) };

        var forward = PendingSupersession.Pair(pending, booked);
        var reversed = PendingSupersession.Pair(pending.Reverse().ToArray(), booked.Reverse().ToArray());

        Assert.Equal(forward.OrderBy(pair => pair.PendingId), reversed.OrderBy(pair => pair.PendingId));
    }
}
