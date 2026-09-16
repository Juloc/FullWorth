using FullWorth.FinTs;

namespace FullWorth.FinTs.Tests;

/// <summary>
/// Eine runde Menge ist eine Menge. In SWIFT steht sie als <c>10,</c> da.
///
/// Gemeldet als "ich hab nur 3 von 4 ETF", und die Messung liess nur noch eine Erklaerung zu:
///
/// <code>
/// Bestaende=4                                        der Parser las alle vier Bloecke
/// kept 3 of 4 holdings, WithoutQuantity=1            einer fiel am Mengenfilter
/// [19A+35B+70E+90B+93B+93C+94B+98A] x4               alle vier gleich aufgebaut
/// </code>
///
/// Gleich aufgebaut, gleiche Felder, und trotzdem faellt genau einer heraus. Der Unterschied lag
/// im INHALT: die drei, die ankamen, waren Bruchstuecke aus Sparplaenen - 0,43761 und 11,48992 und
/// 2,07808. Der vierte war glatt zehn Stueck.
///
/// Das Dezimalkomma ist in SWIFT vorgeschrieben, die Stellen dahinter sind es nicht. Zehn Stueck
/// stehen als <c>10,</c> da, und der Mengenausdruck verlangte <c>[.,]\d+</c> - mindestens eine
/// Ziffer hinter dem Komma. Auf <c>10,</c> passte er nirgends, die Menge wurde zu nichts, daraus
/// machte <c>quantity ?? 0m</c> eine Null, und <c>Where(Quantity > 0)</c> warf sie stumm weg.
///
/// Wer also runde Stueckzahlen haelt, verlor sie - und wer Sparplaene laufen hat, merkte nie etwas.
/// </summary>
public sealed class Mt535RoundAmountTests
{
    /// <summary>Zehn Stueck, wie die Bank sie schreibt.</summary>
    [Fact]
    public void AWholeNumberQuantityWrittenWithATrailingCommaIsRead()
    {
        var holdings = Mt535Parser.Parse(Statement("10,", "EUR100,", "EUR1000,"));

        Assert.Equal(10m, Assert.Single(holdings).Quantity);
    }

    /// <summary>Kurs und Marktwert schreibt dieselbe Bank auf dieselbe Weise.</summary>
    [Fact]
    public void RoundPricesAndMarketValuesAreReadTheSameWay()
    {
        var holding = Assert.Single(Mt535Parser.Parse(Statement("10,", "EUR100,", "EUR1000,")));

        Assert.Equal(100m, holding.Price);
        Assert.Equal(1000m, holding.MarketValue);
        Assert.Equal("EUR", holding.MarketValueCurrency);
    }

    /// <summary>Und die Bruchstuecke aus den Sparplaenen bleiben auf die Stelle genau.</summary>
    [Fact]
    public void FractionalQuantitiesKeepEveryDigit()
    {
        var holdings = Mt535Parser.Parse(Statement("0,43761", "EUR55,394", "EUR24,24"));

        Assert.Equal(0.43761m, Assert.Single(holdings).Quantity);
    }

    /// <summary>Eine gemeldete Null bleibt eine Null - sie wird nicht zu etwas anderem gemacht.</summary>
    [Fact]
    public void AReportedZeroStaysZero()
    {
        var holdings = Mt535Parser.Parse(Statement("0,", "EUR100,", "EUR0,"));

        Assert.Equal(0m, Assert.Single(holdings).Quantity);
    }

    private static string Statement(string quantity, string price, string value) => string.Join("\n",
    [
        ":16R:GENL",
        ":28E:1/ONLY",
        ":16S:GENL",
        ":16R:FIN",
        ":35B:ISIN LU0908500753",
        "/DE/LYX0Q0",
        "AIS-AMUN.STEUR600 U.ETF A",
        ":93B::AGGR//UNIT/" + quantity,
        ":90B::MRKT//ACTU/" + price,
        ":98A::PRIC//20260916",
        ":19A::HOLD//" + value,
        ":16S:FIN",
    ]);
}
