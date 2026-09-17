using FullWorth.FinTs;

namespace FullWorth.FinTs.Tests;

/// <summary>
/// Der Fliesstext eines Bestands, lesbar gemacht ohne ihn preiszugeben.
///
/// Ein Bestand sagt, was er heute wert ist - nicht, was er gekostet hat. Ohne Einstand gibt es
/// keinen Gewinn und keine Prozentzahl, und genau die zeigen andere Apps. :70E: ist das Feld, in
/// dem die deutsche Auspraegung von MT535 Zusatzangaben unterbringt; die ING schickt es in jedem
/// Block mit, und gelesen hat es hier noch nie jemand.
///
/// Um den Aufbau zu erfahren, braucht es die BESCHRIFTUNGEN, nicht die Zahlen. Also bleiben die
/// Woerter und jede Ziffer wird '#'. Ein Depotbestand gehoert seinem Eigentuemer.
/// </summary>
public sealed class Mt535NarrativeShapeTests
{
    /// <summary>Die Beschriftung bleibt, der Betrag verschwindet.</summary>
    [Fact]
    public void TheLabelSurvivesAndEveryDigitIsMasked()
    {
        var shape = Mt535NarrativeShapeTests.Narrative(":70E::HOLD//Einstandskurs EUR 123,45");

        Assert.Equal(":HOLD//Einstandskurs EUR ###,##", Assert.Single(shape));
    }

    /// <summary>Auch mehrzeilig - der Umbruch wird sichtbar, statt die Zeile zu zerreissen.</summary>
    [Fact]
    public void AMultiLineNarrativeKeepsItsShapeOnOneLine()
    {
        var shape = Narrative(":70E::HOLD//Einstandswert", "EUR 1.234,56");

        Assert.Equal(":HOLD//Einstandswert·EUR #.###,##", Assert.Single(shape));
    }

    /// <summary>Kein Fliesstext ist eine Antwort - und muss als solche dastehen, nicht als Luecke.</summary>
    [Fact]
    public void ABlockWithoutANarrativeSaysSo()
    {
        var shape = Mt535Parser.NarrativeShape(string.Join("\n",
        [
            ":16R:FIN",
            ":35B:ISIN LU0908500753",
            ":93B::AGGR//UNIT/10,",
            ":16S:FIN",
        ]));

        Assert.Equal("-", Assert.Single(shape));
    }

    private static IReadOnlyList<string> Narrative(params string[] narrativeLines) =>
        Mt535Parser.NarrativeShape(string.Join("\n",
        [
            ":16R:FIN",
            ":35B:ISIN LU0908500753",
            ":93B::AGGR//UNIT/10,",
            .. narrativeLines,
            ":16S:FIN",
        ]));
}
