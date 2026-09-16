using FullWorth.FinTs;

namespace FullWorth.FinTs.Tests;

/// <summary>
/// "Depot 0 EUR, obwohl es über 3000 sein sollten - vier ETF-Positionen."
///
/// Null Bestaende ist kein Nichts, sondern eine Frage, und es gibt drei sehr verschiedene Antworten
/// darauf. Von aussen sahen alle drei gleich aus: ein leeres Depot.
///
/// <list type="number">
/// <item>Die Bank hat kein HIWPD geliefert.</item>
/// <item>HIWPD kam, aber es steckt kein erkennbares MT535 darin.</item>
/// <item>MT535 steckt darin, aber es wurde nichts daraus gelesen.</item>
/// </list>
///
/// Gesendete Nachrichten haben dafuer laengst eine Form (<c>SentShape</c>), und sie hat drei
/// Protokollfehler hintereinander erklaert. Fuer die Antwort gab es nichts dergleichen.
///
/// Was NICHT hineingehoert, ist ebenso wichtig: keine Wertpapiernamen, keine Kennungen, keine
/// Betraege. Ein Depotauszug ist der Inhalt eines Vermoegens; seine FORM verraet davon nichts.
/// </summary>
public sealed class HoldingsShapeTests
{
    [Fact]
    public void ABankThatSentNoPortfolioSegmentSaysSo()
    {
        var shape = FinTsResponseParser.HoldingsShape(Response(Acknowledgement()), parsed: 0);

        Assert.Contains("HIWPD=0", shape);
        Assert.Contains("Bestaende=0", shape);
    }

    [Fact]
    public void APortfolioSegmentWithoutAStatementIsNamedAsSuch()
    {
        var shape = FinTsResponseParser.HoldingsShape(
            Response(Acknowledgement(), Portfolio("irgendetwas, das kein MT535 ist")), parsed: 0);

        Assert.Contains("HIWPD=1", shape);
        Assert.Contains("kein-MT535", shape);
    }

    /// <summary>Das MT535 ist erkannt - dann liegt es am Lesen, nicht am Empfangen.</summary>
    [Fact]
    public void ARecognisedStatementIsReportedWithItsLength()
    {
        var statement = Statement();
        var shape = FinTsResponseParser.HoldingsShape(Response(Acknowledgement(), Portfolio(statement)), parsed: 0);

        Assert.Contains("MT535", shape);
        Assert.DoesNotContain("kein-MT535", shape);
        Assert.Contains($"{statement.Length}-Zeichen", shape);
    }

    /// <summary>Und wenn alles stimmt, steht die Zahl da, die man erwartet.</summary>
    [Fact]
    public void AStatementThatParsedIsReportedWithItsCount()
    {
        var response = Response(Acknowledgement(), Portfolio(Statement()));

        Assert.Equal(1, FinTsResponseParser.Holdings(response).Count);
        Assert.Contains("Bestaende=1", FinTsResponseParser.HoldingsShape(response, parsed: 1));
    }

    /// <summary>Die Form beschreibt die Antwort - sie gibt sie nicht wieder.</summary>
    [Fact]
    public void TheShapeNeverCarriesTheHoldingsThemselves()
    {
        var shape = FinTsResponseParser.HoldingsShape(Response(Acknowledgement(), Portfolio(Statement())), parsed: 1);

        Assert.DoesNotContain("DE0007164600", shape);
        Assert.DoesNotContain("SAP", shape);
        Assert.DoesNotContain("1506", shape);
        Assert.DoesNotContain("120", shape);
    }

    private static string Statement() => string.Join("\r\n",
        ":16R:GENL",
        ":16S:GENL",
        ":16R:SUBSAFE",
        ":16R:FIN",
        ":35B:ISIN DE0007164600",
        "SAP SE",
        ":93B::AGGR//UNIT/12,5",
        ":90B::MRKT//ACTU/EUR120,5",
        ":19A::HOLD//EUR1506,25",
        ":16S:FIN",
        ":16S:SUBSAFE");

    private static FinTsSegment Acknowledgement() => new([
        FinTsGroup.Of(FinTsValue.T("HIRMG"), FinTsValue.T("2"), FinTsValue.T("2")),
        FinTsGroup.Of(FinTsValue.T("0010"), FinTsValue.T("-"), FinTsValue.T("Nachricht entgegengenommen."))
    ]);

    // Wie die Bank es schickt: die Aufstellung steht als BINAERfeld im Segment, nicht als Text.
    private static FinTsSegment Portfolio(string statement) => new([
        FinTsGroup.Of(FinTsValue.T("HIWPD"), FinTsValue.T("3"), FinTsValue.T("6"), FinTsValue.T("3")),
        FinTsGroup.Of(FinTsValue.B(System.Text.Encoding.Latin1.GetBytes(statement)))
    ]);

    private static FinTsResponse Response(params FinTsSegment[] segments)
        => FinTsResponseParser.Parse(FinTsWire.Serialize(segments));
}
