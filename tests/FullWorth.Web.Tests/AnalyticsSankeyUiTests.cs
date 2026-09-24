namespace FullWorth.Web.Tests;

/// <summary>
/// Das Flussdiagramm der freien Auswertung (#177).
///
/// Der Endpunkt war fertig gebaut und unerreichbar — und davor lief sein Rumpf nicht einmal, weil
/// <c>FinancialReconciliationMiddleware</c> die Route vor der Zuordnung abfing. Die Middleware ist
/// weg, und seit dieser Ansicht ruft ihn endlich jemand.
///
/// Geprüft wird hier vor allem, was die Antwort an Wahrheit mitliefert und was leicht verlorengeht,
/// weil es nur zwei Felder sind:
///
///   incomplete  Für mindestens einen Betrag fehlte ein Wechselkurs. Ein Bild, das die Summe dann
///               trotzdem glatt zeichnet, behauptet etwas, das niemand geprüft hat — und die
///               Geldregeln dieses Hauses verbieten genau das (nie 1:1, nie 0, sondern unvollständig).
///   reconciles  Der Server rechnet selbst nach, ob Zufluss und Abfluss aufgehen. Tun sie es nicht,
///               zeigt das Bild die Teile, nicht die Summe, und das gehört dazugesagt.
///
/// Beides sind zwei Zeilen im Modul. Sie verschwinden bei einem Umbau, ohne dass irgendetwas kaputt
/// aussieht — das Diagramm zeichnet ja weiter.
/// </summary>
public sealed class AnalyticsSankeyUiTests
{
    private static string Modul() => WebSources.Asset("pages", "analytics", "sankey.js");

    [Fact]
    public void A_missing_exchange_rate_is_shown_and_not_smoothed_over()
    {
        var quelle = Modul();

        Assert.Contains("data.incomplete", quelle, StringComparison.Ordinal);
        Assert.Contains("fehlt ein Wechselkurs", quelle, StringComparison.Ordinal);
    }

    [Fact]
    public void Flows_that_do_not_add_up_say_so()
    {
        var quelle = Modul();

        Assert.Contains("data.reconciles === false", quelle, StringComparison.Ordinal);
        Assert.Contains("gehen nicht auf", quelle, StringComparison.Ordinal);
    }

    /// <summary>
    /// Beträge gehen durch den gemeinsamen Formatierer. Der maskiert im Privatmodus — ein eigenes
    /// <c>toFixed</c> hier würde die Zahlen zeigen, während der Rest des Schirms sie verbirgt.
    /// </summary>
    [Fact]
    public void Amounts_go_through_the_shared_formatter_so_privacy_mode_still_holds()
    {
        var quelle = Modul();

        Assert.Contains("ctx.money(", quelle, StringComparison.Ordinal);
        Assert.DoesNotContain("toFixed", quelle, StringComparison.Ordinal);
        Assert.DoesNotContain("Intl.NumberFormat", quelle, StringComparison.Ordinal);
    }

    /// <summary>
    /// Kein Framework für ein Rechteck und eine Kurve: der Server liefert drei Spalten, das ist keine
    /// Layoutaufgabe. Ein nachgeladenes Modul wäre außerdem ein Bruch von Frontend-Regel 2.
    /// </summary>
    [Fact]
    public void The_diagram_is_drawn_without_a_library()
    {
        var quelle = Modul();

        Assert.DoesNotContain("import(", quelle, StringComparison.Ordinal);
        Assert.DoesNotContain("cdn", quelle, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<svg", quelle, StringComparison.Ordinal);
    }

    [Fact]
    public void The_builder_offers_the_flow_and_asks_the_endpoint_for_it()
    {
        var seite = WebSources.Asset("pages", "analytics", "page.js");

        Assert.Contains("analytics.builder.type_sankey", seite, StringComparison.Ordinal);
        Assert.Contains("api/analytics/sankey", seite, StringComparison.Ordinal);
        Assert.Contains("renderSankey(ctx, el, flow)", seite, StringComparison.Ordinal);

        foreach (var sprache in new[] { "de", "en" })
            Assert.Contains("type_sankey", WebSources.Asset("locales", $"{sprache}.json"), StringComparison.Ordinal);
    }
}
