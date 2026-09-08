namespace FullWorth.Web.Tests;

public sealed class AutopilotFrontendGuardTests
{
    [Fact]
    public void AutopilotMustNotAddPrimaryOrBottomNavigationItem()
    {
        var html = File.ReadAllText(Path.Combine(WwwRoot(), "index.html"));
        var primary = Slice(html, "<nav id=\"nav\"", "</nav>");
        var mobile = Slice(html, "<nav id=\"bottom-nav\"", "</nav>");

        foreach (var nav in new[] { primary, mobile })
        {
            Assert.DoesNotContain("data-view=\"insights\"", nav, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("data-view=\"autopilot\"", nav, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("data-view=\"intelligence\"", nav, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("data-view=\"ai\"", nav, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void PlannedAutopilotFeaturesUseSharedApiClient()
    {
        var featureRoot = Path.Combine(WwwRoot(), "features");
        var planned = new[]
        {
            "insights.js",
            "action-proposals.js",
            "scenarios.js",
            "rule-compiler.js"
        };

        foreach (var name in planned)
        {
            var path = Path.Combine(featureRoot, name);
            if (!File.Exists(path)) continue;
            var content = File.ReadAllText(path);
            Assert.DoesNotContain("/bff/backend/", content, StringComparison.Ordinal);
            Assert.DoesNotContain("/bff/banking/", content, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Deploy5InsightsAreSecondaryReadOnlyFeatureSurface()
    {
        var html = File.ReadAllText(Path.Combine(WwwRoot(), "index.html"));
        var app = File.ReadAllText(Path.Combine(WwwRoot(), "app.js"));
        var feature = File.ReadAllText(Path.Combine(WwwRoot(), "features", "insights.js"));
        var serviceWorker = File.ReadAllText(Path.Combine(WwwRoot(), "sw.js"));
        var appCss = File.ReadAllText(Path.Combine(WwwRoot(), "app.css"));
        var insightCssPath = Path.Combine(WwwRoot(), "styles", "features", "insights.css");

        Assert.Contains("id=\"dashboard-insights\"", html);
        Assert.Contains("id=\"view-insights\"", html);
        Assert.Contains("id=\"insights-root\"", html);

        Assert.True(File.Exists(insightCssPath));
        Assert.Contains("/styles/features/insights.css", html);
        Assert.DoesNotContain("Autopilot Deploy 5: read-only financial insights", appCss, StringComparison.Ordinal);
        Assert.Contains(".register('insights'", app);
        Assert.Contains("v!=='insights'", app);
        Assert.Contains("renderDashboardInsights", app);
        Assert.Contains("mountInsights", app);

        Assert.Contains("api/insights?view=", feature);
        Assert.Contains("load(ctx, 'current', 3", feature);
        Assert.Contains("data-insights-tab", feature);
        Assert.Contains("data-action=\"read\"", feature);
        Assert.Contains("data-action=\"dismiss\"", feature);
        Assert.Contains("data-action=\"snooze\"", feature);
        Assert.Contains("data-feedback=\"useful\"", feature);
        Assert.Contains("data-feedback=\"irrelevant\"", feature);
        Assert.Contains("ctx.isPrivate()", feature);
        Assert.Contains("ctx.dialog", feature);
        Assert.Contains("AbortController", feature);
        Assert.Contains("emitAppEvent('contract:open'", feature);
        Assert.Contains("emitAppEvent('budget:open'", feature);

        Assert.DoesNotContain("api/contracts/", feature, StringComparison.Ordinal);
        Assert.DoesNotContain("api/transactions/", feature, StringComparison.Ordinal);
        Assert.DoesNotContain("api/budgets/", feature, StringComparison.Ordinal);
        Assert.DoesNotContain("api/contract-parity/merge", feature, StringComparison.Ordinal);
        Assert.DoesNotContain("PriceChangeSuggestion", feature, StringComparison.Ordinal);
        Assert.DoesNotContain("ai-badge", feature, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ai-gradient", feature, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("IIntelligenceProvider", feature, StringComparison.Ordinal);

        Assert.Contains("'/features/insights.js'", serviceWorker);
        Assert.Contains("'/styles/features/insights.css'", serviceWorker);
    }

    [Fact]
    public void InsightFeatureDoesNotAppearInMoreOrPersistentNavigation()
    {
        var app = File.ReadAllText(Path.Combine(WwwRoot(), "app.js"));
        var html = File.ReadAllText(Path.Combine(WwwRoot(), "index.html"));

        Assert.Contains("v!=='insights'", app);
        Assert.DoesNotContain("data-view=\"insights\"", Slice(html, "<nav id=\"bottom-nav\"", "</nav>"), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("data-view=\"insights\"", Slice(html, "<nav id=\"nav\"", "</nav>"), StringComparison.OrdinalIgnoreCase);
    }

    private static string Slice(string text, string startMarker, string endMarker)
    {
        var start = text.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing marker: {startMarker}");
        var end = text.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, $"Missing marker: {endMarker}");
        return text[start..(end + endMarker.Length)];
    }

    private static string WwwRoot() => Path.Combine(Root(), "src", "FullWorth.Web", "wwwroot");

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FullWorth.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
