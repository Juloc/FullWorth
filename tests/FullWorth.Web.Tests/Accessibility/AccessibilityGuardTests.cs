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
        var html = Www("index.html");
        Assert.Contains("class=\"skip-link\"", html);
        Assert.Contains("href=\"#main\"", html);
        Assert.Contains("id=\"main\"", html);
    }

    [Fact]
    public void FocusVisibleAndReducedMotionStylesExist()
    {
        var css = Www("app.css");
        Assert.Contains(":focus-visible", css);
        Assert.Contains("prefers-reduced-motion", css);
    }

    [Fact]
    public void BothNavsSetAriaCurrent()
    {
        var js = Www("app.js");
        // Desktop sidebar and mobile bottom-nav must both mark the active destination for AT users.
        var occurrences = System.Text.RegularExpressions.Regex.Matches(js, "aria-current").Count;
        Assert.True(occurrences >= 2, "aria-current must be set on both the desktop and bottom navigation.");
        Assert.Contains("#bottom-nav button[data-view]", js);
        Assert.Contains("document.documentElement.lang", js);
    }

    [Fact]
    public void ChartModulesProvideTextAlternative()
    {
        // Every SVG chart must carry role="img" + an aria-label (§25: chart meaning not by color alone).
        foreach (var module in new[] { "features/analytics.js", "features/networth.js", "features/loans.js", "features/contracts.js" })
        {
            var js = Www(module.Split('/'));
            Assert.Contains("role=\"img\"", js);
            Assert.Contains("aria-label", js);
        }
    }
}
