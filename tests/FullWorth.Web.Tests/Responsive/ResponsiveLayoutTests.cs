using FullWorth.Web.Navigation;
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
        // The desktop sidebar's full multi-group menu is hidden; the fixed, safe-area-aware bottom nav
        // takes over primary navigation. (#171: the sidebar element itself stays, shrunk to a compact
        // brand strip above the topbar - only its menu/nav-list and user-footer are gone below desktop.)
        Assert.Contains(".sidebar #nav,.sidebar .sidebar-foot", tabletDown);
        Assert.Matches(@"#bottom-nav\{display:grid[^}]*position:fixed", tabletDown);
        // The fallback is not cosmetic: without it, a browser that does not support safe-area insets
        // treats the whole declaration as invalid and drops it, so the bottom nav ends up with no inset
        // padding at all and covers the last row of the list.
        Assert.Contains("env(safe-area-inset-bottom,0px)", tabletDown);
        Assert.DoesNotMatch(@"env(safe-area-inset-[a-z]+)", tabletDown);
        Assert.Matches(@"#bottom-nav \.nav-item span\{font-size:10px", tabletDown);
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
        var css = ReadAsset("styles/dialogs.css");

        // minmax(0,1fr), not 1fr: a grid item's default min-width:auto refuses to go below min-content,
        // so 1fr alone would not have fixed it.
        //
        // Scheibe 11 (Design-System-Plan): this used to be its own second `.dialog-card` rule, merged
        // into the one true-base rule with padding/shadow/gap (background/border/radius moved out to
        // the shared card base in components.css, alongside .metric/.panel/.fw-card). The assertion
        // now matches the merged rule instead of the standalone one that no longer exists.
        Assert.Contains("grid-template-columns:minmax(0,1fr)", css);
        Assert.Contains(".dialog-card{", css);
        Assert.Contains(".dialog-card>*{min-width:0}", css);
        Assert.Contains(".dialog-card label{grid-template-columns:minmax(0,1fr);min-width:0}", css);
        Assert.Contains(".dialog-card input,.dialog-card select,.dialog-card textarea{min-width:0;max-width:100%}", css);
    }

    /// <summary>
    /// #171: the FullWorth logo and the "Alpha" badge live only inside `.brand`, itself only inside
    /// `.sidebar` - and `.sidebar` used to be `display:none` below 768px, taking the brand with it with
    /// no mobile equivalent anywhere. Fixed by keeping `.sidebar` as a compact, brand-only strip instead
    /// of introducing a second, mobile-only copy of the logo markup.
    /// </summary>
    [Fact]
    public void MobileHeaderStillShowsTheBrandLogoAndAlphaBadge()
    {
        var css = ReadCss();
        var start = css.IndexOf("@media(max-width:767px)", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var mobile = css[start..];

        // The sidebar must stay a visible, laid-out element (not display:none) so `.brand` inside it
        // can render - only its menu list and user footer are hidden, not the sidebar itself.
        Assert.DoesNotMatch(@"\.sidebar\{[^}]*display:none", mobile);
        Assert.Contains(".sidebar{", mobile);
        Assert.DoesNotContain(".brand{display:none", mobile);
    }

    /// <summary>
    /// Regression found while verifying #171 live: shell.css's `html.nav-collapsed .brand strong` and
    /// `.brand-name` rules hide the name/badge to make the desktop sidebar a narrow icon column - and
    /// that class is set unconditionally from a `localStorage` flag (`app/boot.js`) before first paint,
    /// with no viewport check. A user who ever collapsed the sidebar on desktop carries `nav-collapsed`
    /// into mobile too, which silently re-broke the #171 fix (logo visible, name and "Alpha" badge gone
    /// again) without touching `.sidebar{display:none}` at all - the assertions above alone don't catch
    /// this, because they only look at the plain (non-collapsed) mobile rule.
    /// </summary>
    [Fact]
    public void MobileBrandSurvivesAPersistedDesktopNavCollapsedState()
    {
        var css = ReadCss();
        var start = css.IndexOf("@media(max-width:767px)", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var mobile = css[start..];

        Assert.Matches(@"html\.nav-collapsed \.brand strong,html\.nav-auto-collapsed \.brand strong\{display:(?!none)", mobile);
        Assert.Matches(@"html\.nav-collapsed \.brand-name,html\.nav-auto-collapsed \.brand-name\{display:(?!none)", mobile);
    }

    [Fact]
    public void BottomNavHasExactlyFivePrimaryDestinations()
    {
        var nav = WebSources.BottomNavigation();
        // UI_UX_SPEC §3.2: genau fünf sichtbare Ziele — vier Bereiche und "Mehr".
        //
        // Die vier stehen seit #154 nicht mehr als Markup da, sondern entstehen aus
        // NavigationCatalog.Quick; gezaehlt wird deshalb dort. "Mehr" ist der einzige, der
        // woertlich in der Partial steht, weil er zu keinem Eintrag gehoert.
        Assert.Equal(4, NavigationCatalog.Quick.Count);
        Assert.Contains("id=\"bottom-more\"", nav);
        Assert.Contains("NavigationCatalog.Quick", nav);
    }

    [Fact]
    public void EveryDeclaredActionButtonIsWiredInAppJs()
    {
        var root = RepoRoot();
        var wwwroot = Path.Combine(root, "src", "FullWorth.Web", "wwwroot");
        var html = WebSources.Layout();
        var appJs = WebSources.Asset("app", "shell.js");
        // The frontend is a set of ES modules: app.js orchestrates, feature/ui modules own their screens.
        // A data-action may be wired in app.js OR in the module that owns that screen (e.g. pages/rules/page.js).
        var allJs = appJs + string.Concat(Directory
            .EnumerateFiles(wwwroot, "*.js", SearchOption.AllDirectories)
            .Where(path => !path.EndsWith("app.js", StringComparison.Ordinal))
            .Select(File.ReadAllText));

        // Regression guard: "Add" buttons (budgets/contracts/rules) once existed in the markup with
        // no click handler at all — visibly dead UI. Every data-action must have a JS binding somewhere.
        //
        // Seit #154 liegt das Markup der umgezogenen Seiten nicht mehr in index.html, sondern in
        // ihrer Razor-Seite. Beide Orte gehoeren hier hinein: als index.html die letzte davon
        // abgab, stand die Liste auf null und der Waechter fiel ueber sein eigenes NotEmpty - er
        // haette ab da jeden toten Knopf durchgelassen, ohne dass es jemandem aufgefallen waere.
        var pagesRoot = Path.Combine(root, "src", "FullWorth.Web", "Pages");
        var markup = html + string.Concat(Directory
            .EnumerateFiles(pagesRoot, "*.cshtml", SearchOption.AllDirectories)
            .Select(File.ReadAllText));

        var actions = System.Text.RegularExpressions.Regex.Matches(markup, "data-action=\"([^\"]+)\"")
            .Select(match => match.Groups[1].Value).Distinct().ToList();
        Assert.NotEmpty(actions);
        foreach (var action in actions)
            Assert.Contains($"[data-action=\"{action}\"]", allJs);

        // Every nav view must be routed. loadCurrent (in app.js) used to dispatch through a switch/case;
        // the architecture cleanup replaced that with the shared core/feature-registry.js, so each view
        // war: jede Ansicht ist in app.js registriert. Seit #154 ist jede Ansicht eine eigene Seite,
        // also lautet dieselbe Frage: gibt es zu jedem Menueeintrag eine Razor-Seite? Genau das prueft
        // MenuParityTests.Every_entry_leads_somewhere - hier bliebe nur eine zweite, schwaechere Kopie.
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
            Path.Combine(root, "styles", "app.css"),
            Path.Combine(root, "styles", "responsive.css")
        };
        foreach (var path in paths) Assert.True(File.Exists(path), $"css layer not found: {path}");
        return string.Concat(paths.Select(File.ReadAllText));
    }
}
