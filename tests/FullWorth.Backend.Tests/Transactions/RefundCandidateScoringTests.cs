using FullWorth.Backend.Modules.Transactions;

namespace FullWorth.Backend.Tests.Transactions;

/// <summary>
/// Welche Ausgabe zu einer Erstattung vorgeschlagen wird, war bis 2026-09-15 nirgends geprueft: die
/// Punktevergabe stand in einer einzigen Zeile zwischen vier Datenbankabfragen, und ein Test dafuer
/// haette eine Datenbank und eine HTTP-Runde gebraucht.
///
/// Seit sie in <see cref="RefundCandidateScoring"/> steht, ist sie eine reine Funktion - und das ist
/// der eigentliche Gewinn der Aufteilung, nicht die Datei-Kosmetik.
/// </summary>
public sealed class RefundCandidateScoringTests
{
    private static readonly DateOnly RefundDay = new(2026, 3, 10);

    private static FinanceTransaction Buchung(
        decimal amount, DateOnly date, string? counterparty = null, string? description = null) => new()
    {
        Id = Guid.NewGuid(),
        AccountId = Guid.NewGuid(),
        Amount = amount,
        BookingDate = date,
        Currency = "EUR",
        Counterparty = counterparty,
        Description = description
    };

    private static IReadOnlyList<RefundCandidate> Bewerte(
        FinanceTransaction refund,
        params FinanceTransaction[] expenses) =>
        RefundCandidateScoring.Rank(refund, RefundDay, expenses, new Dictionary<Guid, RefundPurchaseRef>());

    [Fact]
    public void An_exact_amount_from_the_same_merchant_a_day_later_is_a_strong_match()
    {
        var refund = Buchung(49.90m, RefundDay, "MediaMarkt");
        var ausgabe = Buchung(-49.90m, RefundDay.AddDays(-1), "MediaMarkt");

        var treffer = Assert.Single(Bewerte(refund, ausgabe));

        // 45 Betrag + 30 Haendler + 15 Naehe = 90
        Assert.Equal(90m, treffer.MatchStrength);
        Assert.Equal("strong", treffer.Strength);
        Assert.Contains("Exact amount", treffer.Reasons);
        Assert.Contains("Same merchant", treffer.Reasons);
    }

    [Fact]
    public void A_smaller_refund_is_offered_as_a_partial_one()
    {
        var refund = Buchung(10m, RefundDay, "Otto");
        var ausgabe = Buchung(-80m, RefundDay.AddDays(-3), "Otto");

        var treffer = Assert.Single(Bewerte(refund, ausgabe));

        Assert.Contains("Possible partial refund", treffer.Reasons);
        Assert.DoesNotContain("Exact amount", treffer.Reasons);
    }

    [Fact]
    public void Noise_below_the_threshold_is_not_offered_at_all()
    {
        // Fremder Haendler, Teilbetrag, vier Monate her: 15 + 0 + 0 = 15, unter 20.
        var refund = Buchung(10m, RefundDay, "Otto");
        var ausgabe = Buchung(-500m, RefundDay.AddDays(-120), "Ein ganz anderer Laden");

        Assert.Empty(Bewerte(refund, ausgabe));
    }

    [Fact]
    public void The_same_order_outweighs_everything_else()
    {
        var refund = Buchung(12m, RefundDay, "Amazon");
        var ausgabe = Buchung(-300m, RefundDay.AddDays(-150), "Ein ganz anderer Laden");

        var bestellung = "302-1234567-0000001";
        var purchases = new Dictionary<Guid, RefundPurchaseRef>
        {
            [refund.Id] = new(refund.Id, Guid.NewGuid(), bestellung, "Amazon"),
            [ausgabe.Id] = new(ausgabe.Id, Guid.NewGuid(), bestellung, "Amazon")
        };

        var treffer = Assert.Single(
            RefundCandidateScoring.Rank(refund, RefundDay, [ausgabe], purchases));

        // Ohne die Bestellung waeren es 15 Punkte und der Vorschlag fiele weg.
        Assert.Contains("Same purchase", treffer.Reasons);
        Assert.Equal(65m, treffer.MatchStrength);
    }

    [Fact]
    public void Candidates_come_back_with_the_best_match_first()
    {
        var refund = Buchung(49.90m, RefundDay, "MediaMarkt");
        var schwach = Buchung(-200m, RefundDay.AddDays(-25), "MediaMarkt");
        var stark = Buchung(-49.90m, RefundDay.AddDays(-2), "MediaMarkt");

        var treffer = Bewerte(refund, schwach, stark);

        Assert.Equal(stark.Id, treffer[0].TransactionId);
        Assert.True(treffer[0].MatchStrength > treffer[1].MatchStrength);
    }

    [Fact]
    public void A_hundred_is_the_ceiling()
    {
        var refund = Buchung(49.90m, RefundDay, "MediaMarkt", "REFUND für Bestellung");
        var ausgabe = Buchung(-49.90m, RefundDay.AddDays(-1), "MediaMarkt");
        var bestellung = "A-1";
        var purchases = new Dictionary<Guid, RefundPurchaseRef>
        {
            [refund.Id] = new(refund.Id, Guid.NewGuid(), bestellung, "MediaMarkt"),
            [ausgabe.Id] = new(ausgabe.Id, Guid.NewGuid(), bestellung, "MediaMarkt")
        };

        var treffer = Assert.Single(
            RefundCandidateScoring.Rank(refund, RefundDay, [ausgabe], purchases));

        // 45 + 30 + 15 + 5 + 50 = 145, gedeckelt auf 100.
        Assert.Equal(100m, treffer.MatchStrength);
    }
}
