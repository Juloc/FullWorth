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
    public void Razor_pages_are_mapped_before_the_old_shell_catches_the_request()
    {
        var program = Read("Program.cs");
        var razor = program.IndexOf("app.MapRazorPages()", StringComparison.Ordinal);
        var fallback = program.IndexOf("app.MapFallbackToFile(\"index.html\")", StringComparison.Ordinal);

        Assert.True(razor >= 0, "Razor Pages werden nicht gemappt - keine der neuen Seiten waere erreichbar.");
        Assert.True(fallback < 0 || razor < fallback,
            "Der Rueckfall auf index.html steht vor den Razor-Seiten. Dann beantwortet die alte Huelle " +
            "auch Adressen, fuer die es laengst eine eigene Seite gibt.");
    }

    private static string Read(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FullWorth.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(
            new[] { dir!.FullName, "src", "FullWorth.Web" }.Concat(parts).ToArray()));
    }
}
