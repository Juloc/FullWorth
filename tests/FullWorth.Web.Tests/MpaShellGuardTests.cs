using FullWorth.Web.Navigation;
using System.Text.RegularExpressions;

namespace FullWorth.Web.Tests;

/// <summary>
/// Die Regeln der neuen Seitenarchitektur, als Test (#154).
///
/// Der ganze Zweck der Migration ist: <c>/transactions</c> laedt das Markup, CSS und JS von
/// Buchungen - und von nichts anderem. Das faellt im Betrieb nicht auf, wenn es schiefgeht; die Seite
/// sieht richtig aus und ist nur langsam und gross. Genau so ist die heutige Huelle entstanden, ohne
/// dass es jemand entschieden haette: 100 KiB Dokument und 97 vorgeladene Module, Seite fuer Seite
/// dazugekommen.
///
/// Deshalb stehen die Regeln hier, bevor die erste Seite umzieht, und nicht danach.
/// </summary>
public sealed class MpaShellGuardTests
{
    /// <summary>
    /// Eine Unterseite markiert den Eintrag, unter dem sie hängt.
    ///
    /// „Passkeys" steht nicht in der Seitenleiste, „Einstellungen" darüber schon — und genau der
    /// gehört hervorgehoben, sonst ist auf jeder Unterseite gar nichts markiert und der Benutzer
    /// sieht nicht, wo er ist. Die Zuordnung steht in NavigationCatalog.SubPages, im Browser in
    /// app/routes.js und in der Werkstatt in ops/ui-harness/razor.mjs; alle drei müssen dieselbe
    /// Regel befolgen, sonst misst die Werkstatt eine Navigation, die es im Betrieb nicht gibt.
    /// </summary>
    [Fact]
    public void A_subpage_marks_its_parent_entry()
    {
        var navigation = Read("Pages", "Shared", "_Navigation.cshtml");
        Assert.Contains("NavigationCatalog.SubPages", navigation);
        Assert.Contains(".Parent", navigation);

        var harness = File.ReadAllText(Path.Combine(Root(), "ops", "ui-harness", "razor.mjs"));
        Assert.Contains("PARENTS", harness);

        // Jede Unterseite hängt an einem Eintrag, den es wirklich gibt — ein Tippfehler im Elternteil
        // markiert sonst lautlos gar nichts.
        var views = NavigationCatalog.Entries.Select(entry => entry.View).ToHashSet(StringComparer.Ordinal);
        foreach (var page in NavigationCatalog.SubPages)
            Assert.True(views.Contains(page.Parent),
                $"Die Unterseite {page.View} hängt an {page.Parent}, das es in der Navigation nicht gibt.");
    }

    /// <summary>
    /// Die Möbel der Seitenleiste werden von der GETEILTEN Hülle verdrahtet, nicht von app.js.
    ///
    /// Der Einklapp-Knopf steht im Markup jeder Seite, verdrahtet wurde er aber nur in app.js — und
    /// das lädt eine Razor-Seite nicht. Über vier Commits hinweg war er auf zwanzig Seiten da und tat
    /// nichts, und der Ziehgriff für die Breite fehlte dort ganz. Im Quelltext sah alles richtig aus:
    /// das Markup stimmte, die Funktion existierte, nur lief sie nie.
    ///
    /// Deshalb prüft dieser Test nicht das Markup, sondern wer bindet. Alles, was auf jeder Seite
    /// steht, gehört in app/shell.js — sonst gilt es nur dort, wo zufällig noch app.js läuft.
    /// </summary>
    [Fact]
    public void The_sidebar_furniture_is_wired_by_the_shared_shell()
    {
        var shell = Read("wwwroot", "app", "shell.js");

        foreach (var wiring in new[] { "'#nav-collapse'", "'#layout-reset'", "initResizableSidebar()" })
            Assert.True(shell.Contains(wiring, StringComparison.Ordinal),
                $"app/shell.js verdrahtet {wiring} nicht - dann gilt es nur in der alten Hülle.");

        // Die alte Huelle, in der diese Verdrahtung einmal allein stand, gibt es seit #154 nicht mehr.
        // Das ist die staerkere Fassung derselben Aussage: es gibt keinen zweiten Ort.
        Assert.False(File.Exists(Path.Combine(Root(), "src", "FullWorth.Web", "wwwroot", "app.js")),
            "app.js ist zurueck - dann gibt es wieder zwei Huellen und eine davon laeuft nur auf einer Seite.");
    }

    [Fact]
    public void The_layout_links_no_page_specific_asset()
    {
        var layout = Read("Pages", "Shared", "_Layout.cshtml");
        var offenders = Regex.Matches(layout, @"(?:href|src)=""(/pages/[^""]+)""")
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        Assert.True(offenders.Length == 0,
            "Der Rahmen bindet Dateien einzelner Seiten ein. Damit laedt JEDE Seite sie mit - das ist " +
            "die alte Huelle unter neuem Namen. Was zu einer Seite gehoert, gehoert in ihre Abschnitte " +
            "\"Styles\" und \"Scripts\":" +
            Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", offenders));
    }

    [Fact]
    public void The_layout_preloads_no_module_graph()
    {
        var layout = Read("Pages", "Shared", "_Layout.cshtml");
        // Gesucht ist die LINK-Zeile, nicht das Wort - im Kommentar des Rahmens steht es zu Recht.
        Assert.DoesNotContain("rel=\"modulepreload\"", layout, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_global_shell_script_imports_no_page_entrypoint()
    {
        var shell = Read("wwwroot", "app", "shell.js");
        var offenders = Regex.Matches(shell, @"from\s+'([^']*pages/[^']+)'")
            .Select(match => match.Groups[1].Value)
            .ToArray();

        Assert.True(offenders.Length == 0,
            "Die globale Shell importiert Seitenmodule. Sobald sie das tut, laedt wieder jede Seite " +
            "jede andere:" + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", offenders));
    }

    [Fact]
    public void The_navigation_comes_from_the_catalog_and_not_from_a_second_list()
    {
        foreach (var file in new[] { "_Navigation.cshtml", "_BottomNavigation.cshtml" })
        {
            var markup = Read("Pages", "Shared", file);
            Assert.Contains("NavigationCatalog", markup, StringComparison.Ordinal);
            // Ein fest eingetragener Pfad waere der Anfang einer zweiten Menueliste.
            Assert.DoesNotContain("href=\"/transactions\"", markup, StringComparison.Ordinal);
            Assert.DoesNotContain("href=\"/accounts\"", markup, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Every_address_is_a_razor_page_and_there_is_no_fallback()
    {
        var program = Read("Program.cs");

        Assert.Contains("app.MapRazorPages()", program, StringComparison.Ordinal);

        // Gar kein Rueckfall mehr: jede Adresse ist eine Seite, also darf eine unbekannte Adresse 404
        // sagen statt wortlos die Uebersicht zu zeigen. Der Rueckfall gab es nur, weil die alte Huelle
        // ihre Adressen im Browser aufloeste und das Dokument unter jeder von ihnen brauchte.
        //
        // MapFallbackToPage ist kein Ersatz: es hat einen PUT auf eine unbekannte Adresse mitgenommen
        // und mit 200 und einer fertigen HTML-Seite beantwortet - der Aufrufer haelt seinen
        // Schreibzugriff dann fuer gelungen.
        // Gesucht ist der AUFRUF, nicht das Wort - im Kommentar darueber steht beides zu Recht.
        Assert.DoesNotContain("app.MapFallbackTo", program, StringComparison.Ordinal);
    }

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine(
            new[] { Root(), "src", "FullWorth.Web" }.Concat(parts).ToArray()));

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FullWorth.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
