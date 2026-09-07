namespace FullWorth.Web.Tests.Theme;

public sealed class TypographyAppearanceTests
{
    [Fact]
    public void AppCssUsesSharedTypographyTokensAndFontPresets()
    {
        var css = File.ReadAllText(WebRootFile("app.css"));

        Assert.Contains("--font-size-base:13px", css);
        Assert.Contains("--font-weight-base:400", css);
        Assert.Contains("--letter-spacing-base:0em", css);
        Assert.Contains("--line-height-base:1.5", css);

        Assert.Contains("font-size:var(--font-size-base)", css);
        Assert.Contains("font-weight:var(--font-weight-base)", css);
        Assert.Contains("line-height:var(--line-height-base)", css);
        Assert.Contains("letter-spacing:var(--letter-spacing-base)", css);

        Assert.Contains("html[data-font=\"fredoka\"]", css);
        Assert.Contains("html[data-font=\"system\"]", css);
        Assert.Contains("html[data-font=\"comic\"]", css);
        Assert.Contains("html[data-font=\"mono\"]", css);
    }

    [Fact]
    public void AppearanceModulePersistsAllTypographyControls()
    {
        var script = File.ReadAllText(WebRootFile("ui", "appearance.js"));

        Assert.Contains("finance.typography.baseSize", script);
        Assert.Contains("finance.typography.weight", script);
        Assert.Contains("finance.typography.letterSpacing", script);
        Assert.Contains("finance.typography.lineHeight", script);
        Assert.Contains("data-typography-input", script);
        Assert.Contains("Comic Sans MS", script);
        Assert.Contains("Barlow Condensed", script);
    }

    [Fact]
    public void ThemeInitRestoresTypographyBeforePaint()
    {
        var script = File.ReadAllText(WebRootFile("theme-init.js"));

        Assert.Contains("applyStoredTypography()", script);
        Assert.Contains("finance.typography.baseSize", script);
        Assert.Contains("finance.typography.weight", script);
        Assert.Contains("finance.typography.letterSpacing", script);
        Assert.Contains("finance.typography.lineHeight", script);
    }

    private static string WebRootFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FullWorth.slnx")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return Path.Combine(new[] { dir!.FullName, "src", "FullWorth.Web", "wwwroot" }.Concat(parts).ToArray());
    }
}
