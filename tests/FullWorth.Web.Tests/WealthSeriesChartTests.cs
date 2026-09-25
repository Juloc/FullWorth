using FullWorth.Web.Tests.Pwa;
namespace FullWorth.Web.Tests;

/// <summary>
/// Die Reihen des Vermoegensverlaufs auf dem Schirm (#178).
///
/// Die Rechnung dahinter steht in <c>WealthAssetKindSeriesTests</c>. Hier geht es um die zwei
/// Entscheidungen, die diese Karte ueberhaupt erst brauchbar machen und die beide leise verloren
/// gehen koennten:
///
/// <list type="number">
///   <item>Eine Luecke bleibt eine Luecke. Ein Tag ohne Aufteilung ist NICHT null Euro; ein
///         durchgezogener Strich darueber hinweg oder ein Absturz auf die Grundlinie waere eine
///         Behauptung ueber Vermoegen, das niemand gemessen hat.</item>
///   <item>Die Legende ist ein Schalter, und die Achse folgt ihr. Eine Immobilie steht bei 300 000
///         und das Edelmetall bei 4 700 - auf einer gemeinsamen Achse ist die kleinere Reihe ein
///         Strich auf der Grundlinie. Erst das Abschalten macht sie lesbar.</item>
/// </list>
/// </summary>
public sealed class WealthSeriesChartTests
{
    private static string Modul() => WebSources.Asset("pages", "networth", "history-series.js");

    [Fact]
    public void All_eight_series_are_drawn_and_the_module_is_precached()
    {
        var quelle = Modul();

        foreach (var key in new[]
        {
            "accounts", "investments", "realEstateAssets", "preciousMetalAssets",
            "pensionAssets", "otherAssets", "realEstateEquity", "debt"
        })
            Assert.Contains($"key: '{key}'", quelle, StringComparison.Ordinal);

        PwaAssert.Ships("/pages/networth/history-series.js", WebSources.Asset("sw.js"));
        Assert.Contains("bindSeriesChart", WebSources.Asset("pages", "networth", "page.js"), StringComparison.Ordinal);
    }

    /// <summary>
    /// Der Pfad bricht bei einem fehlenden Wert ab und beginnt danach neu - nachweisbar daran, dass
    /// ein null-Tag den Stift hebt (<c>open = false</c>) statt uebersprungen zu werden.
    /// </summary>
    [Fact]
    public void A_missing_day_breaks_the_line_instead_of_dropping_it_to_zero()
    {
        var quelle = Modul();

        Assert.Contains("if (value == null) { open = false; return; }", quelle, StringComparison.Ordinal);
        // Und nirgends eine Null als Ersatz fuer einen fehlenden Wert.
        Assert.DoesNotContain("?? 0", quelle, StringComparison.Ordinal);
        Assert.DoesNotContain("|| 0)", quelle.Replace("Number(a) || 0", string.Empty).Replace("Number(b) || 0", string.Empty), StringComparison.Ordinal);
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
    /// Die Achse rechnet nach der Auswahl - sonst brauchte man den Schalter nicht. Und die Null
    /// bleibt im Bild, damit eine Reihe zwischen 4 600 und 4 700 nicht nach einer Verdopplung
    /// aussieht.
    /// </summary>
    [Fact]
    public void The_axis_follows_the_selection_and_keeps_zero_in_view()
    {
        var quelle = Modul();

        Assert.Contains("const shown = lines.filter(line => selected.has(line.series.key))", quelle, StringComparison.Ordinal);
        Assert.Contains("const all = shown.flatMap(line => line.values)", quelle, StringComparison.Ordinal);
        Assert.Contains("Math.min(0, ...all)", quelle, StringComparison.Ordinal);
    }

    /// <summary>
    /// Die letzte Reihe laesst sich nicht auch noch abschalten: ein leeres Diagramm ist kein Zustand,
    /// den jemand waehlt, und der Weg zurueck waere dann nur noch zu erraten.
    /// </summary>
    [Fact]
    public void The_last_series_cannot_be_switched_off()
    {
        Assert.Contains(
            "if (selected.has(line.series.key) && selected.size === 1) return;",
            Modul(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Schulden werden positiv gezeigt. "minus 200 000" neben "plus 300 000" liest sich als
    /// Vermoegen, und die Reihe heisst ohnehin so, wie sie ist.
    /// </summary>
    [Fact]
    public void Debt_is_one_positive_series_made_of_both_kinds()
    {
        Assert.Contains("sum(point.loans, point.otherLiabilities)", Modul(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Farben kommen aus den Tokens, nicht aus der Datei. Acht fest eingetragene Farbwerte waeren
    /// acht Stellen, die beim naechsten Thema nicht mitwandern.
    /// </summary>
    [Fact]
    public void The_colours_come_from_tokens()
    {
        var quelle = Modul();
        var css = WebSources.Asset("pages", "networth", "page.css");

        Assert.DoesNotContain("#", quelle.Replace("#178", string.Empty), StringComparison.Ordinal);
        Assert.Contains(".nw-series-line[data-cat=\"1\"]{stroke:var(--cat-1)}", css, StringComparison.Ordinal);
        Assert.Contains(".nw-series-line[data-cat=\"8\"]{stroke:var(--cat-8)}", css, StringComparison.Ordinal);
    }
}
