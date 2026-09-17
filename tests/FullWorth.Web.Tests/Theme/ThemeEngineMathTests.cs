namespace FullWorth.Web.Tests.Theme;

/// <summary>
/// Static, file-level guards for app/theme.js that need no browser: which properties it may touch and
/// which it must never touch. The actual colour MATH (whether the numbers coming out are right) is
/// verified in <see cref="ThemeEngineDerivationTests"/> instead, by running the real engine in a real
/// browser - a string search cannot tell a correct OKLCH conversion from a wrong one.
/// </summary>
public sealed class ThemeEngineMathTests : IClassFixture<FullWorthWebFactory>
{
    private readonly HttpClient _client;

    public ThemeEngineMathTests(FullWorthWebFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task TheEngineNeverTouchesTheSemanticColourTokens()
    {
        var themeEngine = await GetAsync("/app/theme.js");

        // Danger/Warning/Success/Info are a separate slice (issue #149 §6) and must stay exactly what
        // tokens.css defines regardless of the seed - only accent/neutral/data/logo are seed-derived.
        Assert.DoesNotContain("--danger", themeEngine);
        Assert.DoesNotContain("--warning", themeEngine);
        Assert.DoesNotContain("--success", themeEngine);
        Assert.DoesNotContain("--info", themeEngine);
        Assert.DoesNotContain("--positive", themeEngine);
        Assert.DoesNotContain("--negative", themeEngine);
    }

    private async Task<string> GetAsync(string path)
    {
        using var response = await _client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }
}
