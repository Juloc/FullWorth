using FullWorth.Web.Tests.Pwa;
namespace FullWorth.Web.Tests;

/// <summary>
/// The "cute" visual theme is gone. It was a second look layered over the whole app - topbar, sidebar,
/// panels, progress bars, budget cards - so every new feature had to be checked twice and the design
/// drifted in two directions at once. It is replaced by ONE user-chosen seed colour that an OKLCH
/// engine (app/theme.js) turns into the whole accent/neutral/data palette: same room for taste, one
/// design AND one colour engine to maintain, instead of four copies of contrast/mix math spread across
/// boot.js, appearance.js, app.js and auth/auth.js. These tests pin the replacement and the fact that
/// nothing branches on a visual theme any more.
/// </summary>
public sealed class AppearanceThemeBaselineTests : IClassFixture<FullWorthWebFactory>
{
    private readonly HttpClient _client;

    public AppearanceThemeBaselineTests(FullWorthWebFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task ThemeInit_AppliesTheSeedDerivedColoursBeforeAppBoot()
    {
        var init = await GetAsync("/app/boot.js");
        var themeEngine = await GetAsync("/app/theme.js");
        // From disk: an unauthenticated request for /index.html gets the auth shell, not the app shell.
        var head = WebSources.Layout();

        // boot.js no longer carries the colour math itself - it calls into the one shared engine, which
        // it can only do synchronously because that engine is loaded as a classic (non-module) <script>
        // right before it. A <script type="module"> would not block parsing the way a classic <script>
        // does, so the module would run AFTER the document (and possibly the first paint) - exactly the
        // flash-of-default-colour this file exists to prevent.
        Assert.Contains("window.FullWorthTheme.readThemeState()", init);
        Assert.Contains("window.FullWorthTheme.applyTheme(", init);
        Assert.Contains("/app/appearance.js", init);
        // "~/" loest ASP.NET zur Adresse mit Fingerabdruck auf (#154); ein klassisches <script> bleibt es.
        Assert.Contains("<script src=\"~/app/theme.js\"></script>", head);
        Assert.Contains("<script src=\"~/app/boot.js\"></script>", head);
        Assert.True(
            head.IndexOf("/app/theme.js", StringComparison.Ordinal) < head.IndexOf("/app/boot.js", StringComparison.Ordinal),
            "theme.js must load BEFORE boot.js, or boot.js's synchronous call has nothing to call into yet.");
        Assert.Contains("/styles/appearance.css", head);

        // The math itself - the persisted seed, the two derived buttons/link tokens, and the WCAG
        // contrast formula - lives in exactly one place now: the engine, not boot.js.
        Assert.Contains("finance.themeSeed", themeEngine);
        Assert.Contains("--cta", themeEngine);
        Assert.Contains("--accent", themeEngine);
        Assert.Contains("0.2126", themeEngine);
    }

    [Fact]
    public async Task CuteThemeIsGoneEverywhere()
    {
        var css = await GetAsync("/styles/appearance.css");
        var appearance = await GetAsync("/app/appearance.js");
        var themeEngine = await GetAsync("/app/theme.js");
        var init = await GetAsync("/app/boot.js");

        foreach (var source in new[] { css, appearance, themeEngine, init })
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
        var css = await GetAsync("/styles/appearance.css");

        // appearance.css loads third in the chain (tokens -> reset -> appearance -> shell ->
        // components -> app.css -> responsive), so it can never win a rule against app.css. If it ever
        // starts declaring real rules again they will silently lose, exactly as the cute layer did in
        // the places app.css was more specific.
        //
        // Only --brand-primary remains here (the raw seed, needed for the --tint-* mixes below it) -
        // --brand-secondary is gone with the second colour it used to hold (issue #149 §3: one seed now
        // drives the whole accent scale, so there is nothing left for a second brand token to carry).
        Assert.Contains("--brand-primary", css);
        Assert.DoesNotContain("--brand-secondary", css);
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
        var themeEngine = await GetAsync("/app/theme.js");
        var appearance = await GetAsync("/app/appearance.js");
        var css = await GetAsync("/pages/settings/page.css");

        // seed -> --cta (buttons) and --accent (links, focus rings, chart strokes) both, since one seed
        // now drives the whole scale instead of two independently picked colours. Writing anything else
        // would give a picker that visibly does nothing.
        Assert.Contains("root.style.setProperty('--cta', accent.solid)", themeEngine);
        Assert.Contains("root.style.setProperty('--accent', accent.solid)", themeEngine);
        Assert.Contains("onColor", themeEngine);
        // Empty means "the token decides", which is not the same as writing the default value: it has
        // to hand control back so light and dark keep their own values.
        Assert.Contains("removeProperty", themeEngine);
        // The mark is recoloured by swapping the source; a CSS mask would flatten its three bars.
        Assert.Contains("tintBrandMark", themeEngine);

        // BRAND_PRESETS (mono/ink/forest/plum/copper) was the old five-entry list; issue #149 §8
        // replaced it with six literal quick-pick seeds (white/grey/black/blue/purple/green), and the
        // rename to APPEARANCE_PRESETS makes the constant's new meaning ("plain seed values, no brand
        // naming") visible at the call site rather than pinning the old name for its own sake.
        Assert.Contains("APPEARANCE_PRESETS", appearance);
        // The settings panel no longer touches storage keys directly - it only ever calls into the
        // engine (readThemeState/writeThemeState/applyTheme), so the legacy key string does not need to
        // live here any more; it lives exactly once, inside the engine's own migration.
        Assert.DoesNotContain("finance.color.tintLogo", appearance);

        Assert.DoesNotContain("mask-image", css.Substring(css.IndexOf(".appearance-panel", StringComparison.Ordinal)));
        Assert.Contains(".appearance-preset", css);
    }

    [Fact]
    public async Task AppearanceSettings_KeepColorModeAndTheSeedIndependent()
    {
        var app = await GetAsync("/app/shell.js");
        var appearance = await GetAsync("/app/appearance.js");
        var themeEngine = await GetAsync("/app/theme.js");

        // The engine is the one place that owns every persistence key now - mode AND seed AND logo
        // mode - which is the whole point of consolidating four copies into one. What stays independent
        // is that app.js (color MODE, a light/dark/system choice) never reaches for the seed key, and
        // appearance.js (the seed picker) never hardcodes either raw storage key - both only ever call
        // into the engine's functions.
        Assert.Contains("finance.theme", themeEngine);
        Assert.Contains("finance.themeSeed", themeEngine);
        Assert.DoesNotContain("finance.themeSeed", app);
        Assert.DoesNotContain("finance.color.primary", app);
        Assert.DoesNotContain("finance.theme'", appearance);
        Assert.DoesNotContain("finance.themeSeed", appearance);
        Assert.False(appearance.Contains("mascot", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DerivedColoursNeverReachStorageOnlyTheThreeCanonicalKeysDo()
    {
        var themeEngine = await GetAsync("/app/theme.js");

        // The three canonical keys (issue #149 §12) - nothing derived (an accent shade, a neutral tint,
        // a chart colour) ever gets its own localStorage entry; every one of them is recomputed from the
        // seed on every load instead.
        Assert.Contains("finance.theme", themeEngine);
        Assert.Contains("finance.themeSeed", themeEngine);
        Assert.Contains("finance.logoMode", themeEngine);

        // The legacy keys are READ once (to migrate) and then deleted - never written back to.
        Assert.Contains("finance.color.primary", themeEngine);
        Assert.Contains("finance.color.secondary", themeEngine);
        Assert.Contains("finance.color.tintLogo", themeEngine);

        var writeBody = FunctionBody(themeEngine, "function writeThemeState");
        Assert.Contains("STORAGE.mode", writeBody);
        Assert.Contains("STORAGE.seed", writeBody);
        Assert.Contains("STORAGE.logoMode", writeBody);
        Assert.DoesNotContain("LEGACY_STORAGE", writeBody);

        var migrateBody = FunctionBody(themeEngine, "function migrateLegacyState");
        Assert.Contains("localStorage.removeItem(key)", migrateBody);
        Assert.DoesNotContain("localStorage.setItem(LEGACY_STORAGE", migrateBody);
    }

    [Fact]
    public async Task PwaShell_PrecachesAppearanceWithoutMascotAssetsOrSensitiveRoutes()
    {
        var sw = await GetAsync("/sw.js");

        Assert.Contains("'/styles/appearance.css'", sw);
        Assert.Contains("'/styles/tokens.css'", sw);
        Assert.Contains("'/styles/reset.css'", sw);
        Assert.Contains("'/styles/shell.css'", sw);
        Assert.Contains("'/styles/components.css'", sw);
        Assert.DoesNotContain("/parity-completion.css", sw);
        Assert.Contains("'/app/theme.js'", sw);
        Assert.Contains("'/app/appearance.js'", sw);
        Assert.Matches(@"const\s+VERSION\s*=\s*'v\d+'", sw);
        PwaAssert.Ships("'/pages/networth/real-estate.js'", sw);
        PwaAssert.Ships("'/pages/networth/page.css'", sw);
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

    /// <summary>
    /// The body of the first top-level function named <paramref name="signature"/> found in
    /// <paramref name="source"/>, from its opening brace to its matching closing brace. Good enough for
    /// theme.js, which never nests a function this deep inside another of the same name.
    /// </summary>
    private static string FunctionBody(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"function not found: {signature}");
        var braceStart = source.IndexOf('{', start);
        Assert.True(braceStart > start, $"unterminated function: {signature}");

        var depth = 0;
        for (var index = braceStart; index < source.Length; index++)
        {
            if (source[index] == '{') depth++;
            else if (source[index] == '}')
            {
                depth--;
                if (depth == 0) return source[braceStart..(index + 1)];
            }
        }

        throw new InvalidOperationException($"unterminated function: {signature}");
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
