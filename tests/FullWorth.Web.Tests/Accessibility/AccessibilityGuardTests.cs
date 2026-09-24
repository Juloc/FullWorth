using System.IO;

namespace FullWorth.Web.Tests.Accessibility;

// Locks the statically-checkable UI_UX_SPEC §25 accessibility invariants so they don't regress:
// skip link, visible focus, reduced-motion, aria-current on BOTH navs, <html lang> tracking the
// locale, and a text alternative (role="img") on every chart-rendering module.
public sealed class AccessibilityGuardTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FullWorth.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string Www(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { Root(), "src", "FullWorth.Web", "wwwroot" }.Concat(parts).ToArray()));

    [Fact]
    public void SkipLinkAndLandmarksExist()
    {
        var html = WebSources.Layout();
        Assert.Contains("class=\"skip-link\"", html);
        Assert.Contains("href=\"#main\"", html);
        Assert.Contains("id=\"main\"", html);
    }

    [Fact]
    public void FocusVisibleAndReducedMotionStylesExist()
    {
        // Checked against every stylesheet the app shell actually loads, not a hand-picked four: the
        // reduced-motion handling lives in appearance.css, design-depth.css and styles/responsive.css,
        // so naming individual files made the guard depend on where the rules happen to sit today.
        var html = WebSources.Layout();
        var hrefs = System.Text.RegularExpressions.Regex
            .Matches(html, "<link[^>]+rel=\"stylesheet\"[^>]+href=\"/([^\"]+\\.css)\"")
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(hrefs);
        var css = string.Concat(hrefs.Select(href => Www(href.Split('/'))));

        Assert.Contains(":focus-visible", css);
        Assert.Contains("prefers-reduced-motion", css);
    }

    [Fact]
    public void BothNavsSetAriaCurrent()
    {
        // Seitenleiste und untere Leiste markieren beide das aktive Ziel. Seit #154 tut das der
        // Server, bevor das Markup entsteht - vorher setzte app.js es nach dem ersten Bild, und mit
        // der alten Huelle waere es ersatzlos verschwunden (UI_UX_SPEC §25).
        Assert.Contains("aria-current", WebSources.Navigation());
        Assert.Contains("aria-current", WebSources.BottomNavigation());

        // Und beide zeichnen ueberhaupt Eintraege - die erzeugten Abschnitte in index.html, an denen
        // das frueher haftete, gibt es nicht mehr.
        Assert.Contains("nav-item", WebSources.Navigation());
        Assert.Contains("nav-item", WebSources.BottomNavigation());

        // <html lang> tracking now lives in the shared i18n module (core/i18n.js) rather than app.js
        // directly; the architecture cleanup extracted locale handling out of app.js.
        var i18n = Www("core", "i18n.js");
        Assert.Contains("document.documentElement.lang", i18n);
    }

    [Fact]
    public void ChartModulesProvideTextAlternative()
    {
        // Every SVG chart must carry role="img" + an aria-label (§25: chart meaning not by color alone).
        foreach (var module in new[] { "pages/analytics/page.js", "pages/networth/page.js", "pages/networth/loans.js", "pages/contracts/page.js" })
        {
            var js = Www(module.Split('/'));
            Assert.Contains("role=\"img\"", js);
            Assert.Contains("aria-label", js);
        }
    }
}
