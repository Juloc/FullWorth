using FullWorth.FinTs;

namespace FullWorth.FinTs.Tests;

/// <summary>
/// Ein Bestand ohne lesbare Menge ist ein Bestand - und muss als solcher sichtbar bleiben.
///
/// Gemeldet als "ich hab nur 3 von 4 ETF". Die Datenbank zeigte genau ein Depot mit genau drei
/// Positionen, alle mit plausibler Menge und plausiblem Kurs. Der vierte ging also nicht bei der
/// Aufteilung verloren, sondern auf dem Weg vom Papier in die Position.
///
/// Dafuer gibt es eine durchgehende Kette, und sie schwieg an beiden Enden:
///
/// <list type="number">
///   <item>Hier wird eine fehlende Menge zu <c>0</c> (<c>quantity ?? 0m</c>) - nicht zu "unbekannt".</item>
///   <item>Die Uebernahme filtert <c>Where(Quantity > 0)</c> und ueberspringt ihn ohne ein Wort.</item>
/// </list>
///
/// Der Parser darf den Bestand deshalb NICHT verwerfen: nur wenn er in der Liste steht, kann die
/// Zahl dahinter ("4 geliefert, 3 uebernommen") den Verlust ueberhaupt benennen. Verschluckte ihn
/// schon der Parser, waere der vierte ETF nirgends zu zaehlen.
/// </summary>
public sealed class Mt535MissingQuantityTests
{
    /// <summary>Vier Bloecke, einer ohne Mengenfeld - es kommen vier Bestaende zurueck, nicht drei.</summary>
    [Fact]
    public void AHoldingWhoseQuantityFieldIsMissingIsStillReturned()
    {
        var holdings = Mt535Parser.Parse(FourBlocksOneWithoutQuantity());

        Assert.Equal(4, holdings.Count);
        Assert.Equal("LU0908500753", holdings[3].Isin);
    }

    /// <summary>Und seine Menge ist 0 - der Wert, ueber den ihn die Uebernahme stumm verliert.</summary>
    [Fact]
    public void ItsQuantityIsZeroWhichIsExactlyWhatTheImportDropsSilently()
    {
        var holdings = Mt535Parser.Parse(FourBlocksOneWithoutQuantity());

        Assert.Equal(0m, holdings[3].Quantity);
        Assert.Equal(3, holdings.Count(holding => holding.Quantity > 0));
    }

    /// <summary>
    /// Die Feldkennungen benennen den Unterschied, ohne einen Namen oder Betrag zu verraten: drei
    /// Bloecke tragen 93B, der vierte nicht. Das ist die Zeile, die im Log die Frage entscheidet.
    /// </summary>
    [Fact]
    public void TheFieldShapeNamesWhichBlockIsMissingTheQuantityTag()
    {
        var shape = Mt535Parser.FieldShape(FourBlocksOneWithoutQuantity());

        Assert.Equal(4, shape.Count);
        Assert.All(shape.Take(3), tags => Assert.Contains("93B", tags));
        Assert.DoesNotContain("93B", shape[3]);
    }

    /// <summary>Der Aufbau der ING: GENL, dann FIN-Bloecke, kein SUBSAFE.</summary>
    private static string FourBlocksOneWithoutQuantity() => string.Join("\n",
    [
        ":16R:GENL",
        ":28E:1/ONLY",
        ":13A::STAT//001",
        ":20C::SEME//DEPOT",
        ":16S:GENL",
        Block("IE00B4L5YC18", "/DE/A0RPWJ", "ISHSIII-MSCI EM USD(ACC)", "0,43761", "55,394", "24,24"),
        Block("IE000BI8OT95", "/DE/ETF146", "AMUNDI CORE MSCI WLD UE A", "11,48992", "158,215", "1817,74"),
        Block("LU0378449770", "/DE/ETF127", "COMSTAGE-NASDAQ-100 U.ETF", "2,07808", "313,75", "651,99"),
        // Der vierte: alles da ausser der Menge.
        ":16R:FIN",
        ":35B:ISIN LU0908500753",
        "/DE/LYX0Q0",
        "AIS-AMUN.STEUR600 U.ETF A",
        ":90B::MRKT//ACTU/EUR313,75",
        ":98A::PRIC//20260916",
        ":19A::HOLD//EUR651,99",
        ":16S:FIN",
    ]);

    private static string Block(string isin, string wkn, string name, string quantity, string price, string value) =>
        string.Join("\n",
        [
            ":16R:FIN",
            ":35B:ISIN " + isin,
            wkn,
            name,
            ":93B::AGGR//UNIT/" + quantity,
            ":90B::MRKT//ACTU/EUR" + price,
            ":98A::PRIC//20260916",
            ":19A::HOLD//EUR" + value,
            ":16S:FIN",
        ]);
}
