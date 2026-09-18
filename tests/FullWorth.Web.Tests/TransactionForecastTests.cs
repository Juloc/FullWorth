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

    /// <summary>
    /// #139, part 2: the sort reversal. The forecast-eligible (plain, unfiltered/lightly-scoped) view
    /// must ask the backend for ascending order - <c>TransactionStore.SearchForUserAsync</c> already
    /// puts pending rows into one contiguous group AFTER every booked row under <c>order=asc</c>, which
    /// is what finally lets "today" sit in the middle of the list instead of forcing the forecast to be
    /// appended below the oldest transaction. Every other (filtered) view must keep the old descending
    /// default untouched.
    /// </summary>
    [Fact]
    public void ForecastEligible_view_requests_ascending_order()
    {
        var js = PageJs();

        Assert.Contains("if (forecastEligible) q.set('order', 'asc');", js);
    }

    /// <summary>
    /// The grouping loop's pending-detection flag has to start in the state that matches which end of
    /// the list is pending: descending still leads with it (unchanged), ascending now trails it. A flag
    /// hardcoded to <c>true</c> here would silently misgroup real transactions under ascending order -
    /// the exact regression this test exists to catch.
    /// </summary>
    [Fact]
    public void Grouping_loop_starts_pending_detection_in_the_state_matching_ascending_order()
    {
        var js = PageJs();

        Assert.Contains("let inPendingGroup = !forecastEligible", js);
    }

    /// <summary>
    /// The "Heute" button used to mean "scroll to the literal top", which was true only because the list
    /// was always descending. Under ascending order the list top is the OLDEST transaction, so the
    /// handler must consult the real today-anchor first and only fall back to the literal top for the
    /// (unchanged) descending/filtered case.
    /// </summary>
    [Fact]
    public void TodayButton_no_longer_unconditionally_scrolls_to_the_literal_top()
    {
        var js = PageJs();
        var match = Regex.Match(js, @"#tx-daybar-today'\)\.addEventListener\('click',\s*\(\)\s*=>\s*\{", RegexOptions.Singleline);
        Assert.True(match.Success, "#tx-daybar-today click handler was not found.");

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

        Assert.Contains("todayAnchor", body);
        Assert.Contains("ascendingTimeline", body);
    }

    /// <summary>
    /// #139, part 3 (load more on scroll): an <c>IntersectionObserver</c> on an end-of-list sentinel,
    /// not a scroll listener - it fires correctly whether <c>scrollHost()</c> is <c>window</c> (mobile)
    /// or the <c>.table-panel</c> element (desktop), without this code needing to know which one is
    /// live for the current viewport.
    /// </summary>
    [Fact]
    public void LoadMore_uses_an_intersection_observer_not_a_scroll_listener()
    {
        var js = PageJs();
        var match = Regex.Match(js, @"function observeForecastSentinel\([^)]*\)\s*\{", RegexOptions.Singleline);
        Assert.True(match.Success, "observeForecastSentinel(...) was not found.");

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

        Assert.Contains("new IntersectionObserver", body);
        Assert.DoesNotContain("addEventListener", body);
    }

    /// <summary>
    /// The backend's <c>ForecastTimelineAsync</c> clamps <c>horizonDays</c> to [1, 180]
    /// (<c>AnalyticsService.cs</c>) - there is no third, larger step to grow into. Once the sentinel has
    /// triggered a load at the max horizon, the sentinel must not be re-added, or the observer would sit
    /// there forever re-requesting a horizon the server can never grow past.
    /// </summary>
    [Fact]
    public void LoadMore_never_requests_past_the_backend_180_day_cap()
    {
        var js = PageJs();

        Assert.Contains("FORECAST_MAX_HORIZON_DAYS = 180", js);
        Assert.Contains("if (forecastLoadingMore || forecastHorizonDays >= FORECAST_MAX_HORIZON_DAYS) return;", js);
        Assert.Contains("if (forecastEligible && forecastHorizonDays < FORECAST_MAX_HORIZON_DAYS) {", js);
    }

    /// <summary>
    /// A wider-horizon refetch returns the same leading entries again (same <c>from</c> date) - appending
    /// the raw response a second time would duplicate every row already on screen. Only entries dated
    /// after the latest one already rendered may be appended.
    /// </summary>
    [Fact]
    public void LoadMore_filters_out_entries_already_rendered()
    {
        var js = PageJs();
        var match = Regex.Match(js, @"async function loadMoreForecast\([^)]*\)\s*\{", RegexOptions.Singleline);
        Assert.True(match.Success, "loadMoreForecast(...) was not found.");

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

        Assert.Contains("entry.date > forecastLatestDate", body);
    }
}
