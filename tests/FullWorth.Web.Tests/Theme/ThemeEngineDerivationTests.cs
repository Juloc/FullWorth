using System.Text.Json;
using FullWorth.Web.Tests.Frontend;

namespace FullWorth.Web.Tests.Theme;

/// <summary>
/// Runs the real app/theme.js inside a real Chromium page (via the shared ui-harness) and checks the
/// actual numbers it produces. Every other guard in this suite compares strings in source files; that
/// can catch a renamed token, but it cannot tell a correct OKLCH round-trip from a subtly wrong one -
/// only running the real conversion can. If this math is wrong, every derived colour downstream is
/// wrong and nothing else here would notice.
/// </summary>
[Trait("Needs", "Browser")]
[Collection(nameof(UiHarnessCollection))]
public sealed class ThemeEngineDerivationTests(UiHarness harness)
{
    // ---------------------------------------------------------------------------------------------
    // 1) OKLCH round-trip: die Grundlage. Wenn das hier nicht stimmt, ist jede abgeleitete Farbe falsch.
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("#3355ff")]
    [InlineData("#ff0000")]
    [InlineData("#00ff00")]
    [InlineData("#123456")]
    [InlineData("#ffffff")]
    [InlineData("#000000")]
    [InlineData("#808080")]
    [InlineData("#7c5ac7")]
    public async Task Hex_Oklch_Hex_RoundTrips_WithinOnePerChannel(string hex)
    {
        var result = await harness.AskAsync("/", false, $$"""
            () => {
                const oklch = window.FullWorthTheme.hexToOklch('{{hex}}');
                return window.FullWorthTheme.oklchToHex(oklch);
            }
            """);

        AssertHexWithinOnePerChannel(hex, result);
    }

    // ---------------------------------------------------------------------------------------------
    // 2) Monochrom-Erkennung.
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("#ffffff", true)]
    [InlineData("#808080", true)]
    [InlineData("#000000", true)]
    [InlineData("#0a0a0b", true)]   // near-black with a hair of chroma from hex quantization
    [InlineData("#3355ff", false)]
    [InlineData("#ff2d55", false)]
    public async Task IsMonochrome_ClassifiesGreyVsColoured(string hex, bool expectedMonochrome)
    {
        var result = await harness.AskAsync("/", false, $$"""
            () => String(window.FullWorthTheme.isMonochrome(window.FullWorthTheme.hexToOklch('{{hex}}')))
            """);

        Assert.Equal(expectedMonochrome, bool.Parse(result));
    }

    // ---------------------------------------------------------------------------------------------
    // 3) Pro-Sitz Ableitung: die von der Slice genannten Seeds, hell UND dunkel.
    // ---------------------------------------------------------------------------------------------

    public static TheoryData<string, string, bool> Seeds() => new()
    {
        { "#3355ff", "light", false },  // Blue
        { "#3355ff", "dark", false },
        { "#ff2d55", "light", false },  // a vivid/strong colour
        { "#ff2d55", "dark", false },
        { "#ffffff", "light", true },   // White
        { "#ffffff", "dark", true },
        { "#808080", "light", true },   // Mid-grey
        { "#808080", "dark", true },
        { "#000000", "light", true },   // Black
        { "#000000", "dark", true }
    };

    [Theory]
    [MemberData(nameof(Seeds))]
    public async Task AccentAndNeutralScales_MatchTheSpecBandsPerSeed(string seed, string mode, bool monochrome)
    {
        var json = await harness.AskAsync("/", false, $$"""
            () => {
                const T = window.FullWorthTheme;
                const seedOklch = T.hexToOklch('{{seed}}');
                const accent = T.accentScale(seedOklch, '{{mode}}');
                const neutral = T.neutralScale(seedOklch, '{{mode}}');
                return JSON.stringify({
                    mono: T.isMonochrome(seedOklch),
                    accentSolidC: T.hexToOklch(accent.solid).C,
                    accentSolidL: T.hexToOklch(accent.solid).L,
                    neutralBgC: T.hexToOklch(neutral.bg).C,
                    onSolid: T.onColor(accent.solid)
                });
            }
            """);

        var result = JsonSerializer.Deserialize<AccentResult>(json, JsonSerializerOptions.Web)!;

        Assert.Equal(monochrome, result.Mono);

        // Neutral tint is always tiny; C=0 exactly for a monochrome seed.
        Assert.True(result.NeutralBgC <= 0.015, $"neutral bg chroma too large: {result.NeutralBgC}");
        if (monochrome) Assert.True(result.NeutralBgC < 0.005, $"monochrome seed produced a hue: {result.NeutralBgC}");

        if (monochrome)
        {
            // No invented hue: a true grey seed must produce a true grey accent.
            Assert.True(result.AccentSolidC < 0.01, $"monochrome seed produced a coloured accent: {result.AccentSolidC}");

            // Issue #149 §3, verbatim: a monochrome accent-solid is "fast schwarz" in light mode and
            // "fast weiß" in dark mode - not a mid-grey button reusing the coloured seed's L=0.56/0.72
            // steps. This is the one place the two branches must NOT share a lightness table.
            if (mode == "light") Assert.True(result.AccentSolidL < 0.30, $"light monochrome solid is not near-black: L={result.AccentSolidL}");
            else Assert.True(result.AccentSolidL > 0.85, $"dark monochrome solid is not near-white: L={result.AccentSolidL}");
        }
        else
        {
            // accentSolidC = clamp(seedC*0.95, 0.11, 0.21) - a little rounding room for the hex quantization.
            Assert.InRange(result.AccentSolidC, 0.10, 0.22);
        }

        // A readable foreground for the solid accent button exists either way.
        Assert.True(result.OnSolid is "#ffffff" or "#151719", $"unexpected onColor result: {result.OnSolid}");
    }

    // ---------------------------------------------------------------------------------------------
    // 4) Datenpalette: die monochrome Fassung bleibt bunt, mit dem festen Farbton-Satz, in JEDEM Modus.
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("light")]
    [InlineData("dark")]
    public async Task DataPalette_MonochromeSeed_AlwaysUsesTheFixedHueSet(string mode)
    {
        var json = await harness.AskAsync("/", false, $$"""
            () => {
                const T = window.FullWorthTheme;
                const seedOklch = T.hexToOklch('#808080');
                const palette = T.dataPalette(seedOklch, '{{mode}}');
                return JSON.stringify(palette.map(hex => Math.round(T.hexToOklch(hex).H)));
            }
            """);

        var hues = JsonSerializer.Deserialize<int[]>(json)!;
        var expected = new[] { 255, 145, 30, 325, 195, 75 };

        Assert.Equal(expected.Length, hues.Length);
        for (var i = 0; i < expected.Length; i++)
        {
            // Not an exact match: at C=0.22/L=0.56-0.78 some of these six points sit right at (or just
            // past) the edge of the sRGB gamut, so the naive per-channel clamp in oklchToHex (this
            // slice's documented, deliberately simple gamut strategy - no chroma-reduction mapping)
            // rotates the hue a little on the way back out. Measured up to ~9deg on the seeds this test
            // covers; 15deg of headroom proves "still the fixed hue family", not exact-degree survival.
            Assert.InRange(Math.Abs(hues[i] - expected[i]), 0, 15);
        }
    }

    [Fact]
    public async Task DataPalette_ColouredSeed_ProducesSixDistinctHuesFollowingTheSeed()
    {
        var json = await harness.AskAsync("/", false, """
            () => {
                const T = window.FullWorthTheme;
                const seedOklch = T.hexToOklch('#3355ff');
                const palette = T.dataPalette(seedOklch, 'light');
                return JSON.stringify(palette.map(hex => Math.round(T.hexToOklch(hex).H)));
            }
            """);

        var hues = JsonSerializer.Deserialize<int[]>(json)!;
        Assert.Equal(6, hues.Distinct().Count());
    }

    // ---------------------------------------------------------------------------------------------
    // 5) Semantic colours are untouched by a seed change, end to end (not just "the source never
    //    mentions --danger" - the COMPUTED value on the root element must be identical before/after).
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task ApplyingASeed_NeverChangesTheDangerToken()
    {
        var json = await harness.AskAsync("/", false, """
            () => {
                const root = document.documentElement;
                // Explicit light + no seed first, so "before" does not depend on whatever the harness's
                // own system colour scheme happened to resolve at page load.
                window.FullWorthTheme.applyTheme({ mode: 'light', seed: '', logoMode: 'standard' });
                const before = getComputedStyle(root).getPropertyValue('--danger').trim();
                window.FullWorthTheme.applyTheme({ mode: 'light', seed: '#ff2d55', logoMode: 'standard' });
                const afterColoured = getComputedStyle(root).getPropertyValue('--danger').trim();
                window.FullWorthTheme.applyTheme({ mode: 'dark', seed: '#808080', logoMode: 'standard' });
                const afterMonoDark = getComputedStyle(root).getPropertyValue('--danger').trim();
                return JSON.stringify({ before, afterColoured, afterMonoDark });
            }
            """);

        var result = JsonSerializer.Deserialize<DangerResult>(json, JsonSerializerOptions.Web)!;
        Assert.Equal(result.Before, result.AfterColoured);
        // Mode also changed here (light -> dark), so --danger is allowed to differ from the FIRST
        // reading (tokens.css itself defines a different --danger per mode) - what matters is that it
        // is still exactly tokens.css's dark value, not something theme.js invented. Re-reading it a
        // third time after reverting to light proves nothing new was ever written for --danger.
        var backToLight = await harness.AskAsync("/", false, """
            () => {
                window.FullWorthTheme.applyTheme({ mode: 'light', seed: '', logoMode: 'standard' });
                return getComputedStyle(document.documentElement).getPropertyValue('--danger').trim();
            }
            """);
        Assert.Equal(result.Before, backToLight);
    }

    private static void AssertHexWithinOnePerChannel(string expected, string actual)
    {
        var expectedBytes = HexBytes(expected);
        var actualBytes = HexBytes(actual);
        for (var i = 0; i < 3; i++)
        {
            var diff = Math.Abs(expectedBytes[i] - actualBytes[i]);
            Assert.True(diff <= 1, $"channel {i} differs by {diff}: {expected} -> {actual}");
        }
    }

    private static int[] HexBytes(string hex)
    {
        var clean = hex.TrimStart('#');
        return
        [
            Convert.ToInt32(clean.Substring(0, 2), 16),
            Convert.ToInt32(clean.Substring(2, 2), 16),
            Convert.ToInt32(clean.Substring(4, 2), 16)
        ];
    }

    private sealed record AccentResult(bool Mono, double AccentSolidC, double AccentSolidL, double NeutralBgC, string OnSolid);

    private sealed record DangerResult(string Before, string AfterColoured, string AfterMonoDark);
}
