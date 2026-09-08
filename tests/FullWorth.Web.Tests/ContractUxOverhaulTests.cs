namespace FullWorth.Web.Tests;

public sealed class ContractUxOverhaulTests : IClassFixture<FullWorthWebFactory>
{
    private readonly HttpClient client;

    public ContractUxOverhaulTests(FullWorthWebFactory factory)
    {
        client = factory.CreateClient();
    }

    [Fact]
    public async Task ContractsOverview_UsesFocusedSortGroupingAndAnalysisEntry()
    {
        var js = await GetAsync("/features/contracts.js");

        Assert.Contains("data-contract-analysis", js);
        Assert.Contains("key: 'cycle'", js);
        Assert.Contains("key: 'annual'", js);
        Assert.Contains("key: 'account'", js);
        Assert.Contains("key: 'due'", js);
        Assert.Contains("key: 'category'", js);
        Assert.Contains("contracts-row-card", js);
    }

    [Fact]
    public async Task ContractDetail_SupportsQuickEditsAndCompactPaymentHistory()
    {
        var js = await GetAsync("/features/contracts.js");

        Assert.Contains("data-quick-edit", js);
        Assert.Contains("openQuickEdit", js);
        Assert.Contains("contract-payment-row", js);
        Assert.Contains("data-all-payments", js);
        Assert.Contains("Weitere Vertragsdaten", js);
        Assert.Contains("Zahlungskonten & Historie", js);
    }

    [Fact]
    public async Task ContractAnalysis_ContainsBudgetContextCategoriesAndHistory()
    {
        var js = await GetAsync("/features/contracts.js");
        var css = await GetAsync("/styles/components.css") + await GetAsync("/app.css");

        Assert.Contains("api/analytics/overview", js);
        Assert.Contains("contractAnalysisDonut", js);
        Assert.Contains("contractAnalysisBars", js);
        Assert.Contains("Frei verfügbar", js);
        Assert.Contains("contract-analysis-donut", css);
        Assert.Contains("contract-analysis-chart", css);
    }

    [Fact]
    public async Task ContractEdit_HidesTechnicalIntervalFromNormalForm()
    {
        var js = await GetAsync("/features/contracts.js");

        Assert.Contains("contract-edit-v2", js);
        Assert.DoesNotContain("name=\"interval\"", js);
        Assert.Contains("interval: existing?.interval || 1", js);
    }

    private async Task<string> GetAsync(string path)
    {
        using var response = await client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }
}
