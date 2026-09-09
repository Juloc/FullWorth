using System.IO;
using System.Text.RegularExpressions;

namespace FullWorth.Web.Tests.Frontend;

/// <summary>
/// /admin is a separate shell on purpose - no sidebar, no bottom nav, no finance data - but it is not
/// a separate product. It used to carry its own ~40 hardcoded hex colours, its own
/// prefers-color-scheme dark mode (which overrode the theme the user picked in the app), its own
/// button and dialog styling, and a single 850px breakpoint. That is how a page drifts: nothing
/// fails, it just slowly stops looking and behaving like the app.
/// </summary>
public sealed class AdminPageDesignSystemGuardTests
{
    private static readonly string[] RequiredChain =
    [
        "/styles/tokens.css",
        "/styles/reset.css",
        "/appearance.css",
        "/styles/shell.css",
        "/styles/components.css",
        "/app.css",
        "/styles/responsive.css",
        "/dialogs.css"
    ];

    [Fact]
    public void AdminPageLoadsTheSharedLayerChainAndTheStoredTheme()
    {
        var html = File.ReadAllText(Path.Combine(Root(), "src", "FullWorth.Web", "wwwroot", "admin", "index.html"));

        var loaded = Regex.Matches(html, "<link[^>]+rel=\"stylesheet\"[^>]+href=\"([^\"]+)\"")
            .Select(match => match.Groups[1].Value)
            .ToArray();
        foreach (var sheet in RequiredChain) Assert.Contains(sheet, loaded, StringComparer.Ordinal);

        Assert.True(
            Array.IndexOf(loaded, "/app.css") < Array.IndexOf(loaded, "/styles/responsive.css"),
            "responsive.css must come after app.css or its mobile rules cannot win.");

        // Without theme-init.js the page falls back to the OS preference, so a user on a light theme
        // with a dark OS got a dark admin page.
        Assert.Contains("/theme-init.js", html);
    }

    [Fact]
    public void AdminStylesUseTokensAndTheSharedButtonRoles()
    {
        // Comments are prose about the old state ("used to carry hardcoded colours"), not rules, so
        // they are stripped before asserting - otherwise the guard fails on its own explanation.
        var css = Regex.Replace(
            File.ReadAllText(Path.Combine(Root(), "src", "FullWorth.Web", "wwwroot", "admin", "admin.css")),
            @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        var js = File.ReadAllText(Path.Combine(Root(), "src", "FullWorth.Web", "wwwroot", "admin", "admin.js"));
        var html = File.ReadAllText(Path.Combine(Root(), "src", "FullWorth.Web", "wwwroot", "admin", "index.html"));

        // Tokens only. Hex literals are what made the page diverge from the app's palette, and they
        // are also what made a second dark mode necessary.
        var hexColours = Regex.Matches(css, "#[0-9a-fA-F]{3,8}\\b").Select(match => match.Value).ToArray();
        Assert.True(hexColours.Length == 0,
            $"admin.css must use design tokens, found hardcoded colours: {string.Join(", ", hexColours)}");
        Assert.DoesNotContain("prefers-color-scheme", css);

        // Buttons come from the shared roles; the page must not hand-roll them again.
        foreach (var role in new[] { "btn btn-secondary", "btn btn-danger" }) Assert.Contains(role, js);
        Assert.Contains("btn btn-secondary", html);

        // The user detail goes through the shared dialog, which is what gives it the full-screen
        // phone treatment and the generated close button.
        Assert.Contains("createDialog(", js);
        Assert.DoesNotContain("<dialog", html);
    }

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FullWorth.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
