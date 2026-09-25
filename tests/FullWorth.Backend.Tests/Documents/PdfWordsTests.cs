using FullWorth.Backend.Documents;

namespace FullWorth.Backend.Tests.Documents;

/// <summary>
/// Die Zeilen eines PDF aus der Lage seiner Worte (#131, Abschnitt 11). Hier liegt der Grund, warum die
/// Kontoauszug-Leser Koordinaten lesen statt Text: im Textmodus stand beim Ikano-Auszug jeder Betrag eine
/// Zeile zu hoch.
/// </summary>
public sealed class PdfWordsTests
{
    private const string TwoPages = """
        <html><body><doc>
        <page width="595.0" height="842.0">
          <flow><block><line>
            <word xMin="40.0" yMin="100.0" xMax="80.0" yMax="110.0">12.09.26</word>
            <word xMin="90.0" yMin="100.0" xMax="200.0" yMax="110.0">M&amp;M</word>
          </line></block></flow>
          <flow><block><line>
            <word xMin="500.0" yMin="99.2" xMax="540.0" yMax="111.8">120,00</word>
            <word xMin="545.0" yMin="99.2" xMax="550.0" yMax="111.8">-</word>
          </line></block></flow>
          <flow><block><line>
            <word xMin="40.0" yMin="118.0" xMax="80.0" yMax="128.0">13.09.26</word>
          </line></block></flow>
        </page>
        <page width="595.0" height="842.0">
          <flow><block><line><word xMin="40.0" yMin="50.0" xMax="90.0" yMax="60.0">Seite2</word></line></block></flow>
        </page>
        </doc></body></html>
        """;

    [Fact]
    public void WordsAreReadPerPageWithTheirBoxesAndDecodedText()
    {
        var pages = PopplerPdfWordSource.ParseBboxLayout(TwoPages);

        Assert.Equal(2, pages.Count);
        Assert.Equal(5, pages[0].Count);
        Assert.Contains(pages[0], word => word.Text == "M&M");
        Assert.Equal("Seite2", Assert.Single(pages[1]).Text);
    }

    /// <summary>
    /// Der Betrag steht in einem eigenen Block, in einer groesseren Schrift und deshalb mit anderer Ober-
    /// und Unterkante als sein Buchungstext - aber auf dessen Hoehe. Er gehoert zu dieser Zeile, nicht zur
    /// naechsten und nicht in eine eigene.
    /// </summary>
    [Fact]
    public void AnAmountSetInItsOwnBlockJoinsTheLineItSitsOn()
    {
        var lines = PopplerPdfWordSource.AssembleLines(PopplerPdfWordSource.ParseBboxLayout(TwoPages)[0]);

        Assert.Equal(2, lines.Count);
        Assert.Equal("12.09.26 M&M 120,00 -", lines[0].Text);
        Assert.Equal("13.09.26", lines[1].Text);
    }

    [Fact]
    public void WordsOnALineAreOrderedLeftToRight()
    {
        var words = new[]
        {
            new PdfWord(300, 10, 340, 20, "rechts"),
            new PdfWord(40, 10, 80, 20, "links"),
            new PdfWord(150, 10, 200, 20, "mitte")
        };

        Assert.Equal("links mitte rechts", Assert.Single(PopplerPdfWordSource.AssembleLines(words)).Text);
    }
}
