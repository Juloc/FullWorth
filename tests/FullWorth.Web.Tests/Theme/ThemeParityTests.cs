using System.Text.RegularExpressions;

namespace FullWorth.Web.Tests.Theme;

/// <summary>
/// Release-readiness guard (Wave N2): every theme CSS custom property must be defined in BOTH the
/// light (:root) and dark (html[data-theme="dark"]) palettes, so a color can never fall back to an
/// unthemed value in one mode. Pure file check — no server/DB.
/// </summary>
public sealed class ThemeParityTests
{
    [Fact]
    public void LightAndDarkPalettesDefineTheSameVariables()
    {
        var css = File.ReadAllText(AppCss());
        var light = Variables(css, ":root{");
        var dark = Variables(css, "html[data-theme=\"dark\"]{");

        Assert.NotEmpty(light);
        Assert.NotEmpty(dark);

        // Every SEMANTIC COLOR token must exist in both palettes so a color can never fall back to an
        // unthemed value in one mode. Layout tokens (spacing/radii/widths, UI_UX_SPEC §4.1) are
        // theme-independent and intentionally live only in :root, so they are excluded from parity.
        var semanticColors = new[] { "bg", "surface", "surface-2", "text", "muted", "line", "accent", "accent-soft", "positive", "negative", "warning" };
        foreach (var token in semanticColors)
        {
            Assert.Contains(token, light);
            Assert.Contains(token, dark);
        }

        // No color token may exist in dark without a light definition.
        var colorLike = dark.Where(v => !light.Contains(v)).ToList();
        Assert.True(colorLike.Count == 0, "Tokens in dark but not light: " + string.Join(", ", colorLike));
    }

    private static SortedSet<string> Variables(string css, string selectorOpen)
    {
        var start = css.IndexOf(selectorOpen, StringComparison.Ordinal);
        Assert.True(start >= 0, $"selector not found: {selectorOpen}");
        start += selectorOpen.Length;
        var end = css.IndexOf('}', start);           // a CSS declaration block has no nested braces
        Assert.True(end > start, "unterminated block");
        var block = css[start..end];
        var names = Regex.Matches(block, @"--([a-z0-9-]+)\s*:", RegexOptions.None, TimeSpan.FromSeconds(1))
            .Select(match => match.Groups[1].Value);
        return new SortedSet<string>(names, StringComparer.Ordinal);
    }

    private static string AppCss()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FullWorth.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var path = Path.Combine(dir!.FullName, "src", "FullWorth.Web", "wwwroot", "app.css");
        Assert.True(File.Exists(path), $"app.css not found: {path}");
        return path;
    }
}
