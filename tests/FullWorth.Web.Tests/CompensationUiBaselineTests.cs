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
