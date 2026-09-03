namespace FullWorth.Web.Tests;

public sealed class AppearanceThemeBaselineTests : IClassFixture<FullWorthWebFactory>
{
    private readonly HttpClient _client;

    public AppearanceThemeBaselineTests(FullWorthWebFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task ThemeInit_AppliesVisualThemeAndMascotBeforeAppBoot()
    {
        var init = await GetAsync("/theme-init.js");

        Assert.Contains("finance.visualTheme", init);
        Assert.Contains("finance.mascot", init);
        Assert.Contains("finance.mascotActivity", init);
        Assert.Contains("dataset.visualTheme", init);
        Assert.Contains("dataset.mascot", init);
        Assert.Contains("/appearance.css", init);
        Assert.Contains("/ui/appearance.js", init);
        Assert.Contains("/ui/mascot-scenes.js", init);
        Assert.Contains("bankConnectedAtBoot", init);
        Assert.Contains("first-bank-connected", init);
    }

    [Fact]
    public async Task CuteTheme_IsAnOverrideLayer_NotAParallelApp()
    {
        var css = await GetAsync("/appearance.css");

        Assert.Contains("data-visual-theme=\"cute\"", css);
        Assert.Contains("--radius-card", css);
        Assert.Contains("--surface", css);
        Assert.Contains("--shadow", css);
        Assert.Contains("prefers-reduced-motion: reduce", css);
        Assert.False(css.Contains("CuteCard", StringComparison.OrdinalIgnoreCase));
        Assert.False(css.Contains("CleanCard", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task MascotRegistry_ContainsAllProductMascotsAndSafeFallbacks()
    {
        var js = await GetAsync("/ui/appearance.js");

        foreach (var mascot in Mascots)
        {
            Assert.Contains($"['{mascot}'", js);
        }

        Assert.Contains("CORE_SCENES", js);
        Assert.Contains("BESPOKE_SCENES", js);
        Assert.Contains("createCoreAssetMap", js);
        Assert.Contains("SCENE_FALLBACKS", js);
        Assert.Contains("EMPTY_SCENE_BY_VIEW", js);
        Assert.Contains("resolveMascotScene", js);
        Assert.Contains("renderMascotScene", js);
        // Scenes are framed with a nested <svg viewBox> embedding the sheet (Chromium ignores <img> #view
        // fragments), reading each cell's viewBox from the sprite's own <view> elements.
        Assert.Contains("mascot-slot-sprite", js);
        Assert.Contains("view[id]", js);
        // Rich illustrated portraits are the primary mascot art; sprites/glyph remain as fallbacks.
        Assert.Contains("mascot-slot-art", js);
        Assert.Contains("/mascots/art/", js);
        Assert.Contains("registerMascotAsset", js);
        Assert.Contains("refreshGenericEmptyStates", js);
        Assert.Contains("aria-hidden", js);
    }

    [Fact]
    public async Task MascotSprites_ExposeEveryRequiredBaseScene()
    {
        foreach (var mascot in Mascots)
        {
            var svg = await GetAsync($"/mascots/{mascot}.svg");
            Assert.Contains("<svg", svg);
            Assert.False(svg.Contains("<text", StringComparison.OrdinalIgnoreCase));

            foreach (var scene in BaseScenes)
            {
                Assert.Contains($"<view id=\"{scene}\"", svg);
            }
        }
    }

    [Fact]
    public async Task MascotSprites_RootViewBoxSpansTheFullSheet()
    {
        // The root viewBox must span the entire sprite sheet (0 0 <width> 128), not a single 128px cell.
        // With "0 0 128 128" the <image>-embedded scene framing renders the whole squished sheet instead
        // of the requested cell — a real visual regression that unit tests otherwise cannot see.
        foreach (var mascot in Mascots)
        {
            var svg = await GetAsync($"/mascots/{mascot}.svg");
            var header = svg[..(svg.IndexOf('>') + 1)];

            const string widthToken = "width=\"";
            var wi = header.IndexOf(widthToken, StringComparison.Ordinal);
            Assert.True(wi >= 0, $"{mascot}.svg root has no width attribute");
            var wStart = wi + widthToken.Length;
            var width = header[wStart..header.IndexOf('"', wStart)];

            Assert.Contains($"viewBox=\"0 0 {width} 128\"", header);
        }
    }

    [Fact]
    public async Task BuiltInBespokeScenes_AreRegisteredAndOnlyOverrideMascotsThatActuallyProvideThem()
    {
        var appearance = await GetAsync("/ui/appearance.js");
        Assert.Contains("BESPOKE_SCENES", appearance);

        foreach (var (mascot, scenes) in BuiltInBespokeSceneExpectations)
        {
            var svg = await GetAsync($"/mascots/{mascot}.svg");
            foreach (var scene in scenes)
            {
                Assert.Contains($"<view id=\"{scene}\"", svg);
                Assert.Contains($"'{scene}'", appearance);
            }
        }

        var ghost = await GetAsync("/mascots/ghost.svg");
        Assert.DoesNotContain("id=\"receipt-scanning\"", ghost);
        Assert.DoesNotContain("id=\"house\"", ghost);
        Assert.Contains("'receipt-scanning': ['working', 'idle']", appearance);
        Assert.Contains("house: ['happy', 'idle']", appearance);
    }

    [Fact]
    public async Task ExtendedBespokeScenes_UsePublicRegistrationApiAndExistInSprites()
    {
        var scenesModule = await GetAsync("/ui/mascot-scenes.js");
        Assert.Contains("EXTENDED_BESPOKE_ASSETS", scenesModule);
        Assert.Contains("registerMascotAsset", scenesModule);
        Assert.Contains("registerExtendedBespokeAssets", scenesModule);

        foreach (var (mascot, scenes) in ExtendedBespokeSceneExpectations)
        {
            var svg = await GetAsync($"/mascots/{mascot}.svg");
            foreach (var scene in scenes)
            {
                Assert.Contains($"<view id=\"{scene}\"", svg);
                Assert.Contains($"'{scene}'", scenesModule);
            }
        }
    }

    [Fact]
    public async Task SemanticSceneLayer_ComposesPropsAndTransientMomentsWithoutFeatureThemeBranches()
    {
        var scenes = await GetAsync("/ui/mascot-scenes.js");
        var css = await GetAsync("/appearance.css");

        foreach (var scene in SemanticScenes)
        {
            Assert.Contains($"'{scene}'", scenes);
        }

        Assert.Contains("SCENE_PROPS", scenes);
        Assert.Contains("decorateMascotScene", scenes);
        Assert.Contains("showMascotMoment", scenes);
        Assert.Contains("FullWorthAppearance", scenes);
        Assert.Contains("fullworth:mascot-moment", scenes);
        Assert.Contains("receipt-file", scenes);
        Assert.Contains("data-sync-days", scenes);
        Assert.Contains(".budget-detail", scenes);
        Assert.Contains("[data-property-form]", scenes);
        Assert.Contains("[data-debt-form]", scenes);
        Assert.Contains("armSuccessfulDialogMoment(form, 'house')", scenes);
        Assert.Contains("armSuccessfulDialogMoment(form, 'mortgage')", scenes);
        Assert.Contains("mascotSubmitToken", scenes);
        Assert.False(scenes.Contains("visualTheme === 'cute'", StringComparison.OrdinalIgnoreCase));

        Assert.Contains(".mascot-scene-prop", css);
        Assert.Contains("#mascot-moment", css);
        Assert.Contains("aria-hidden", scenes);
        Assert.Contains("prefers-reduced-motion: reduce", css);
    }

    [Fact]
    public async Task AppearanceSettings_KeepColorModeVisualStyleAndMascotIndependent()
    {
        var app = await GetAsync("/app.js");
        var appearance = await GetAsync("/ui/appearance.js");
        var css = await GetAsync("/appearance.css");

        Assert.Contains("finance.theme", app);
        Assert.Contains("finance.visualTheme", appearance);
        Assert.Contains("visualTheme", appearance);
        Assert.Contains("mascotActivity", appearance);
        Assert.DoesNotContain("finance.visualTheme", app);
        Assert.Contains("const show = appearance.mascot !== 'none'", appearance);
        Assert.Contains("html:not([data-mascot=\"none\"])[data-mascot-activity=\"normal\"]", css);
    }

    [Fact]
    public async Task PwaShell_PrecachesAppearanceAndMascotAssetsWithoutFinanceRoutes()
    {
        var sw = await GetAsync("/sw.js");

        Assert.Contains("'/appearance.css'", sw);
        Assert.Contains("'/parity-completion.css'", sw);
        Assert.Contains("'/ui/appearance.js'", sw);
        Assert.Contains("'/ui/mascot-scenes.js'", sw);
        Assert.Contains("const VERSION = 'v55'", sw);
        Assert.Contains("'/features/wealth-real-estate.js'", sw);
        Assert.Contains("'/features/wealth-real-estate.css'", sw);
        foreach (var mascot in Mascots)
        {
            Assert.Contains($"'/mascots/{mascot}.svg'", sw);
            Assert.Contains($"'/mascots/art/{mascot}.webp'", sw);
        }

        var appShellStart = sw.IndexOf("const APP_SHELL", StringComparison.Ordinal);
        var appShellEnd = sw.IndexOf("];", appShellStart, StringComparison.Ordinal);
        Assert.True(appShellStart >= 0 && appShellEnd > appShellStart);
        var shell = sw[appShellStart..appShellEnd];
        Assert.False(shell.Contains("/bff/", StringComparison.OrdinalIgnoreCase));
        Assert.False(shell.Contains("/api/", StringComparison.OrdinalIgnoreCase));
        Assert.False(shell.Contains("/auth/", StringComparison.OrdinalIgnoreCase));
    }

    private static readonly string[] Mascots =
    [
        "lion", "duck", "elephant", "penguin", "raccoon", "tree", "ghost", "vault"
    ];

    private static readonly string[] BaseScenes =
    [
        "idle", "happy", "working", "warning", "celebrate", "empty"
    ];

    private static readonly string[] SemanticScenes =
    [
        "receipt-scanning", "budget-success", "budget-warning", "goal-reached",
        "investment-growth", "portfolio-growth", "house", "mortgage", "first-bank-connected", "amazon-import"
    ];

    private static readonly Dictionary<string, string[]> BuiltInBespokeSceneExpectations = new()
    {
        ["raccoon"] = ["receipt-scanning", "amazon-import"],
        ["duck"] = ["receipt-scanning"],
        ["penguin"] = ["receipt-scanning", "budget-success", "budget-warning"],
        ["lion"] = ["budget-success", "budget-warning", "goal-reached", "investment-growth"],
        ["elephant"] = ["goal-reached", "house"],
        ["tree"] = ["goal-reached", "investment-growth", "house"]
    };

    private static readonly Dictionary<string, string[]> ExtendedBespokeSceneExpectations = new()
    {
        ["vault"] = ["first-bank-connected", "mortgage"],
        ["lion"] = ["first-bank-connected", "portfolio-growth"],
        ["elephant"] = ["mortgage"],
        ["tree"] = ["portfolio-growth"]
    };

    [Fact]
    public void FeatureModules_NeverBranchOnCuteOrReferenceMascotAssetPaths()
    {
        var featuresDir = Path.Combine(WwwrootDir(), "features");
        Assert.True(Directory.Exists(featuresDir), $"features directory not found: {featuresDir}");

        // The layering contract: feature enhancers stay Cute-agnostic. They must not branch on the visual
        // theme or hard-code a concrete mascot asset path; special moments go through semantic scenes only.
        string[] forbidden = ["visualTheme", "data-visual-theme", "/mascots/", "data-mascot"];
        var violations = new List<string>();
        foreach (var file in Directory.EnumerateFiles(featuresDir, "*.js"))
        {
            var text = File.ReadAllText(file);
            foreach (var marker in forbidden)
            {
                if (text.Contains(marker, StringComparison.Ordinal))
                    violations.Add($"{Path.GetFileName(file)} references '{marker}'");
            }
        }

        Assert.True(
            violations.Count == 0,
            "Feature modules must request semantic scenes/tokens only, never Cute branches or mascot asset paths:\n"
                + string.Join("\n", violations));
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
