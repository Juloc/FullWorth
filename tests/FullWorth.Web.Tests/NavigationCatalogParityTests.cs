using System.Text.RegularExpressions;
using FullWorth.Web.Navigation;

namespace FullWorth.Web.Tests;

/// <summary>
/// Die Navigation steht waehrend der Migration zweimal da (#154).
///
/// Serverseitig in <see cref="NavigationCatalog"/>, weil jede Razor-Seite sie braucht, und weiterhin
/// in <c>wwwroot/app/menu.js</c>, weil die alte Huelle von dort ihre Seitenleiste zeichnet. Das ist
/// ein Uebergangszustand mit Ablaufdatum: faellt die Huelle, faellt die JS-Fassung mit ihr.
///
/// Solange es beide gibt, duerfen sie nicht auseinanderlaufen. Ein Eintrag nur im Katalog waere eine
/// Razor-Seite, zu der kein Weg fuehrt; ein Eintrag nur in der JS-Fassung eine Seitenleiste, die ins
/// Leere zeigt. Beides faellt im Betrieb erst auf, wenn jemand klickt.
/// </summary>
public sealed class NavigationCatalogParityTests
{
    [Fact]
    public void Both_definitions_list_the_same_entries_in_the_same_order()
    {
        var script = MenuScript();
        var entries = Regex.Matches(script, @"\{\s*view:\s*'([\w-]+)'\s*,\s*label:\s*'([\w.]+)'")
            .Select(match => (View: match.Groups[1].Value, Label: match.Groups[2].Value))
            .ToArray();
        Assert.NotEmpty(entries);

        Assert.Equal(
            entries,
            NavigationCatalog.Entries.Select(entry => (entry.View, entry.Label)).ToArray());
    }

    [Fact]
    public void Both_definitions_hide_the_same_entries_from_a_session_without_admin_rights()
    {
        var script = MenuScript();
        var adminOnly = Regex.Matches(script, @"\{\s*view:\s*'([\w-]+)'[^}]*admin:\s*true")
            .Select(match => match.Groups[1].Value)
            .ToArray();

        Assert.Equal(
            adminOnly,
            NavigationCatalog.Entries.Where(entry => entry.AdminOnly).Select(entry => entry.View).ToArray());
    }

    [Fact]
    public void Both_definitions_name_the_same_four_quick_targets()
    {
        var script = MenuScript();
        var match = Regex.Match(script, @"export const QUICK = \[([^\]]*)\]");
        Assert.True(match.Success, "QUICK steht nicht mehr in app/menu.js.");
        var quick = Regex.Matches(match.Groups[1].Value, @"'([\w-]+)'").Select(x => x.Groups[1].Value).ToArray();

        Assert.Equal(quick, NavigationCatalog.Quick.ToArray());
    }

    [Fact]
    public void Both_definitions_agree_on_every_subpage_address()
    {
        var app = File.ReadAllText(Path.Combine(WwwRoot(), "app.js"));
        var start = app.IndexOf("const SUBPAGES={", StringComparison.Ordinal);
        var end = app.IndexOf("const ALL_VIEWS=", StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "SUBPAGES steht nicht mehr in app.js.");

        var subPages = Regex.Matches(
                app[start..end],
                @"'?([\w-]+)'?\s*:\s*\{\s*path:\s*'([^']+)'\s*,\s*parent:\s*'([^']+)'")
            .Select(match => (View: match.Groups[1].Value, Path: match.Groups[2].Value, Parent: match.Groups[3].Value))
            .ToArray();
        Assert.NotEmpty(subPages);

        Assert.Equal(
            subPages,
            NavigationCatalog.SubPages.Select(page => (page.View, page.Path, page.Parent)).ToArray());
    }

    [Fact]
    public void Every_address_is_unique_and_absolute()
    {
        var paths = NavigationCatalog.AllPaths.ToArray();
        Assert.All(paths, path => Assert.StartsWith("/", path, StringComparison.Ordinal));
        Assert.Equal(paths.Length, paths.Distinct(StringComparer.Ordinal).Count());
    }

    private static string MenuScript() => File.ReadAllText(Path.Combine(WwwRoot(), "app", "menu.js"));

    private static string WwwRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FullWorth.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src", "FullWorth.Web", "wwwroot");
    }
}
