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
