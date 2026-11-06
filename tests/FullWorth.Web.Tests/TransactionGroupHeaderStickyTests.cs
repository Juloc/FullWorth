using System.Text.RegularExpressions;

namespace FullWorth.Web.Tests;

/// <summary>
/// Die Buchungsliste klebt in drei Stufen: der Topbar, darunter die Tagesleiste ("Heute", Tagesstand),
/// darunter der Kopf der Tagesgruppe. Eine Stufe sitzt immer am unteren Rand der davor.
///
/// Der Fehler, den das haelt: der Topbar ist seit der Kopfzeile mit Brotkrumen selbst sticky und die SEITE
/// scrollt. Die Tagesleiste stand aber noch auf <c>top:0</c> - sie lag unsichtbar UNTER dem Topbar, und
/// mit ihr der Knopf "Heute". Die Tageskoepfe klebten gar nicht: <c>.table-panel</c> hat
/// <c>overflow:auto</c> und ist damit der Bezug fuer <c>position:sticky</c>, scrollt aber nicht, also
/// bleibt der Kopf stehen, wo er ist. Gemessen: Kopf bei -11 px statt geklebt, die erste Zeile jedes
/// Tages hinter dem Topbar.
///
/// Geprueft wird die REGEL - jeder Versatz passt zu dem, was davor klebt -, nicht der Wortlaut einer
/// Deklaration: der Fehler kommt wieder, sobald sich eine Stufe bewegt und die andere nicht.
/// </summary>
public sealed class TransactionGroupHeaderStickyTests : IClassFixture<FullWorthWebFactory>
{
    private readonly HttpClient client;

    public TransactionGroupHeaderStickyTests(FullWorthWebFactory factory)
    {
        client = factory.CreateClient();
    }

    [Fact]
    public async Task The_day_bar_sticks_directly_below_the_topbar()
    {
        var rule = await RuleForAsync("/pages/transactions/page.css", ".tx-daybar");

        Assert.Contains("position:sticky", rule);
        Assert.Equal("var(--topbar-h)", Declaration(rule, "top"));
        Assert.Equal("var(--tx-daybar-h)", Declaration(rule, "height"));
    }

    [Fact]
    public async Task The_group_header_sticks_directly_below_the_day_bar()
    {
        var rule = await RuleForAsync("/pages/transactions/page.css", ".tx-date-head");

        Assert.Contains("position:sticky", rule);
        Assert.Equal("calc(var(--topbar-h) + var(--tx-daybar-h))", Declaration(rule, "top"));
        Assert.Equal("var(--tx-head-h)", Declaration(rule, "height"));
    }

    /// <summary>
    /// Ein Vorfahr mit overflow:auto ist der Bezug fuer position:sticky - auch wenn er nie scrollt. Das
    /// Panel muss deshalb sichtbar bleiben, sonst klebt nichts mehr.
    /// </summary>
    [Fact]
    public async Task The_panel_is_not_a_scroll_container()
    {
        var rule = await RuleForAsync("/pages/transactions/page.css", "#view-transactions .table-panel");

        Assert.Equal("visible", Declaration(rule, "overflow"));
    }

    /// <summary>
    /// Der Sprung auf "Heute" richtet die erste Zeile eines Tages am oberen Rand aus. Ohne diesen Abstand
    /// steht sie hinter Topbar, Tagesleiste und Gruppenkopf.
    /// </summary>
    [Fact]
    public async Task A_row_scrolled_into_view_lands_below_everything_that_sticks()
    {
        var rule = await RuleForAsync("/pages/transactions/page.css", ".tx-row");

        Assert.Equal(
            "calc(var(--topbar-h) + var(--tx-daybar-h) + var(--tx-head-h))",
            Declaration(rule, "scroll-margin-top"));
    }

    [Fact]
    public async Task The_small_screen_layout_does_not_move_the_headers_again()
    {
        var responsive = await GetAsync("/styles/responsive.css");

        Assert.DoesNotContain("#view-transactions .tx-date-head{border-radius:8px;margin:8px 0 2px;top:", responsive);
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
