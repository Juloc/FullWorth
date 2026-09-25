using FullWorth.Web.Tests.Pwa;
namespace FullWorth.Web.Tests;

/// <summary>
/// Die Zusammenfassungen der geplanten Intelligence-Laeufe (#177).
///
/// Unter den Funden dieses Issues der ungewoehnlichste: hinter <c>GET /api/intelligence/digests</c>
/// stand keine leere Tabelle, sondern echte Daten. <c>IntelligenceDigestService</c> schreibt bei
/// jedem woechentlichen und monatlichen Lauf eine Zusammenfassung - samt der AI-Kosten des
/// Zeitraums -, und niemand konnte sie sehen. Die Kosten sind der Grund, warum die Ansicht auf die
/// Intelligence-Seite gehoert: der Ueberblick dort zeigt nur den laufenden Monat.
/// </summary>
public sealed class IntelligenceDigestUiTests
{
    private static string Modul() => WebSources.Asset("pages", "settings", "intelligence", "digests.js");

    [Fact]
    public void The_route_has_a_caller_and_the_module_is_precached()
    {
        Assert.Contains("api/intelligence/digests", Modul(), StringComparison.Ordinal);
        Assert.Contains("id=\"digest-list\"", WebSources.Page("Settings/Intelligence"), StringComparison.Ordinal);
        Assert.Contains("renderIntelligenceDigests", WebSources.Asset("pages", "settings", "intelligence", "page.js"), StringComparison.Ordinal);
        PwaAssert.Ships("/pages/settings/intelligence/digests.js", WebSources.Asset("sw.js"));
    }

    /// <summary>
    /// Ein Rueckblick darf den Zustand der Seite nicht aufhalten. Der Abruf laeuft deshalb neben
    /// reload() und nicht darin; ein Fehler darin laesst den Rest der Seite in Ruhe.
    /// </summary>
    [Fact]
    public void A_failing_digest_load_does_not_take_the_page_with_it()
    {
        Assert.Contains("renderIntelligenceDigests().catch(console.error)",
            WebSources.Asset("pages", "settings", "intelligence", "page.js"), StringComparison.Ordinal);
    }

    /// <summary>
    /// Leer ist hier kein Fehler: vor dem ersten geplanten Lauf gibt es keine Zusammenfassung. Das
    /// gehoert dagestanden, sonst liest sich die leere Liste wie ein Defekt.
    /// </summary>
    [Fact]
    public void An_empty_list_explains_itself()
    {
        var quelle = Modul();

        Assert.Contains("Noch keine Zusammenfassung", quelle, StringComparison.Ordinal);
        Assert.Contains("No digest yet", quelle, StringComparison.Ordinal);
    }

    /// <summary>
    /// Der Inhalt kommt aus der Antwort und wird nicht nachgerechnet - insbesondere nicht die Kosten.
    /// Eine zweite Rechnung waere eine zweite Wahrheit ueber Geld.
    /// </summary>
    [Fact]
    public void It_shows_what_the_summary_says_and_computes_nothing()
    {
        var quelle = Modul();

        Assert.Contains("estimatedOrActualCostEur", quelle, StringComparison.Ordinal);
        Assert.Contains("summary?.suggestions", quelle, StringComparison.Ordinal);
        Assert.Contains("summary?.unresolved", quelle, StringComparison.Ordinal);
    }

    /// <summary>
    /// Der Detailabruf je Zusammenfassung ist geloescht statt verdrahtet: er lieferte dieselbe
    /// Ansicht wie die Liste, die das vollstaendige <c>summary</c> ohnehin mitbringt.
    /// </summary>
    [Fact]
    public void There_is_no_second_reader_for_a_single_digest()
    {
        var surface = File.ReadAllText(Path.Combine(WebSources.RepoRoot,
            "tests", "FullWorth.Backend.Tests", "Architecture", "route-surface.txt"));

        Assert.DoesNotContain("/api/intelligence/digests/{id:guid}", surface, StringComparison.Ordinal);
        Assert.Contains("/api/intelligence/digests/", surface, StringComparison.Ordinal);
    }

    /// <summary>
    /// Und die Bank-Faehigkeiten sind ganz weg. In ihrer Tabelle hat nie etwas geschrieben, also
    /// antwortete der Endpunkt immer aus einer fest eingebauten Liste "geplanter" Institute - die
    /// inzwischen falsch ist, weil ING ueber den eigenen FinTS-Weg laeuft.
    /// </summary>
    [Fact]
    public void The_bank_capability_stub_is_gone()
    {
        var surface = File.ReadAllText(Path.Combine(WebSources.RepoRoot,
            "tests", "FullWorth.Backend.Tests", "Architecture", "route-surface.txt"));

        Assert.DoesNotContain("/api/bank-capabilities", surface, StringComparison.Ordinal);
    }
}
