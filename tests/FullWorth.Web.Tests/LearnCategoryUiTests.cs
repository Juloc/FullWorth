using FullWorth.Web.Tests.Pwa;
namespace FullWorth.Web.Tests;

/// <summary>
/// „Immer so kategorisieren" (#177).
///
/// <c>POST /api/category-intelligence/learn</c> stand fertig im Baum und hatte keinen Aufrufer.
/// Teuer war der Fund nicht wegen der fehlenden Oberflaeche, sondern wegen dessen, was daran haengt:
/// bei <c>scope=future</c> - und NUR dort - meldet der Server die bestaetigte
/// Haendler-zu-Kategorie-Zuordnung an die Cloud weiter. „REWE ist Lebensmittel" gilt fuer jeden;
/// „diese eine Buchung gehoert zu Urlaub" gilt nur hier.
/// </summary>
public sealed class LearnCategoryUiTests
{
    private static string Modul() => WebSources.Asset("pages", "transactions", "learn-category.js");

    [Fact]
    public void The_route_has_a_caller_and_the_module_is_precached()
    {
        var quelle = Modul();

        Assert.Contains("api/category-intelligence/learn", quelle, StringComparison.Ordinal);
        PwaAssert.Ships("/pages/transactions/learn-category.js", WebSources.Asset("sw.js"));
        Assert.Contains("openLearnCategory", WebSources.Asset("pages", "transactions", "page.js"), StringComparison.Ordinal);
    }

    /// <summary>
    /// "one" wird bewusst nicht angeboten: das ist genau die Auswahl, die der Detaildialog darueber
    /// ohnehin schon macht. Ein dritter Eintrag, der dasselbe tut wie das Feld daneben, ist kein
    /// Umfang, sondern eine Verdopplung.
    /// </summary>
    [Fact]
    public void Only_the_two_scopes_that_do_something_new_are_offered()
    {
        var quelle = Modul();

        Assert.Contains("value: 'future'", quelle, StringComparison.Ordinal);
        Assert.Contains("value: 'existing'", quelle, StringComparison.Ordinal);
        Assert.DoesNotContain("value: 'one'", quelle, StringComparison.Ordinal);
    }

    /// <summary>
    /// Ohne Haendler kann der Server nichts lernen - er antwortet mit 400. Ein Knopf, der garantiert
    /// scheitert, ist schlimmer als keiner, also gibt es ihn dann gar nicht erst.
    /// </summary>
    [Fact]
    public void Without_a_merchant_there_is_no_button()
    {
        Assert.Contains("export function canLearnFrom", Modul(), StringComparison.Ordinal);
        Assert.Contains("canLearnFrom(t)", WebSources.Asset("pages", "transactions", "page.js"), StringComparison.Ordinal);
    }

    /// <summary>
    /// Und ohne Kategorie auch nicht: "immer WAS?" ist keine Frage. Die Zeile erscheint erst, wenn
    /// eine dasteht, statt ausgegraut Platz zu halten.
    /// </summary>
    [Fact]
    public void Without_a_category_the_row_stays_hidden()
    {
        Assert.Contains("learnRow.hidden = !sel.value",
            WebSources.Asset("pages", "transactions", "page.js"), StringComparison.Ordinal);
    }

    /// <summary>
    /// Die Zahl gehoert in die Rueckmeldung. "Gespeichert" allein sagt nicht, dass gerade 34
    /// Buchungen umkategorisiert wurden - und genau das ist passiert.
    /// </summary>
    [Fact]
    public void The_result_says_how_many_transactions_changed()
    {
        Assert.Contains("result?.affected", Modul(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Die dritte Massenaenderung fuer Buchungen ist weg und kommt nicht zurueck. Sie hatte keinen
    /// Aufrufer und konnte weniger als die verbliebene unter /api/transaction-bulk/apply - kein
    /// Filter, keine Sicherung ueber ExpectedCount, kein Vertrag, keine Notiz.
    /// </summary>
    [Fact]
    public void The_third_bulk_engine_is_gone()
    {
        var surface = File.ReadAllText(Path.Combine(WebSources.RepoRoot,
            "tests", "FullWorth.Backend.Tests", "Architecture", "route-surface.txt"));

        Assert.DoesNotContain("/api/category-intelligence/bulk", surface, StringComparison.Ordinal);
        Assert.Contains("/api/transaction-bulk/apply", surface, StringComparison.Ordinal);
    }
}
