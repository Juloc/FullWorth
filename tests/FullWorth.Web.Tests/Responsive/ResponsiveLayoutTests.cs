namespace FullWorth.Web.Tests.Responsive;

/// <summary>
/// Release-readiness guard (Wave N3): the app keeps its responsive breakpoints and the key layout
/// adaptations (collapsing multi-column grids, an adapted sidebar, horizontally-scrollable data
/// tables) so a future CSS edit can't silently drop mobile/tablet support. Pure file check.
/// </summary>
public sealed class ResponsiveLayoutTests
{
    [Fact]
    public void HasTabletAndMobileBreakpoints()
    {
        var css = ReadCss();
        // UI_UX_SPEC §3: desktop sidebar from 1024px; below that the mobile bottom-nav model.
        Assert.Contains("@media(max-width:1023px)", css);
        Assert.Contains("@media(max-width:767px)", css);
    }

    [Fact]
    public void DataTablesScrollHorizontallyInsteadOfOverflowing()
    {
        var css = ReadCss();
        // The transactions grid is wrapped in a horizontally-scrollable panel on narrow screens.
        Assert.Matches(@"\.table-panel\{[^}]*overflow:auto", css);
    }

    [Fact]
    public void MobileBreakpointCollapsesMultiColumnLayout()
    {
        var css = ReadCss();
        var start = css.IndexOf("@media(max-width:767px)", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var mobile = css[start..];
        // Two-column content grid collapses to one column on phones.
        Assert.Contains(".content-grid", mobile);
        Assert.Contains("grid-template-columns:1fr", mobile);
    }

    [Fact]
    public void BelowDesktopUsesFixedBottomNavAndHidesTheSidebar()
    {
        var css = ReadCss();
        // Regression guard: nav labels were once collapsed via font-size:0 + a "•" pseudo-element,
        // leaving the phone bar a row of unreadable dots. Now icons + labels in a real bottom nav.
        Assert.DoesNotContain("font-size:0", css);
        Assert.DoesNotContain("content:\"•\"", css);

        var start = css.IndexOf("@media(max-width:1023px)", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var tabletDown = css[start..];
        // Desktop sidebar is hidden; the fixed, safe-area-aware bottom nav takes over.
        Assert.Contains(".sidebar{display:none", tabletDown);
        Assert.Matches(@"#bottom-nav\{display:grid[^}]*position:fixed", tabletDown);
        Assert.Contains("env(safe-area-inset-bottom)", tabletDown);
        Assert.Matches(@"#bottom-nav button span\{font-size:10px", tabletDown);
    }

    [Fact]
    public void BottomNavHasExactlyFivePrimaryDestinations()
    {
        var root = RepoRoot();
        var html = File.ReadAllText(Path.Combine(root, "src", "FullWorth.Web", "wwwroot", "index.html"));
        var nav = html[html.IndexOf("id=\"bottom-nav\"", StringComparison.Ordinal)..];
        nav = nav[..nav.IndexOf("</nav>", StringComparison.Ordinal)];
        // UI_UX_SPEC §3.2: exactly five visible destinations (four sections + More).
        var buttons = System.Text.RegularExpressions.Regex.Matches(nav, "<button").Count;
        Assert.Equal(5, buttons);
        Assert.Contains("id=\"bottom-more\"", nav);
    }

    [Fact]
    public void EveryDeclaredActionButtonIsWiredInAppJs()
    {
        var root = RepoRoot();
        var wwwroot = Path.Combine(root, "src", "FullWorth.Web", "wwwroot");
        var html = File.ReadAllText(Path.Combine(wwwroot, "index.html"));
        var appJs = File.ReadAllText(Path.Combine(wwwroot, "app.js"));
        // The frontend is a set of ES modules: app.js orchestrates, feature/ui modules own their screens.
        // A data-action may be wired in app.js OR in the module that owns that screen (e.g. features/rules.js).
        var allJs = appJs + string.Concat(Directory
            .EnumerateFiles(wwwroot, "*.js", SearchOption.AllDirectories)
            .Where(path => !path.EndsWith("app.js", StringComparison.Ordinal))
            .Select(File.ReadAllText));

        // Regression guard: "Add" buttons (budgets/contracts/rules) once existed in the markup with
        // no click handler at all — visibly dead UI. Every data-action must have a JS binding somewhere.
        var actions = System.Text.RegularExpressions.Regex.Matches(html, "data-action=\"([^\"]+)\"")
            .Select(match => match.Groups[1].Value).Distinct().ToList();
        Assert.NotEmpty(actions);
        foreach (var action in actions)
            Assert.Contains($"[data-action=\"{action}\"]", allJs);

        // Every nav view must be routed in loadCurrent (which lives in app.js), and dead nav entries are caught.
        var views = System.Text.RegularExpressions.Regex.Matches(html, "data-view=\"([^\"]+)\"")
            .Select(match => match.Groups[1].Value).Distinct().ToList();
        Assert.NotEmpty(views);
        foreach (var view in views)
            Assert.Contains($"case'{view}'", appJs);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FullWorth.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string ReadCss()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FullWorth.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var path = Path.Combine(dir!.FullName, "src", "FullWorth.Web", "wwwroot", "app.css");
        Assert.True(File.Exists(path), $"app.css not found: {path}");
        return File.ReadAllText(path);
    }
}
