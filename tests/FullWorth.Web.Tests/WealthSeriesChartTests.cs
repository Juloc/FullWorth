using FullWorth.Web.Tests.Pwa;
namespace FullWorth.Web.Tests;

/// <summary>
/// Woraus das Vermoegen besteht, im Zeitverlauf (#178, #222): eine gestapelte Flaeche mit dem letzten
/// Stand jedes Monats.
///
/// Die Rechnung dahinter steht in <c>WealthAssetKindSeriesTests</c>. Hier geht es um die Entscheidungen,
/// die diese Karte ueberhaupt erst brauchbar machen und die alle leise verloren gehen koennten:
///
/// <list type="number">
///   <item>Eine Luecke bleibt eine Luecke. Ein Monat ohne Aufteilung ist NICHT null Euro; eine Flaeche
///         darueber hinweg oder ein Absturz auf die Grundlinie waere eine Behauptung ueber Vermoegen,
///         das niemand gemessen hat.</item>
///   <item>Die Legende ist ein Schalter, und die Achse folgt ihr. Eine Immobilie steht bei 300 000 und
///         das Edelmetall bei 4 700 - auf einer gemeinsamen Achse ist die kleinere Schicht ein Streifen
///         an der Kante. Erst das Abschalten macht sie lesbar.</item>
///   <item>Schulden und Eigenkapital stehen nicht im Stapel: eine Last ist keine Schicht, und das
///         Eigenkapital waere dieselbe Immobilie ein zweites Mal.</item>
/// </list>
/// </summary>
public sealed class WealthSeriesChartTests
{
    private static string Modul() => WebSources.Asset("pages", "networth", "history-series.js");

    [Fact]
    public void The_six_asset_layers_are_drawn_and_the_modules_are_precached()
    {
        var quelle = Modul();

        foreach (var key in new[]
        {
            "accounts", "investments", "realEstateAssets", "preciousMetalAssets",
            "pensionAssets", "otherAssets"
        })
            Assert.Contains($"key: '{key}'", quelle, StringComparison.Ordinal);

        PwaAssert.Ships("/pages/networth/history-series.js");
        PwaAssert.Ships("/pages/networth/chart-axes.js");
        Assert.Contains("bindSeriesChart", WebSources.Asset("pages", "networth", "page.js"), StringComparison.Ordinal);
    }

    [Fact]
    public void Debt_and_equity_are_not_part_of_the_stack()
    {
        var quelle = Modul();

        Assert.DoesNotContain("key: 'debt'", quelle, StringComparison.Ordinal);
        Assert.DoesNotContain("key: 'realEstateEquity'", quelle, StringComparison.Ordinal);
    }

    /// <summary>
    /// Die Flaeche bricht bei einem Monat ohne Wert ab und beginnt danach neu - nachweisbar daran, dass
    /// ein null-Monat den Lauf beendet (<c>close()</c>) statt uebersprungen zu werden.
    /// </summary>
    [Fact]
    public void A_missing_month_ends_the_area_instead_of_dropping_it_to_zero()
    {
        var quelle = Modul();

        Assert.Contains("tops.forEach((top, index) => { if (top == null) close(); else run.push(index); });", quelle, StringComparison.Ordinal);
        Assert.Contains("line.values[index] == null ? null :", quelle, StringComparison.Ordinal);
    }

    /// <summary>
    /// Die Karte erscheint nur, wenn ueberhaupt ein Tag die Aufteilung traegt. Vor #178 gab es sie
    /// nicht, und eine leere Karte waere ein Versprechen auf Daten, die nie kommen.
    /// </summary>
    [Fact]
    public void Without_a_single_split_day_there_is_no_card()
    {
        Assert.Contains("export function hasSeriesHistory", Modul(), StringComparison.Ordinal);
        Assert.Contains("if (!hasSeriesHistory(nw.history)) return", WebSources.Asset("pages", "networth", "page.js"), StringComparison.Ordinal);
    }

    /// <summary>
    /// Die Achse rechnet nach der Auswahl - sonst brauchte man den Schalter nicht. Und die Null ist die
    /// Grundlinie, damit eine Schicht zwischen 4 600 und 4 700 nicht nach einer Verdopplung aussieht.
    /// </summary>
    [Fact]
    public void The_axis_follows_the_selection_and_starts_at_zero()
    {
        var quelle = Modul();

        Assert.Contains("const shown = lines.filter(line => selected.has(line.series.key))", quelle, StringComparison.Ordinal);
        Assert.Contains("const max = Math.max(...totals)", quelle, StringComparison.Ordinal);
        Assert.Contains("const y = value => H - (value / max) * H", quelle, StringComparison.Ordinal);
    }

    /// <summary>
    /// Die letzte Schicht laesst sich nicht auch noch abschalten: ein leeres Diagramm ist kein Zustand,
    /// den jemand waehlt, und der Weg zurueck waere dann nur noch zu erraten.
    /// </summary>
    [Fact]
    public void The_last_layer_cannot_be_switched_off()
    {
        Assert.Contains(
            "if (selected.has(line.series.key) && selected.size === 1) return;",
            Modul(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Farben kommen aus den Tokens, nicht aus der Datei. Sechs fest eingetragene Farbwerte waeren
    /// sechs Stellen, die beim naechsten Thema nicht mitwandern.
    /// </summary>
    [Fact]
    public void The_colours_come_from_tokens()
    {
        var quelle = Modul();
        var css = WebSources.Asset("pages", "networth", "page.css");

        Assert.DoesNotContain("#", quelle.Replace("#178", string.Empty).Replace("#222", string.Empty), StringComparison.Ordinal);
        Assert.Contains(".nw-series-area[data-cat=\"1\"]{fill:var(--cat-1)}", css, StringComparison.Ordinal);
        Assert.Contains(".nw-series-area[data-cat=\"6\"]{fill:var(--cat-6)}", css, StringComparison.Ordinal);
    }
}
