namespace FullWorth.Web.Tests;

public sealed class AppearanceThemeBaselineTests : IClassFixture<FullWorthWebFactory>
{
    private readonly HttpClient _client;

    public AppearanceThemeBaselineTests(FullWorthWebFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task ThemeInit_AppliesVisualThemeBeforeAppBoot()
    {
        var init = await GetAsync("/theme-init.js");

        Assert.Contains("finance.visualTheme", init);
        Assert.Contains("dataset.visualTheme", init);
        Assert.Contains("/appearance.css", init);
        Assert.Contains("/ui/appearance.js", init);
        Assert.False(init.Contains("mascot", StringComparison.OrdinalIgnoreCase));
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
        Assert.False(css.Contains("mascot", StringComparison.OrdinalIgnoreCase));
        Assert.False(css.Contains("CuteCard", StringComparison.OrdinalIgnoreCase));
        Assert.False(css.Contains("CleanCard", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AppearanceSettings_KeepColorModeAndVisualStyleIndependent()
    {
        var app = await GetAsync("/app.js");
        var appearance = await GetAsync("/ui/appearance.js");

        Assert.Contains("finance.theme", app);
        Assert.Contains("finance.visualTheme", appearance);
        Assert.Contains("visualTheme", appearance);
        Assert.DoesNotContain("finance.visualTheme", app);
        Assert.False(appearance.Contains("mascot", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PwaShell_PrecachesAppearanceWithoutMascotAssetsOrSensitiveRoutes()
    {
        var sw = await GetAsync("/sw.js");

        Assert.Contains("'/appearance.css'", sw);
        Assert.Contains("'/parity-completion.css'", sw);
        Assert.Contains("'/ui/appearance.js'", sw);
        Assert.Matches(@"const\s+VERSION\s*=\s*'v\d+'", sw);
        Assert.Contains("'/features/wealth-real-estate.js'", sw);
        Assert.Contains("'/features/wealth-real-estate.css'", sw);
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
    public void FeatureModules_NeverBranchOnVisualTheme()
    {
        var featuresDir = Path.Combine(WwwrootDir(), "features");
        Assert.True(Directory.Exists(featuresDir), $"features directory not found: {featuresDir}");

        string[] forbidden = ["visualTheme", "data-visual-theme"];
        var violations = new List<string>();
        foreach (var file in Directory.EnumerateFiles(featuresDir, "*.js"))
        {
            var source = File.ReadAllText(file);
            foreach (var marker in forbidden)
            {
                if (source.Contains(marker, StringComparison.Ordinal))
                    violations.Add($"{Path.GetFileName(file)} references '{marker}'");
            }
        }

        Assert.True(
            violations.Count == 0,
            "Feature modules must remain independent of the visual theme:\n"
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
