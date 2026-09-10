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
        // The fallback is not cosmetic: without it, a browser that does not support safe-area insets
        // treats the whole declaration as invalid and drops it, so the bottom nav ends up with no inset
        // padding at all and covers the last row of the list.
        Assert.Contains("env(safe-area-inset-bottom,0px)", tabletDown);
        Assert.DoesNotMatch(@"env(safe-area-inset-[a-z]+)", tabletDown);
        Assert.Matches(@"#bottom-nav button span\{font-size:10px", tabletDown);
    }

    /// <summary>
    /// Every dialog overflowed the viewport as soon as its content held one long string. `.dialog-card`
    /// is `display:grid` with no `grid-template-columns`, so its single implicit column is `auto`, which
    /// resolves to MAX-CONTENT — and a `select`'s max-content width is its longest `option`. Measured on
    /// the booking filter at 375 px: the column came out 435 px and every field sat 76 px past the right
    /// edge. It looked random because it depends on the data, which is why it went unpinned for so long.
    /// </summary>
    [Fact]
    public void DialogContentIsAllowedToShrinkBelowItsLongestOption()
    {
        var css = ReadAsset("dialogs.css");

        // minmax(0,1fr), not 1fr: a grid item's default min-width:auto refuses to go below min-content,
        // so 1fr alone would not have fixed it.
        Assert.Contains(".dialog-card{grid-template-columns:minmax(0,1fr)}", css);
        Assert.Contains(".dialog-card>*{min-width:0}", css);
        Assert.Contains(".dialog-card label{grid-template-columns:minmax(0,1fr);min-width:0}", css);
        Assert.Contains(".dialog-card input,.dialog-card select,.dialog-card textarea{min-width:0;max-width:100%}", css);
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

        // Every nav view must be routed. loadCurrent (in app.js) used to dispatch through a switch/case;
        // the architecture cleanup replaced that with the shared core/feature-registry.js, so each view
        // is now wired via a `.register('view', ...)` call in app.js. Dead nav entries are still caught.
        var views = System.Text.RegularExpressions.Regex.Matches(html, "data-view=\"([^\"]+)\"")
            .Select(match => match.Groups[1].Value).Distinct().ToList();
        Assert.NotEmpty(views);
        foreach (var view in views)
            Assert.Contains($".register('{view}'", appJs);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FullWorth.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string ReadAsset(string name) =>
        File.ReadAllText(Path.Combine(RepoRoot(), "src", "FullWorth.Web", "wwwroot", name));

    private static string ReadCss()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FullWorth.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var root = Path.Combine(dir!.FullName, "src", "FullWorth.Web", "wwwroot");
        var paths = new[]
        {
            Path.Combine(root, "styles", "reset.css"),
            Path.Combine(root, "styles", "shell.css"),
            Path.Combine(root, "styles", "components.css"),
            Path.Combine(root, "app.css"),
            Path.Combine(root, "styles", "responsive.css")
        };
        foreach (var path in paths) Assert.True(File.Exists(path), $"css layer not found: {path}");
        return string.Concat(paths.Select(File.ReadAllText));
    }
}
