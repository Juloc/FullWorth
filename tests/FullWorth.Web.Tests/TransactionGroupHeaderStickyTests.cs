using System.Text.RegularExpressions;

namespace FullWorth.Web.Tests;

/// <summary>
/// The transaction list's date group header ("Vorgemerkt", "10.9.2026") is sticky, and a sticky offset
/// only means anything relative to its own scroll container.
///
/// The bug: the header cell said <c>top: var(--topbar-h)</c> — correct when the PAGE scrolls and the
/// header has to clear the app topbar. Above 1023px the page is not the scroll container: the table panel
/// is (<c>overflow:auto</c>). So the header pinned itself ~96px DOWN inside the panel and painted over the
/// first transaction of every group, permanently, without anyone even scrolling. The first booking of
/// each day was simply not visible.
///
/// These tests pin the RULE — each offset matches its scroll container — rather than the text of a
/// declaration, because the same class of bug returns the moment one of the two moves without the other.
/// </summary>
public sealed class TransactionGroupHeaderStickyTests : IClassFixture<FullWorthWebFactory>
{
    private const string MobileBreakpoint = "@media(max-width:1023px)";

    private readonly HttpClient client;

    public TransactionGroupHeaderStickyTests(FullWorthWebFactory factory)
    {
        client = factory.CreateClient();
    }

    /// <summary>
    /// Default: the panel scrolls itself, so the header sticks to the panel's own top edge. Any non-zero
    /// offset here is measured against the wrong element and lands on top of a transaction row.
    /// </summary>
    [Fact]
    public async Task The_group_header_sticks_to_the_top_of_the_panel_that_scrolls_it()
    {
        var rule = await RuleForAsync("/app.css", ".tx-date-head td");

        Assert.Contains("position:sticky", rule);

        var top = Declaration(rule, "top");
        Assert.Equal("0", top);
    }

    /// <summary>
    /// Below the breakpoint the panel is <c>overflow:visible</c>, so the PAGE scrolls and the offset has
    /// to be the topbar height again. The two belong together: whichever element scrolls is the one the
    /// offset is measured against.
    /// </summary>
    [Fact]
    public async Task Where_the_page_scrolls_instead_the_header_clears_the_topbar()
    {
        var responsive = await GetAsync("/styles/responsive.css");
        var block = MediaBlock(responsive, MobileBreakpoint);

        // The premise of the override: the panel stops being a scroll container here.
        var panel = RuleIn(block, "#view-transactions .table-panel");
        Assert.Equal("visible", Declaration(panel, "overflow"));

        var header = RuleIn(block, "#view-transactions .tx-date-head td");
        Assert.Equal("var(--topbar-h)", Declaration(header, "top"));
    }

    private async Task<string> RuleForAsync(string path, string selector) =>
        RuleIn(await GetAsync(path), selector);

    /// <summary>The declaration block of the first rule whose selector list matches exactly.</summary>
    private static string RuleIn(string css, string selector)
    {
        var match = Regex.Match(css, Regex.Escape(selector) + @"\s*\{([^}]*)\}");
        Assert.True(match.Success, $"No rule for '{selector}'.");
        return match.Groups[1].Value;
    }

    /// <summary>Everything between a media query's brace and its matching close.</summary>
    private static string MediaBlock(string css, string query)
    {
        var start = css.IndexOf(query, StringComparison.Ordinal);
        Assert.True(start >= 0, $"No '{query}' block.");
        var open = css.IndexOf('{', start);
        var depth = 0;
        for (var i = open; i < css.Length; i++)
        {
            if (css[i] == '{') depth++;
            else if (css[i] == '}' && --depth == 0) return css[(open + 1)..i];
        }

        Assert.Fail($"'{query}' is never closed.");
        return string.Empty;
    }

    private static string Declaration(string rule, string property)
    {
        var match = Regex.Match(rule, @"(?:^|;)\s*" + Regex.Escape(property) + @"\s*:\s*([^;]+)");
        Assert.True(match.Success, $"No '{property}' in: {rule.Trim()}");
        return match.Groups[1].Value.Trim();
    }

    private async Task<string> GetAsync(string path)
    {
        using var response = await client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }
}
