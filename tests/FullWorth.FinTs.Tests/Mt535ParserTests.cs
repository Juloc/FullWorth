using System.Text;
using FullWorth.FinTs;

namespace FullWorth.FinTs.Tests;

/// <summary>
/// Der Depotbestand wird gelesen, nicht geraten (#130 §5).
///
/// Vorher galt die erste Zahl des Segments als Stueckzahl, die zweite als Kurs, die dritte als Wert,
/// und was uebrig blieb, hiess "Wertpapier". Das stimmt, solange die Bank genau diese drei in genau
/// dieser Reihenfolge schickt - und sonst nie. Ein Depot mit falscher Stueckzahl sieht aus wie ein
/// Depot; auffallen wuerde es erst am Vermoegen.
/// </summary>
public sealed class Mt535ParserTests
{
    private const string Statement = """
:16R:GENL
:28E:1/ONLY
:13A::STAT//001
:20C::SEME//STATEMENT0001
:23G:NEWM
:98A::STAT//20260915
:16S:GENL
:16R:SUBSAFE
:16R:FIN
:35B:ISIN DE0007164600
SAP SE
:93B::AGGR//UNIT/12,5
:16R:PRIC
:90B::MRKT//ACTU/EUR120,50
:16S:PRIC
:98A::PRIC//20260915
:19A::HOLD//EUR1506,25
:94B::SAFE//EXCH/XETR
:16S:FIN
:16R:FIN
:35B:ISIN US0378331005
APPLE INC
:93B::AGGR//UNIT/3
:16R:PRIC
:90B::MRKT//ACTU/USD214,80
:16S:PRIC
:98A::PRIC//20260912
:19A::HOLD//USD644,40
:16S:FIN
:16S:SUBSAFE
""";

    [Fact]
    public void EveryHoldingIsReadFromItsOwnFieldsInsteadOfGuessedFromTheOrder()
    {
        var holdings = Mt535Parser.Parse(Statement);

        Assert.Equal(2, holdings.Count);

        var sap = holdings[0];
        Assert.Equal("DE0007164600", sap.Isin);
        Assert.Equal("SAP SE", sap.Name);
        Assert.Equal(12.5m, sap.Quantity);
        Assert.Equal(120.50m, sap.Price);
        Assert.Equal("EUR", sap.PriceCurrency);
        Assert.Equal(new DateOnly(2026, 9, 15), sap.PriceDate);
        Assert.Equal(1506.25m, sap.MarketValue);
        Assert.Equal("XETR", sap.Exchange);

        var apple = holdings[1];
        Assert.Equal("US0378331005", apple.Isin);
        Assert.Equal("APPLE INC", apple.Name);
        Assert.Equal(3m, apple.Quantity);
        Assert.Equal(214.80m, apple.Price);
        Assert.Equal("USD", apple.MarketValueCurrency);
        // Kein Handelsplatz in der Aufstellung heisst: keiner. Nicht der des vorigen Bestands.
        Assert.Null(apple.Exchange);
    }

    [Fact]
    public void AMissingFieldStaysEmptyInsteadOfBeingFilledWithTheNextNumber()
    {
        const string sparse = """
:16R:SUBSAFE
:16R:FIN
:35B:ISIN DE0008404005
ALLIANZ SE
:93B::AGGR//UNIT/7
:16S:FIN
:16S:SUBSAFE
""";

        var holding = Assert.Single(Mt535Parser.Parse(sparse));

        Assert.Equal(7m, holding.Quantity);
        // Genau hier hat die alte Auslegung die naechste Zahl zum Kurs gemacht.
        Assert.Null(holding.Price);
        Assert.Null(holding.MarketValue);
        Assert.Null(holding.PriceDate);
        Assert.Equal("ALLIANZ SE", holding.Name);
    }

    [Fact]
    public void AMultiLineNameStaysOnePiece()
    {
        const string wrapped = """
:16R:SUBSAFE
:16R:FIN
:35B:ISIN LU0908500753
LYXOR CORE STOXX EUROPE
600 ACC
:93B::AGGR//UNIT/40
:16S:FIN
:16S:SUBSAFE
""";

        var holding = Assert.Single(Mt535Parser.Parse(wrapped));

        Assert.Equal("LYXOR CORE STOXX EUROPE 600 ACC", holding.Name);
    }

    [Fact]
    public void TheResponseParserUsesTheStatementWhenHiwpdCarriesOne()
    {
        // So kommt es an: HIWPD traegt die Aufstellung in einem Binaerfeld.
        var inner = FinTsWire.Serialize([
            new FinTsSegment([
                FinTsGroup.Of(FinTsValue.T("HIWPD"), FinTsValue.T("3"), FinTsValue.T("6")),
                FinTsGroup.Of(FinTsValue.B(Encoding.Latin1.GetBytes(Statement)))
            ])
        ]);
        var response = FinTsResponseParser.Parse(inner);

        var holdings = FinTsResponseParser.Holdings(response);

        Assert.Equal(2, holdings.Count);
        Assert.Equal("SAP SE", holdings[0].Name);
        Assert.Equal(12.5m, holdings[0].Quantity);
        // Und nichts heisst mehr "Wertpapier", nur weil kein Name erkannt wurde.
        Assert.DoesNotContain(holdings, holding => holding.Name == "Wertpapier");
    }
}
