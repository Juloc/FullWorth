namespace FullWorth.Web.Tests;

public sealed class FinanceUxGapClosureBaselineTests : IClassFixture<FullWorthWebFactory>
{
    private readonly HttpClient client;

    public FinanceUxGapClosureBaselineTests(FullWorthWebFactory factory) => client = factory.CreateClient();

    [Fact]
    public async Task TransactionsExposeCompleteScopedFilterFlow()
    {
        var js = await GetAsync("/features/transactions.js");
        foreach (var token in new[]
                 {
                     "accountGroupId", "includeDescendants", "merchant", "minAmount", "maxAmount",
                     "refundOnly", "hasReceipt", "ignoredOnly", "status", "categoryId"
                 })
            Assert.Contains(token, js);
        Assert.Contains("URLSearchParams(location.search)", js);
        Assert.Contains("tx-filter-sheet", js);
    }

    [Fact]
    public async Task AnalyticsUsesCycleBucketsAndScopedDrilldowns()
    {
        var js = await GetAsync("/features/analytics.js");
        Assert.Contains("byPeriod", js);
        Assert.Contains("granularity=${gran}", js);
        Assert.Contains("analyticsTxScope", js);
        Assert.Contains("includeDescendants=true", js);
        Assert.Contains("merchant=", js);
        foreach (var cycle in new[] { "'week'", "'month'", "'quarter'", "'year'" })
            Assert.Contains(cycle, js);
    }

    [Fact]
    public async Task ContractsExposeAccountCategoryCycleFiltersAndIdentityFallback()
    {
        var js = await GetAsync("/features/contracts.js");
        Assert.Contains("openContractFilterSheet", js);
        Assert.Contains("view.account", js);
        Assert.Contains("view.category", js);
        Assert.Contains("view.cycle", js);
        Assert.Contains("categoryIconKey", js);
        Assert.Contains("monthlyEquivalent", js);
        Assert.Contains("annualizedAmount", js);
    }

    [Fact]
    public async Task WealthHasExplicitConfigurableEmergencyFund()
    {
        var js = await GetAsync("/features/networth.js");
        Assert.Contains("wealth.emergencyFund", js);
        Assert.Contains("buildEmergencyCard", js);
        Assert.Contains("openEmergencyFundDialog", js);
        Assert.Contains("accountGroupId", js);
        Assert.Contains("targetAmount", js);
    }

    [Fact]
    public async Task MobileMoreUsesExplicitAllBookingsEntry()
    {
        var app = await GetAsync("/app.js");
        Assert.Contains("view==='transactions'", app);
        Assert.Contains("transactions.allTx", app);
    }

    private async Task<string> GetAsync(string path)
    {
        using var response = await client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }
}
