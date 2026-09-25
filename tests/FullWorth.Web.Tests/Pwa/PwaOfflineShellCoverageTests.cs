using System.Text.RegularExpressions;

namespace FullWorth.Web.Tests.Pwa;

/// <summary>
/// The existing PWA tests check that everything the service worker lists exists on disk. Nothing checked
/// the other direction — that everything the app shell needs is listed — and 52 of the 137 stylesheets
/// and modules in wwwroot were not, including ten `components/` modules the shell itself imports.
///
/// The fetch handler is network-first with a cache fallback, so online this is invisible: an asset is
/// cached the first time it is fetched. Offline it is not. An installed PWA that cold-starts without a
/// connection can only work if every module reachable from index.html is already in the precache, and
/// this test walks the real import graph to insist on it.
///
/// Feature pages that are loaded on demand are deliberately NOT required here — they are not needed for
/// the shell to come up, and precaching every screen would trade offline breadth for install weight.
/// </summary>
public sealed class PwaOfflineShellCoverageTests
{
    [Fact]
    public void Every_module_the_shell_imports_is_precached()
    {
        var precached = PrecachedPaths();
        var reachable = ReachableFromIndex();
        // Findet das Muster nichts - etwa weil das Markup seine Adressen anders schreibt -, waere dieser
        // Test ueber eine leere Liste gruen und bewiese nichts.
        Assert.NotEmpty(reachable);

        var missing = reachable.Where(path => !precached.Contains(path)).Order(StringComparer.Ordinal).ToArray();

        Assert.True(
            missing.Length == 0,
            "the app shell imports these but the service worker does not precache them, so an offline "
            + "cold start cannot load them:" + Environment.NewLine + string.Join(Environment.NewLine, missing));
    }

    [Fact]
    public void Every_stylesheet_the_shell_links_is_precached()
    {
        var precached = PrecachedPaths();
        var index = WebSources.Layout();

        var linked = Regex.Matches(index, """<link[^>]+rel="stylesheet"[^>]+href="~?(?<path>/[^"?#]+)""")
            .Select(match => match.Groups["path"].Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Assert.NotEmpty(linked);
        var missing = linked
            .Where(path => !precached.Contains(path))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            missing.Length == 0,
            "index.html links these stylesheets but they are not precached:" + Environment.NewLine
            + string.Join(Environment.NewLine, missing));
    }

    /// <summary>
    /// Gehalt is its own HTML page rather than an SPA view, so the shell's import graph never reached it
    /// — and it is the one standalone page that genuinely works with no network, because the German
    /// payroll engine is pure client-side maths.
    ///
    /// Auth, admin, intelligence, passkeys and account deletion are deliberately NOT asserted here.
    /// Every one of them needs the server to do anything, so precaching them would only fake
    /// availability: the page would open offline and then fail on its first request.
    /// </summary>
    [Fact]
    public void The_salary_page_works_offline()
    {
        // Seit #154 ist Gehalt eine eigene Razor-Seite, und ihr Einstieg ist pages/compensation/
        // entry.js statt index.html. Die Frage bleibt dieselbe - was diese Seite laedt, muss im
        // Vorrat liegen -, nur die Wurzel des Importgraphen hat gewechselt.
        var precached = PrecachedPaths();
        var reachable = WebSources.Reachable(WebSources.Page("Compensation"))
            .Where(path => path.StartsWith("/pages/compensation/", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(reachable);
        var missing = reachable.Where(path => !precached.Contains(path)).Order(StringComparer.Ordinal).ToArray();
        Assert.True(
            missing.Length == 0,
            "the Gehalt page needs these and they are not precached:" + Environment.NewLine
            + string.Join(Environment.NewLine, missing));
    }

    /// <summary>
    /// Die Gegenrichtung (#154, Abschnitt 12): keine weitere Seite im Vorrat. Waehrend der
    /// Razor-Umstellung standen hier 126 Seitendateien, von denen die Offline-Pruefung acht brauchte -
    /// jede frische Installation lud im Hintergrund die Module saemtlicher Seiten, und keine davon
    /// haette offline geoeffnet, weil der Worker das HTML einer Seite nie cacht. Was eine Seite braucht,
    /// landet beim ersten Besuch im Cache.
    /// </summary>
    [Fact]
    public void No_page_is_precached_beyond_what_works_offline()
    {
        var allowed = ReachableFromIndex()
            .Concat(WebSources.Reachable(WebSources.Page("Compensation"))
                .Where(path => path.StartsWith("/pages/compensation/", StringComparison.Ordinal)))
            .ToHashSet(StringComparer.Ordinal);

        var surplus = PrecachedPaths()
            .Where(path => path.StartsWith("/pages/", StringComparison.Ordinal) && !allowed.Contains(path))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            surplus.Length == 0,
            "the service worker precaches these page files, but no page opens offline without its HTML, "
            + "which is never cached - they only make every install download the whole app:"
            + Environment.NewLine + string.Join(Environment.NewLine, surplus));
    }

    private static HashSet<string> PrecachedPaths()
    {
        var sw = WebSources.Asset("sw.js");
        var shell = sw[(sw.IndexOf("const APP_SHELL = [", StringComparison.Ordinal) + 1)..];
        shell = shell[..shell.IndexOf("];", StringComparison.Ordinal)];
        return Regex.Matches(shell, """['"](?<path>/[^'"]+)['"]""")
            .Select(match => match.Groups["path"].Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Was das gemeinsame Layout laedt - der Graph selbst steht in <see cref="WebSources.Reachable"/>.</summary>
    private static HashSet<string> ReachableFromIndex() => WebSources.Reachable(WebSources.Layout());
}
