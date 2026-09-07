using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Web.Tests;

public sealed class TaxAssistantUiBaselineTests : IClassFixture<FullWorthWebFactory>
{
    private readonly HttpClient client;
    private readonly FullWorthWebFactory factory;

    public TaxAssistantUiBaselineTests(FullWorthWebFactory factory)
    {
        this.factory = factory;
        client = factory.CreateClient();
    }

    [Fact]
    public async Task MainApp_LoadsTaxFeatureModules()
    {
        // "/" is served by MapFallbackToFile("index.html").RequireAuthorization(), so an unauthenticated
        // client is redirected to the auth login shell. Read the shipped index.html shell directly.
        //
        // Tax is a real registered view on the new architecture (app.js's ALL_VIEWS/feature-registry),
        // not a motion.js side-effect import (the old patch-layer pattern this branch removed): it has a
        // static nav entry + view section in index.html, and app.js imports and registers the module.
        var html = ReadWebAsset("index.html");
        var appJs = await GetAsync("/app.js");
        var tax = await GetAsync("/features/tax.js");
        var review = await GetAsync("/features/tax-review-extra.js");

        Assert.Contains("data-view=\"tax\"", html);
        Assert.Contains("id=\"view-tax\"", html);
        Assert.Contains("./features/tax.js", appJs);
        Assert.Contains("'tax'", appJs);
        Assert.Contains("/tax/review", tax);
        Assert.Contains("api/tax/candidates", tax);
        Assert.Contains("api/tax/years/", review);
    }

    [Fact]
    public async Task TaxReview_ContainsDecisionEvidenceAndExportFlows()
    {
        var tax = await GetAsync("/features/tax.js");
        var review = await GetAsync("/features/tax-review-extra.js");

        Assert.Contains("api/tax/candidates/${id}/${action}", tax);
        Assert.Contains("'confirm'", tax);
        Assert.Contains("'reject'", tax);
        Assert.Contains("eligiblePercentage", tax);
        Assert.Contains("document-target", review);
        Assert.Contains("form.append('document'", review);
        Assert.Contains("/export?format=", review);
        Assert.Contains("data-tax-export", review);
    }

    [Fact]
    public async Task TaxSettings_ExposePersonalAndAnalysisOptOuts()
    {
        var tax = await GetAsync("/features/tax.js");
        var review = await GetAsync("/features/tax-review-extra.js");

        Assert.Contains("api/tax/profile/settings", tax);
        Assert.Contains("assistantEnabled", tax);
        Assert.Contains("analyzeTransactions", review);
        Assert.Contains("analyzePurchases", review);
        Assert.Contains("analyzeDocuments", review);
        Assert.Contains("automaticAnalysisEnabled", review);
        Assert.Contains("aiAnalysisEnabled", review);
        Assert.Contains("api/tax/data", review);
    }

    [Fact]
    public async Task TaxUi_HasResponsiveReviewAndSettingsStyles()
    {
        var taxCss = await GetAsync("/features/tax.css");
        var reviewCss = await GetAsync("/features/tax-review-extra.css");

        Assert.Contains("tax-view", taxCss);
        Assert.Contains("tax-case", taxCss);
        Assert.Contains("@media", taxCss);
        Assert.Contains("tax-year-review", reviewCss);
        Assert.Contains("tax-advanced-grid", reviewCss);
        Assert.Contains("@media", reviewCss);
    }

    private string ReadWebAsset(string relative)
    {
        var webRoot = factory.Services.GetRequiredService<IWebHostEnvironment>().WebRootPath;
        return File.ReadAllText(Path.Combine(webRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
    }

    private async Task<string> GetAsync(string path)
    {
        using var response = await client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }
}
