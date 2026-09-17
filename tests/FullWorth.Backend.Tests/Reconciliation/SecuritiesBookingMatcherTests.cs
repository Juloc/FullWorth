using FullWorth.Backend.Modules.Reconciliation;

namespace FullWorth.Backend.Tests.Reconciliation;

public sealed class SecuritiesBookingMatcherTests
{
    // IE00BK5BQT80 ist eine echte, gültige ISIN (Vanguard FTSE All-World UCITS ETF) - dieselbe wie in
    // BrokerPdfTradeParserTests, damit die Prüfziffer keine Erfindung dieses Tests ist.
    private const string ValidIsin = "IE00BK5BQT80";
    private static readonly Guid SecurityId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherSecurityId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static readonly IReadOnlyDictionary<Guid, IReadOnlyList<(DateOnly Date, decimal Price)>> NoPrices =
        new Dictionary<Guid, IReadOnlyList<(DateOnly Date, decimal Price)>>();
    private static readonly IReadOnlyDictionary<Guid, (decimal Quantity, decimal? CostPrice)> NoHoldings =
        new Dictionary<Guid, (decimal Quantity, decimal? CostPrice)>();

    private static BookingCandidate Booking(
        string? description, decimal amount = -1000m, DateOnly? date = null,
        bool isIgnored = false, bool isTransfer = false) =>
        new(Guid.NewGuid(), date ?? new DateOnly(2026, 3, 1), amount, "EUR", description, isIgnored, isTransfer);

    [Fact]
    public void IsinSplitAcrossMt940MarkerBoundary_IsStillFound()
    {
        // ?21 fällt genau zwischen "IE00BK5B" und "QT80" - wie MT940 einen :86:-Text an einer
        // ~27-Zeichen-Grenze umbricht. Ungereinigt matcht keine ISIN-Regex diesen Text.
        var booking = Booking("?20Kauf Wertpapier ISIN IE00BK5B?21QT80 Kurswert 1000,00");
        var securities = new[] { new SecurityCandidate(SecurityId, ValidIsin, null, "Vanguard FTSE All-World") };

        var result = SecuritiesBookingMatcher.Match([booking], securities, NoPrices, NoHoldings);

        var match = Assert.Single(result.Matches);
        Assert.Equal(SecurityId, match.SecurityId);
        Assert.Equal(booking.TransactionId, match.TransactionId);
    }

    [Fact]
    public void IsinWithWrongCheckDigit_IsNotMatched()
    {
        // Letzte Ziffer von IE00BK5BQT80 (gültige Prüfziffer 0) auf 1 geändert - syntaktisch weiterhin
        // eine ISIN, aber die Mod-10-Prüfung muss sie ablehnen.
        const string invalidIsin = "IE00BK5BQT81";
        var booking = Booking($"Kauf ISIN {invalidIsin}");
        var securities = new[] { new SecurityCandidate(SecurityId, invalidIsin, null, "Irrelevant") };

        var result = SecuritiesBookingMatcher.Match([booking], securities, NoPrices, NoHoldings);

        Assert.Empty(result.Matches);
    }

    [Fact]
    public void LabeledWkn_IsMatched_BareSixCharactersAreNot()
    {
        var securities = new[] { new SecurityCandidate(SecurityId, null, "123456", "Irgendein Fonds") };
        var labeled = Booking("Sparplanausführung WKN: 123456 Kauf");
        var bare = Booking("Bestellnummer 123456 Referenz XY");

        var labeledResult = SecuritiesBookingMatcher.Match([labeled], securities, NoPrices, NoHoldings);
        var bareResult = SecuritiesBookingMatcher.Match([bare], securities, NoPrices, NoHoldings);

        var match = Assert.Single(labeledResult.Matches);
        Assert.Equal(SecurityId, match.SecurityId);
        Assert.Empty(bareResult.Matches);
    }

    [Fact]
    public void Credit_NeverProducesAMatch()
    {
        var booking = Booking($"Gutschrift ISIN {ValidIsin}", amount: 1000m);
        var securities = new[] { new SecurityCandidate(SecurityId, ValidIsin, null, "Fonds") };

        var result = SecuritiesBookingMatcher.Match([booking], securities, NoPrices, NoHoldings);

        Assert.Empty(result.Matches);
    }

    [Fact]
    public void IgnoredAndTransferBookings_AreSkippedEvenWithAValidIsin()
    {
        var ignored = Booking($"Kauf ISIN {ValidIsin}", isIgnored: true);
        var transfer = Booking($"Kauf ISIN {ValidIsin}", isTransfer: true);
        var securities = new[] { new SecurityCandidate(SecurityId, ValidIsin, null, "Fonds") };

        var result = SecuritiesBookingMatcher.Match([ignored, transfer], securities, NoPrices, NoHoldings);

        Assert.Empty(result.Matches);
    }

    [Fact]
    public void MissingPriceOnOrBeforeBookingDate_LeavesQuantityNullAndNeverConfident()
    {
        var booking = Booking($"Kauf ISIN {ValidIsin}", date: new DateOnly(2026, 3, 1));
        var securities = new[] { new SecurityCandidate(SecurityId, ValidIsin, null, "Fonds") };
        // Der einzige bekannte Kurs liegt NACH der Buchung - "auf oder vor dem Datum" liefert also nichts.
        var prices = new Dictionary<Guid, IReadOnlyList<(DateOnly Date, decimal Price)>>
        {
            [SecurityId] = [(new DateOnly(2026, 3, 15), 100m)],
        };
        var holdings = new Dictionary<Guid, (decimal Quantity, decimal? CostPrice)>
        {
            [SecurityId] = (10m, 100m),
        };

        var result = SecuritiesBookingMatcher.Match([booking], securities, prices, holdings);

        var match = Assert.Single(result.Matches);
        Assert.Null(match.Quantity);
        Assert.False(match.Confident);
        Assert.False(Assert.Single(result.Summaries).Confident);
    }

    [Fact]
    public void WithinOnePercentTolerance_IsConfidentAndRescalesToTheExactHolding()
    {
        // Q=3, P=100 -> Q*P=300. Zwei Buchungen mit je unterschiedlichem, zeitlich nächstem Kurs, damit
        // die geschätzte Summe (3.0151...) NICHT zufällig exakt 3 ist, aber innerhalb 1% von Q liegt.
        var date1 = new DateOnly(2026, 1, 10);
        var date2 = new DateOnly(2026, 2, 10);
        var booking1 = Booking("Kauf ISIN " + ValidIsin, amount: -150m, date: date1);
        var booking2 = Booking("Kauf ISIN " + ValidIsin, amount: -150m, date: date2);
        var securities = new[] { new SecurityCandidate(SecurityId, ValidIsin, null, "Fonds") };
        var prices = new Dictionary<Guid, IReadOnlyList<(DateOnly Date, decimal Price)>>
        {
            [SecurityId] = [(date1, 100m), (date2, 99m)],
        };
        var holdings = new Dictionary<Guid, (decimal Quantity, decimal? CostPrice)>
        {
            [SecurityId] = (3m, 100m),
        };

        var result = SecuritiesBookingMatcher.Match([booking1, booking2], securities, prices, holdings);

        Assert.Equal(2, result.Matches.Count);
        Assert.All(result.Matches, m => Assert.True(m.Confident));
        var summary = Assert.Single(result.Summaries);
        Assert.True(summary.Confident);
        // Die rohe Schätzung summiert NICHT exakt auf 3 (150/100 + 150/99 = 1.5 + 1.51515...), aber
        // die zurückgegebenen Stückzahlen müssen es - das ist der Punkt der Reskalierung.
        var sum = result.Matches.Sum(m => m.Quantity!.Value);
        Assert.Equal(3m, sum);
    }

    [Fact]
    public void TwoPercentOffTheHolding_IsNotConfident()
    {
        var date = new DateOnly(2026, 1, 10);
        // Kurswert 306 statt 300 - 2% über Q*P, außerhalb der 1%-Toleranz.
        var booking = Booking("Kauf ISIN " + ValidIsin, amount: -306m, date: date);
        var securities = new[] { new SecurityCandidate(SecurityId, ValidIsin, null, "Fonds") };
        var prices = new Dictionary<Guid, IReadOnlyList<(DateOnly Date, decimal Price)>>
        {
            [SecurityId] = [(date, 100m)],
        };
        var holdings = new Dictionary<Guid, (decimal Quantity, decimal? CostPrice)>
        {
            [SecurityId] = (3m, 100m),
        };

        var result = SecuritiesBookingMatcher.Match([booking], securities, prices, holdings);

        var match = Assert.Single(result.Matches);
        Assert.False(match.Confident);
        Assert.NotNull(match.Quantity);
        Assert.False(Assert.Single(result.Summaries).Confident);
    }

    [Fact]
    public void TextWithNeitherIsinNorLabeledWkn_ProducesNoMatchAtAll()
    {
        var booking = Booking("Miete September Wohnung");
        var securities = new[] { new SecurityCandidate(SecurityId, ValidIsin, "123456", "Fonds"),
                                  new SecurityCandidate(OtherSecurityId, null, null, "Anderer Fonds") };

        var result = SecuritiesBookingMatcher.Match([booking], securities, NoPrices, NoHoldings);

        Assert.Empty(result.Matches);
    }

    [Theory]
    [InlineData("IE00BK5BQT80")] // Vanguard FTSE All-World, echte gültige ISIN
    [InlineData("US0378331005")] // Apple Inc.
    [InlineData("DE0007164600")] // SAP SE
    [InlineData("GB0002374006")] // Diageo
    [InlineData("FR0000131104")] // BNP Paribas
    public void IsValidIsinCheckDigit_AcceptsRealIsins(string isin) =>
        Assert.True(SecuritiesBookingMatcher.IsValidIsinCheckDigit(isin));

    [Theory]
    [InlineData("IE00BK5BQT81")]
    [InlineData("US0378331000")]
    [InlineData("DE0007164601")]
    public void IsValidIsinCheckDigit_RejectsAWrongCheckDigit(string isin) =>
        Assert.False(SecuritiesBookingMatcher.IsValidIsinCheckDigit(isin));
}
