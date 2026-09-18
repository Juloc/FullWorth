using System.Text.RegularExpressions;

namespace FullWorth.Web.Tests;

/// <summary>
/// #139, part 1: source-text guards for the forecast section appended to the transactions list.
///
/// No database, no HTTP - these are the same kind of plain source checks as
/// <see cref="FrontendArchitectureGuardTests"/>, because what they pin (no second cadence-math engine
/// in JS, no click-to-open on a row with nothing to open, tokens-only CSS) is true of the file's text
/// regardless of what any particular request returns.
/// </summary>
public sealed class TransactionForecastTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FullWorth.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string PageJs() => File.ReadAllText(Path.Combine(
        Root(), "src", "FullWorth.Web", "wwwroot", "pages", "transactions", "page.js"));

    private static string PageCss() => File.ReadAllText(Path.Combine(
        Root(), "src", "FullWorth.Web", "wwwroot", "pages", "transactions", "page.css"));

    [Fact]
    public void Forecast_is_fetched_and_rendered()
    {
        var js = PageJs();

        Assert.Contains("api/transactions/forecast", js);
        Assert.Contains("forecastRow", js);
    }

    /// <summary>All cadence math (next due date, next cycle) must stay server-side in
    /// <c>ContractCycle</c> - the frontend only ever displays a date the backend already computed.</summary>
    [Fact]
    public void PageJs_contains_no_second_cadence_math_engine()
    {
        var js = PageJs();

        Assert.DoesNotContain("AddMonths", js);
        Assert.DoesNotContain("AddDays", js);
        Assert.DoesNotContain(".setMonth(", js);
    }

    /// <summary>A forecast row has nothing to open - there is no transaction behind it yet - so it must
    /// never end up wired to the detail-drawer opener the way a real row is.</summary>
    [Fact]
    public void ForecastRow_builder_never_wires_a_click_to_open_detail()
    {
        var js = PageJs();
        var match = Regex.Match(js, @"function forecastRow\([^)]*\)\s*\{", RegexOptions.Singleline);
        Assert.True(match.Success, "forecastRow(...) was not found.");

        // The body of forecastRow runs from its opening brace to the matching closing brace.
        var start = match.Index + match.Length;
        var depth = 1;
        var end = start;
        while (depth > 0 && end < js.Length)
        {
            if (js[end] == '{') depth++;
            else if (js[end] == '}') depth--;
            end++;
        }
        var body = js[start..end];

        Assert.DoesNotContain("openDetail", body);
        Assert.DoesNotContain("addEventListener", body);
        Assert.DoesNotContain("data-tx-select", body);
    }

    /// <summary>Tokens only (design system rule) - no hex/rgb literal in the forecast row's own rule
    /// block.</summary>
    [Fact]
    public void ForecastRow_css_uses_only_design_tokens()
    {
        var css = PageCss();
        var match = Regex.Match(css, @"\.tx-row-forecast\s*\{([^}]*)\}");
        Assert.True(match.Success, "No .tx-row-forecast rule found.");
        var declarations = match.Groups[1].Value;

        Assert.DoesNotContain("#", declarations);
        Assert.DoesNotContain("rgb(", declarations, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rgba(", declarations, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("var(--", declarations);
    }
}
