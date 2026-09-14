using FullWorth.Backend.Modules.Purchases.Extraction;

namespace FullWorth.Backend.Tests.Import;

/// <summary>
/// Vier Importer lasen eine Tausendergruppe als Nachkommastellen. Hier steht, was dabei herauskam.
///
/// Der Wächter daneben verhindert, dass wieder jemand einen eigenen Zahlenleser baut. Dieser hier
/// prüft die Wirkung: dass ein Betrag über tausend auch als Betrag über tausend ankommt.
/// </summary>
public sealed class ThousandsInImportedAmountsTests
{
    /// <summary>
    /// Der Kassenbon hatte zwei Fehler übereinander, und der zweite versteckte den ersten.
    ///
    /// Die Regex war <c>(?&lt;whole&gt;\d{1,6})[.,](?&lt;fraction&gt;\d{2})</c>, davor ein
    /// <c>(?&lt;!\d)</c>. Bei "1.234,56" steht links vom "234" ein Punkt und keine Ziffer — der
    /// Blick zurück hielt also nicht, die Regex griff ab "234,56", und aus 1.234,56 € wurden
    /// 234,56 €. Kein Fehlschlag, keine Warnung, nur tausend Euro weniger.
    /// </summary>
    [Theory]
    [InlineData("Summe 1.234,56", 1234.56)]
    [InlineData("Summe 1234.56", 1234.56)]
    [InlineData("Summe 234,56", 234.56)]
    [InlineData("Summe 12.345,67", 12345.67)]
    public void A_receipt_total_above_a_thousand_keeps_its_thousands(string line, double expected)
    {
        var result = ReceiptTextParser.Parse(line);

        Assert.Equal((decimal)expected, result.Total);
    }

    /// <summary>
    /// Und die Gegenrichtung, damit der Test etwas bedeutet: zwei Nachkommastellen bleiben zwei
    /// Nachkommastellen. Ein Leser, der einfach jeden Punkt entfernte, wäre hier rot.
    /// </summary>
    [Theory]
    [InlineData("Summe 9,99", 9.99)]
    [InlineData("Summe 0,05", 0.05)]
    public void A_small_receipt_total_is_not_inflated(string line, double expected)
    {
        var result = ReceiptTextParser.Parse(line);

        Assert.Equal((decimal)expected, result.Total);
    }
}
