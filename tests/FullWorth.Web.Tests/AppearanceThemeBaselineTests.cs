namespace FullWorth.Web.Tests;

/// <summary>
/// The "cute" visual theme is gone. It was a second look layered over the whole app - topbar, sidebar,
/// panels, progress bars, budget cards - so every new feature had to be checked twice and the design
/// drifted in two directions at once. It is replaced by two user-chosen brand colours: same room for
/// taste, one design to maintain. These tests pin the replacement and the fact that nothing branches
/// on a visual theme any more.
/// </summary>
public sealed class AppearanceThemeBaselineTests : IClassFixture<FullWorthWebFactory>
{
    private readonly HttpClient _client;

    public AppearanceThemeBaselineTests(FullWorthWebFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task ThemeInit_AppliesTheBrandColoursBeforeAppBoot()
    {
        var init = await GetAsync("/theme-init.js");
        // From disk: an unauthenticated request for /index.html gets the auth shell, not the app shell.
        var head = File.ReadAllText(Path.Combine(WwwrootDir(), "index.html"));

        // Applied pre-paint for the same reason the light/dark theme is: doing it after boot means a
        // visible flash of the default colour on every page load.
        Assert.Contains("finance.color.primary", init);
        Assert.Contains("finance.color.secondary", init);
        Assert.Contains("--cta", init);
        Assert.Contains("--accent", init);
        // A light primary must not get white text on it, so contrast is derived here too.
        Assert.Contains("0.2126", init);
        Assert.Contains("/ui/appearance.js", init);
        Assert.Contains("/appearance.css", head);
    }

    [Fact]
    public async Task CuteThemeIsGoneEverywhere()
    {
        var css = await GetAsync("/appearance.css");
        var appearance = await GetAsync("/ui/appearance.js");
        var init = await GetAsync("/theme-init.js");

        foreach (var source in new[] { css, appearance, init })
        {
            // Comments are prose about what was removed and have to be stripped, or the guard trips
            // over its own explanation.
            var code = System.Text.RegularExpressions.Regex.Replace(source, @"/\*.*?\*/", string.Empty,
                System.Text.RegularExpressions.RegexOptions.Singleline);
            code = System.Text.RegularExpressions.Regex.Replace(code, @"^\s*//.*$", string.Empty,
                System.Text.RegularExpressions.RegexOptions.Multiline);
            Assert.DoesNotContain("data-visual-theme", code, StringComparison.Ordinal);
            Assert.DoesNotContain("visualTheme", code, StringComparison.Ordinal);
            Assert.DoesNotContain("cute", code, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task AppearanceLayerOnlyDefinesProperties()
    {
        var css = await GetAsync("/appearance.css");

        // appearance.css loads third in the chain (tokens -> reset -> appearance -> shell ->
        // components -> app.css -> responsive), so it can never win a rule against app.css. If it ever
        // starts declaring real rules again they will silently lose, exactly as the cute layer did in
        // the places app.css was more specific.
        Assert.Contains("--brand-primary", css);
        Assert.Contains("--brand-secondary", css);
        var declarations = System.Text.RegularExpressions.Regex.Replace(css, @"/\*.*?\*/", string.Empty,
            System.Text.RegularExpressions.RegexOptions.Singleline);
        foreach (var block in System.Text.RegularExpressions.Regex.Matches(declarations, @"\{([^}]*)\}")
                     .Select(match => match.Groups[1].Value))
        {
            foreach (var property in block.Split(';', StringSplitOptions.RemoveEmptyEntries)
                         .Select(entry => entry.Trim()).Where(entry => entry.Length > 0))
            {
                Assert.StartsWith("--", property, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public async Task ColourPickerDrivesTheTokensTheAppAlreadyReads()
    {
        var appearance = await GetAsync("/ui/appearance.js");
        var css = await GetAsync("/app.css");

        // primary -> --cta (buttons), secondary -> --accent (links, focus rings, chart strokes).
        // Writing anything else would give a picker that visibly does nothing.
        Assert.Contains("set('--cta', appearance.primary)", appearance);
        Assert.Contains("set('--accent', appearance.secondary)", appearance);
        Assert.Contains("readableTextOn", appearance);
        // Empty means "the token decides", which is not the same as writing the default value: it has
        // to hand control back so light and dark keep their own values.
        Assert.Contains("removeProperty", appearance);

        Assert.Contains("BRAND_PRESETS", appearance);
        Assert.Contains("finance.color.tintLogo", appearance);
        // The mark is recoloured by swapping the source; a CSS mask would flatten its three bars.
        Assert.Contains("tintBrandMark", appearance);
        Assert.DoesNotContain("mask-image", css.Substring(css.IndexOf(".appearance-colors", StringComparison.Ordinal)));
        Assert.Contains(".appearance-preset", css);
    }

    [Fact]
    public async Task AppearanceSettings_KeepColorModeAndBrandColoursIndependent()
    {
        var app = await GetAsync("/app.js");
        var appearance = await GetAsync("/ui/appearance.js");

        Assert.Contains("finance.theme", app);
        Assert.Contains("finance.color.primary", appearance);
        Assert.DoesNotContain("finance.color.primary", app);
        Assert.False(appearance.Contains("mascot", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PwaShell_PrecachesAppearanceWithoutMascotAssetsOrSensitiveRoutes()
    {
        var sw = await GetAsync("/sw.js");

        Assert.Contains("'/appearance.css'", sw);
        Assert.Contains("'/styles/tokens.css'", sw);
        Assert.Contains("'/styles/reset.css'", sw);
        Assert.Contains("'/styles/shell.css'", sw);
        Assert.Contains("'/styles/components.css'", sw);
        Assert.DoesNotContain("/parity-completion.css", sw);
        Assert.Contains("'/ui/appearance.js'", sw);
        Assert.Matches(@"const\s+VERSION\s*=\s*'v\d+'", sw);
        Assert.Contains("'/features/wealth-real-estate.js'", sw);
        Assert.Contains("'/styles/features/wealth-real-estate.css'", sw);
        Assert.False(sw.Contains("/mascots/", StringComparison.OrdinalIgnoreCase));
        Assert.False(sw.Contains("mascot-scenes", StringComparison.OrdinalIgnoreCase));

        var appShellStart = sw.IndexOf("const APP_SHELL", StringComparison.Ordinal);
        var appShellEnd = sw.IndexOf("];", appShellStart, StringComparison.Ordinal);
        Assert.True(appShellStart >= 0 && appShellEnd > appShellStart);
        var shell = sw[appShellStart..appShellEnd];
        Assert.False(shell.Contains("/bff/", StringComparison.OrdinalIgnoreCase));
        Assert.False(shell.Contains("/api/", StringComparison.OrdinalIgnoreCase));
        Assert.False(shell.Contains("/auth/", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void NothingInTheFrontendBranchesOnAVisualTheme()
    {
        var root = WwwrootDir();
        var violations = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*.js", SearchOption.AllDirectories)
                     .Concat(Directory.EnumerateFiles(root, "*.css", SearchOption.AllDirectories)))
        {
            var source = File.ReadAllText(file);
            foreach (var marker in new[] { "visualTheme", "data-visual-theme" })
            {
                if (source.Contains(marker, StringComparison.Ordinal))
                    violations.Add($"{Path.GetRelativePath(root, file)} references '{marker}'");
            }
        }

        Assert.True(
            violations.Count == 0,
            "The visual-theme switch is gone; nothing may branch on it again:\n" + string.Join("\n", violations));
    }

    private static string WwwrootDir()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "FullWorth.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, "src", "FullWorth.Web", "wwwroot");
    }

    private async Task<string> GetAsync(string path)
    {
        using var response = await _client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }
}
