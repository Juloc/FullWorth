using FullWorth.FinTs;

namespace FullWorth.FinTs.Tests;

/// <summary>
/// Der Bestand ist der FIN-Block - ein umschliessendes SUBSAFE ist keine Bedingung.
///
/// Gemeldet als "Depot 0 EUR, obwohl es über 3000 sein sollten - vier ETF-Positionen". Die
/// Antwortform zeigte, dass die Bank sehr wohl geliefert hatte:
///
/// <code>HIWPD=1, v6:MT535:1388-Zeichen, Bestaende=0</code>
///
/// 1388 Zeichen MT535, erkannt, und trotzdem nichts gelesen. Der Parser verlangte ein
/// <c>:16R:SUBSAFE</c> und ignorierte ohne dieses JEDE Zeile. Die ING schickt keines - ihre
/// FIN-Bloecke stehen direkt hinter GENL. SUBSAFE ist eine Klammer, kein Inhalt.
/// </summary>
public sealed class Mt535WithoutSubsafeTests
{
    /// <summary>Genau der Aufbau, den die ING schickt: GENL, dann FIN-Bloecke, kein SUBSAFE.</summary>
    [Fact]
    public void HoldingsAreReadWithoutASubsafeWrapper()
    {
        var holdings = Mt535Parser.Parse(IngStyle());

        Assert.Equal(2, holdings.Count);
        Assert.Equal("LU0378449770", holdings[0].Isin);
        Assert.Equal(10.5m, holdings[0].Quantity);
        Assert.Equal(120.5m, holdings[0].Price);
        Assert.Equal("IE00B4L5Y983", holdings[1].Isin);
    }

    /// <summary>Und der Aufbau MIT SUBSAFE liest sich weiterhin genauso - beides ist gueltig.</summary>
    [Fact]
    public void AStatementWithASubsafeWrapperStillReadsTheSame()
    {
        var withWrapper = Mt535Parser.Parse(Wrapped(IngStyle()));
        var without = Mt535Parser.Parse(IngStyle());

        Assert.Equal(without.Count, withWrapper.Count);
        Assert.Equal(without[0].Isin, withWrapper[0].Isin);
        Assert.Equal(without[0].Quantity, withWrapper[0].Quantity);
    }

    /// <summary>
    /// Was in GENL steht, ist kein Bestand. Dort steht unter anderem die Depotnummer (:97A::SAFE//),
    /// und ein Parser, der jeden Block mitnimmt, machte daraus eine Position ohne Wertpapier.
    /// </summary>
    [Fact]
    public void TheGeneralBlockIsNeverMistakenForAHolding()
    {
        var holdings = Mt535Parser.Parse(IngStyle());

        Assert.DoesNotContain(holdings, holding => holding.Name.Contains("535", StringComparison.Ordinal));
        Assert.All(holdings, holding => Assert.False(string.IsNullOrWhiteSpace(holding.Isin) && string.IsNullOrWhiteSpace(holding.Name)));
    }

    /// <summary>Die Felder der Unterbloecke gehoeren zum Bestand, nicht zu einem eigenen.</summary>
    [Fact]
    public void FieldsInsideNestedBlocksBelongToTheSurroundingHolding()
    {
        var holding = Assert.Single(Mt535Parser.Parse(string.Join("\r\n",
            ":16R:FIN",
            ":35B:ISIN LU0378449770",
            "ETF EINS",
            ":16R:FINSUB",
            ":93B::AGGR//UNIT/10,5",
            ":16S:FINSUB",
            ":16R:SUBBAL",
            ":19A::HOLD//EUR1265,25",
            ":16S:SUBBAL",
            ":16S:FIN")));

        Assert.Equal(10.5m, holding.Quantity);
        Assert.Equal(1265.25m, holding.MarketValue);
    }

    private static string IngStyle() => string.Join("\r\n",
        ":16R:GENL",
        ":28E:1/ONLY",
        ":13A::STAT//535",
        ":98A::STAT//20260916",
        ":97A::SAFE//1234567890",
        ":17B::ACTI//Y",
        ":16S:GENL",
        ":16R:FIN",
        ":35B:ISIN LU0378449770",
        "ETF EINS",
        ":93B::AGGR//UNIT/10,5",
        ":90B::MRKT//ACTU/EUR120,5",
        ":98A::PRIC//20260915",
        ":19A::HOLD//EUR1265,25",
        ":16S:FIN",
        ":16R:FIN",
        ":35B:ISIN IE00B4L5Y983",
        "ETF ZWEI",
        ":93B::AGGR//UNIT/4,",
        ":90B::MRKT//ACTU/EUR98,75",
        ":19A::HOLD//EUR395,",
        ":16S:FIN");

    private static string Wrapped(string body) => string.Join("\r\n", ":16R:SUBSAFE", body, ":16S:SUBSAFE");
}
