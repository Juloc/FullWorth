namespace FullWorth.Web.Tests;

/// <summary>
/// Die Zuordnungsprobe der Haendlerseite (#177).
///
/// <c>GET /api/merchants/resolve</c> stand fertig im Baum und hatte keinen Aufrufer - dabei fehlte
/// genau dieses Werkzeug beim Pflegen von Aliassen: einen Alias anzulegen ist leicht, nachzusehen,
/// ob er wirklich greift, ging nur ueber "warten, bis die naechste Buchung kommt".
///
/// Der Kern dieses Tests ist eine Grenze, keine Beschriftung: gefragt wird der SERVER. Die Regel
/// "der laengste passende Alias gewinnt" hier in JavaScript nachzubauen hiesse, eine zweite Fassung
/// davon zu pflegen - und eine Probe, die nach einer anderen Regel antwortet als die Buchungen, ist
/// schlimmer als keine.
/// </summary>
public sealed class MerchantResolveProbeTests
{
    private static string Seite() => WebSources.Asset("pages", "merchants", "page.js");

    /// <summary>
    /// Nur die Probe, nicht die ganze Seite. Die Haendlerliste darueber zeigt Aliasse an und baut ihre
    /// Zeilen ueber Markup - das ist ihre Sache und war nie Gegenstand dieses Tests.
    /// </summary>
    private static string Probe()
    {
        var quelle = Seite();
        var start = quelle.IndexOf("function bindProbe()", StringComparison.Ordinal);
        var ende = quelle.IndexOf("export function newMerchant", StringComparison.Ordinal);
        Assert.True(start >= 0 && ende > start, "Die Probe steht nicht mehr, wo dieser Test sie sucht.");
        return quelle[start..ende];
    }

    [Fact]
    public void The_route_has_a_caller()
    {
        Assert.Contains("api/merchants/resolve?counterparty=", Seite(), StringComparison.Ordinal);
        Assert.Contains("id=\"merchant-probe\"", WebSources.Page("Merchants"), StringComparison.Ordinal);
    }

    /// <summary>
    /// Kein zweiter Abgleich im Browser. Waere die Regel hier nachgebaut, stuende sie an zwei Orten -
    /// und der zweite wuerde beim naechsten Umbau des ersten nicht mitwandern.
    /// </summary>
    [Fact]
    public void The_matching_rule_stays_on_the_server()
    {
        // Auf die Probe eingegrenzt: die Liste darueber zeigt Aliasse als Chips an, und das ist
        // etwas anderes als sie abzugleichen.
        var probe = Probe();

        Assert.DoesNotContain("normalizedAlias", probe, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".length", probe, StringComparison.Ordinal);
        Assert.DoesNotContain(".includes(", probe, StringComparison.Ordinal);
        // Die Antwort wird gezeigt, nicht ausgewertet.
        Assert.Contains("result?.merchantName", probe, StringComparison.Ordinal);
    }

    /// <summary>
    /// Die normalisierte Form gehoert dazu: sie erklaert, WARUM etwas passt oder nicht - Gross- und
    /// Kleinschreibung, Satzzeichen und Rechtsformen fallen dabei weg. Ohne sie steht man vor einem
    /// "passt nicht" ohne Anhaltspunkt.
    /// </summary>
    [Fact]
    public void The_normalised_form_is_shown_as_the_reason()
    {
        Assert.Contains("result?.normalizedCounterparty", Seite(), StringComparison.Ordinal);
        Assert.Contains("merchants.probeNormalized", Seite(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Fremder Text - der Name eines Haendlers und der Auszugstext - geht ueber
    /// <c>textContent</c>. Beides kommt aus Daten, die jemand anderes geschrieben hat.
    /// </summary>
    [Fact]
    public void Text_from_the_answer_never_goes_through_markup()
    {
        var probe = Probe();

        Assert.DoesNotContain("innerHTML", probe, StringComparison.Ordinal);
        Assert.Contains("textContent", probe, StringComparison.Ordinal);
    }

    [Fact]
    public void The_labels_exist_in_both_languages()
    {
        foreach (var sprache in new[] { "de", "en" })
        {
            var locale = WebSources.Asset("locales", $"{sprache}.json");
            foreach (var key in new[] { "\"probeTitle\"", "\"probeHint\"", "\"probeRun\"", "\"probeNoMatch\"", "\"probeNormalized\"" })
                Assert.Contains(key, locale, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Der Depot-Beitrag ist geloescht statt verdrahtet. <c>InvestmentNetWorthService</c> rechnet ihn
    /// laengst, und drei Leser benutzen ihn schon - die Vermoegensuebersicht, die Schnappschuesse und
    /// Analytics. Die Route lieferte dieselbe Zahl ein viertes Mal, isoliert und ohne Anzeigeort.
    /// </summary>
    [Fact]
    public void The_isolated_investment_contribution_route_is_gone()
    {
        var surface = File.ReadAllText(Path.Combine(WebSources.RepoRoot,
            "tests", "FullWorth.Backend.Tests", "Architecture", "route-surface.txt"));

        Assert.DoesNotContain("/api/investments/net-worth-contribution", surface, StringComparison.Ordinal);
    }
}
