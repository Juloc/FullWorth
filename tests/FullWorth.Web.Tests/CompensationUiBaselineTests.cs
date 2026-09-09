using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Web.Tests;

public sealed class CompensationUiBaselineTests : IClassFixture<FullWorthWebFactory>
{
    private readonly HttpClient _client;
    private readonly FullWorthWebFactory _factory;

    public CompensationUiBaselineTests(FullWorthWebFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    // The calculator has always handled a partial year; for a long time nothing could enter one, so a
    // mid-year job change was silently calculated as twelve months. Three parts have to stay together:
    // the fields, the mapping in the shared profile reader, and the note that explains the smaller
    // annual figures - the mapping alone would produce correct numbers with no visible reason.
    [Fact]
    public async Task CompensationPage_CanEnterAPartialEmploymentYearAndSaysWhenItDoes()
    {
        var html = await GetAsync("/compensation.html");
        var shared = await GetAsync("/features/compensation-shared.js");
        var baseJs = await GetAsync("/features/compensation.js");
        var css = await GetAsync("/compensation.css");

        Assert.Contains("id=\"employment-start\"", html);
        Assert.Contains("id=\"employment-end\"", html);
        Assert.Contains("employmentStart: val('employment-start')", shared);
        Assert.Contains("employmentEnd: val('employment-end')", shared);
        Assert.Contains("setVal('employment-start'", shared);
        Assert.Contains("setVal('employment-end'", shared);
        Assert.Contains("monthsEmployedInYear", baseJs);
        Assert.Contains("comp-partial-year", baseJs);
        // The rule has to live in the sheet the page actually loads - compensation.html loads
        // /compensation.css, not styles/features/compensation.css.
        Assert.Contains(".comp-partial-year", css);
    }

    [Fact]
    public async Task CompensationPage_LoadsAllFeatureModules()
    {
        var html = await GetAsync("/compensation.html");
        var baseJs = await GetAsync("/features/compensation.js");
        var extendedJs = await GetAsync("/features/compensation-extended.js");
        var historyJs = await GetAsync("/features/compensation-history.js");
        var historyCss = await GetAsync("/compensation-history.css");

        Assert.Contains("/features/compensation.js", html);
        Assert.Contains("/features/compensation-extended.js", html);
        Assert.Contains("/features/compensation-history.js", html);
        Assert.Contains("/compensation-history.css", html);
        Assert.Contains("api/compensation/calculate", baseJs);
        Assert.Contains("api/compensation/insights", extendedJs);
        Assert.Contains("api/compensation/payslips/extract", extendedJs);
        Assert.Contains("api/compensation/history", historyJs);
        Assert.Contains("api/compensation/timeline", historyJs);
        Assert.Contains("history-chart", historyCss);
    }

    [Fact]
    public async Task MainNavigation_ContainsCompensationEntry()
    {
        // "/" is served by MapFallbackToFile("index.html").RequireAuthorization(), so an unauthenticated
        // client is redirected to the auth login shell. Read the shipped index.html shell directly.
        var html = ReadWebAsset("index.html");
        var navJs = await GetAsync("/features/compensation-nav.js");

        Assert.Contains("data-compensation-link", html);
        Assert.Contains("/compensation.html", navJs);
        Assert.Contains("data-compensation-mobile", navJs);
    }

    private string ReadWebAsset(string relative)
    {
        var webRoot = _factory.Services.GetRequiredService<IWebHostEnvironment>().WebRootPath;
        return File.ReadAllText(Path.Combine(webRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
    }

    private async Task<string> GetAsync(string path)
    {
        using var response = await _client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }
}
