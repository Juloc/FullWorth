using FullWorth.FinTs;

namespace FullWorth.FinTs.Tests;

/// <summary>
/// Der Einstandskurs steht in der Aufstellung - er musste nur gelesen werden.
///
/// "Wo ist meine Prozentzahl fuer Gewinn und Verlust?" Ein Gewinn braucht zwei Zahlen: den heutigen
/// Wert und den Einstand. HKWPD galt als Momentaufnahme, die nur die erste kennt, und die
/// Faehigkeitsliste der Bank bestaetigt, dass es nichts anderes gibt:
///
/// <code>HIWPDS = 6</code> ist ihr EINZIGES Wertpapiersegment - keine Umsaetze, keine Orderhistorie.
///
/// Der Einstand steckt trotzdem drin, im Fliesstext jedes Blocks:
///
/// <code>
/// :70E::HOLD//1STK
/// 257,128493+EUR
/// </code>
///
/// Dass es der Kurs JE STUECK ist und nicht der Einstandswert, sagen die Daten selbst. Ein Wert
/// waere ein Geldbetrag mit zwei Nachkommastellen; hier tragen drei von vier Positionen sechs - und
/// ausgerechnet die mit glatt zehn Stueck traegt zwei. Das ist eine Division durch die Stueckzahl.
/// </summary>
public sealed class Mt535CostPriceTests
{
    /// <summary>Genau die vier Positionen aus dem gemeldeten Depot.</summary>
    [Fact]
    public void EveryHoldingCarriesItsCostPricePerUnit()
    {
        var holdings = Mt535Parser.Parse(IngDepot());

        Assert.Equal(4, holdings.Count);
        Assert.Equal(257.128493m, holdings[0].CostPrice);
        Assert.Equal(288.17m, holdings[1].CostPrice);
        Assert.Equal(2141.717657m, holdings[2].CostPrice);
        Assert.Equal(2292.048429m, holdings[3].CostPrice);
        Assert.All(holdings, holding => Assert.Equal("EUR", holding.CostCurrency));
    }

    /// <summary>
    /// Die Stelle, an der die Nachkommastellen ihr Argument liefern: zehn glatte Stueck, und der
    /// Betrag hat zwei Nachkommastellen statt sechs.
    /// </summary>
    [Fact]
    public void TheRoundPositionIsTheOneThatProvesItIsAPricePerUnit()
    {
        var round = Mt535Parser.Parse(IngDepot())[1];

        Assert.Equal(10m, round.Quantity);
        Assert.Equal(288.17m, round.CostPrice);
        // Der Einstandswert entsteht erst aus beidem - der Parser bildet ihn NICHT.
        Assert.Equal(2881.70m, round.Quantity * round.CostPrice!.Value);
    }

    /// <summary>Ein negativer Einstand ist moeglich; das Vorzeichen steht hinter dem Betrag.</summary>
    [Fact]
    public void TheSignBelongsToTheAmountAndIsRead()
    {
        var holdings = Mt535Parser.Parse(Block("LU0908500753", "2,07808", "12,50-EUR"));

        Assert.Equal(-12.50m, Assert.Single(holdings).CostPrice);
    }

    /// <summary>Ohne Fliesstext bleibt der Einstand leer - kein Ersatzwert, keine Null.</summary>
    [Fact]
    public void AHoldingWithoutANarrativeHasNoCostPriceAtAll()
    {
        var holdings = Mt535Parser.Parse(Block("LU0908500753", "2,07808", null));

        Assert.Null(Assert.Single(holdings).CostPrice);
    }

    /// <summary>Und ein Fliesstext, der keinen Betrag traegt, erfindet auch keinen.</summary>
    [Fact]
    public void ANarrativeWithoutAnAmountYieldsNothing()
    {
        var holdings = Mt535Parser.Parse(Block("LU0908500753", "2,07808", "keine Angabe"));

        Assert.Null(Assert.Single(holdings).CostPrice);
    }

    private static string Block(string isin, string quantity, string? cost) => string.Join("\n",
    [
        ":16R:GENL",
        ":28E:1/ONLY",
        ":16S:GENL",
        ":16R:FIN",
        ":35B:ISIN " + isin,
        "/DE/LYX0Q0",
        "AIS-AMUN.STEUR600 U.ETF A",
        ":90B::MRKT//ACTU/EUR316,3",
        ":93B::AGGR//UNIT/" + quantity,
        ":19A::HOLD//EUR657,3",
        .. cost is null ? (string[])[] : [":70E::HOLD//1STK", cost],
        ":16S:FIN",
    ]);

    /// <summary>
    /// Der Aufbau, den die ING wirklich schickt - samt SUBBAL, ADDINFO und dem Kursdatum in 98C
    /// statt 98A. Alles davon war vorher nur Vermutung.
    /// </summary>
    private static string IngDepot() => string.Join("\n",
    [
        ":16R:GENL",
        ":28E:1/ONLY",
        ":20C::SEME//NONREF",
        ":23G:NEWM",
        ":98C::STAT//20260917091555",
        ":22F::STTY//CUST",
        ":17B::ACTI//Y",
        ":16S:GENL",
        .. Position("IE00B4L5YC18", "/DE/A0RPWJ", "ISHSIII-MSCI EM USD(ACC)", "55,58", "0,43761", "24,32", "257,128493"),
        .. Position("LU1437017350", "/DE/A2ATYY", "AIS-A.CO.MSCI E.M.UCETFDR", "101,14", "10,", "1011,4", "288,17"),
        .. Position("IE000BI8OT95", "/DE/ETF146", "AMUNDI CORE MSCI WLD UE A", "159,655", "11,49892", "1835,86", "2141,717657"),
        .. Position("LU0908500753", "/DE/LYX0Q0", "AIS-AMUN.STEUR600 U.ETF A", "316,3", "2,07808", "657,3", "2292,048429"),
        ":16R:ADDINFO",
        ":19A::HOLP//EUR3528,88",
        ":16S:ADDINFO",
        "-",
    ]);

    private static string[] Position(
        string isin, string wkn, string name, string price, string quantity, string value, string cost) =>
    [
        ":16R:FIN",
        ":35B:ISIN " + isin,
        wkn,
        name,
        ":90B::MRKT//ACTU/EUR" + price,
        ":94B::PRIC//LMAR/XGAT",
        ":98C::PRIC//20260917091552",
        ":93B::AGGR//UNIT/" + quantity,
        ":16R:SUBBAL",
        ":93C::TAVI//UNIT/AVAI/" + quantity,
        ":16S:SUBBAL",
        ":19A::HOLD//EUR" + value,
        ":70E::HOLD//1STK",
        cost + "+EUR",
        ":16S:FIN",
    ];
}
