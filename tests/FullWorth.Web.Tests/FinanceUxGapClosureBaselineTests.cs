namespace FullWorth.Web.Tests;

public sealed class FinanceUxGapClosureBaselineTests : IClassFixture<FullWorthWebFactory>
{
    private readonly HttpClient client;

    public FinanceUxGapClosureBaselineTests(FullWorthWebFactory factory) => client = factory.CreateClient();

    [Fact]
    public async Task TransactionsExposeCompleteScopedFilterFlow()
    {
        var js = await GetAsync("/pages/transactions/page.js");
        foreach (var token in new[]
                 {
                     "accountGroupId", "includeDescendants", "merchant", "minAmount", "maxAmount",
                     "refundOnly", "hasReceipt", "ignoredOnly", "status", "categoryId"
                 })
            Assert.Contains(token, js);
        Assert.Contains("URLSearchParams(location.search)", js);
        // The sheet used to be pinned by its CSS class, which said nothing about the flow and broke the
        // moment the filter was rebuilt on the shared form dialog (docs/UI_AUDIT.md step 3). What has to
        // stay true is the flow itself: the filter opens from the list, it is the right-side drawer, and
        // every key above round-trips through the URL.
        Assert.Contains("openFilterSheet", js);
        Assert.Contains("className: 'drawer'", js);
        Assert.Contains("txReplaceUrl(p)", js);
    }

    [Fact]
    public async Task AnalyticsUsesCycleBucketsAndScopedDrilldowns()
    {
        var js = await GetAsync("/pages/analytics/page.js");
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
        var js = await GetAsync("/pages/contracts/page.js");
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
        var js = await GetAsync("/pages/networth/page.js");
        Assert.Contains("wealth.emergencyFund", js);
        Assert.Contains("buildEmergencyCard", js);
        Assert.Contains("openEmergencyFundDialog", js);
        Assert.Contains("accountGroupId", js);
        Assert.Contains("targetAmount", js);
    }

    /// <summary>
    /// Das "Mehr"-Blatt beschriftet nichts selbst.
    ///
    /// Es tat es einmal: Buchungen hieß dort "Alle Buchungen", weil das Blatt die einzige Stelle war,
    /// von der aus man ungefiltert dorthin kam — ein Tippen auf ein Konto führte immer in eine
    /// gefilterte Liste. Das war eine Sonderregel im Blatt selbst, und Sonderregeln im Blatt sind
    /// genau der Weg, auf dem Handy und Desktop auseinanderliefen.
    ///
    /// Heute steht Buchungen unten in der Leiste, der Bereich steht in der Kopfzeile der Seite, und
    /// jeder Eintrag holt seine Beschriftung aus app/menu.js.
    /// </summary>
    [Fact]
    public async Task MobileMoreLabelsEveryEntryFromTheMenuDefinition()
    {
        // Das Blatt zog mit #154 nach app/shell.js - dieselbe Datei, die auch jede Razor-Seite
        // benutzt. Die Aussage bleibt dieselbe: die Beschriftung kommt aus der Menuedefinition,
        // nicht aus einer Sonderregel im Blatt.
        var shell = await GetAsync("/app/shell.js");
        var app = await GetAsync("/app/shell.js");

        Assert.Contains("get(entry.label)", shell);
        Assert.DoesNotContain("transactions.allTx", shell);
        Assert.DoesNotContain("transactions.allTx", app);
    }

    [Fact]
    public async Task AnalyticsPeriodAndIncomeExpenseSegmentsAreKeyboardDrillable()
    {
        var js = await GetAsync("/pages/analytics/page.js");
        Assert.Contains("bindPeriodDrills", js);
        Assert.Contains("data-period-index", js);
        Assert.Contains("data-direction=\"income\"", js);
        Assert.Contains("data-direction=\"expense\"", js);
        Assert.Contains("role=\"button\"", js);
        Assert.Contains("tabindex=\"0\"", js);
        Assert.Contains("periodRange", js);
    }


    [Fact]
    public async Task AnalyticsSeparatesPreviewActiveAndCompletedAverageWindows()
    {
        var kit = await GetAsync("/features/ux-kit.js");
        var js = await GetAsync("/pages/analytics/page.js");
        Assert.Contains("activeFrom", kit);
        Assert.Contains("averageFrom", kit);
        Assert.Contains("averageTo", kit);
        Assert.Contains("completedAverageLabel", js);
        Assert.Contains("averageOverview?.expenses", js);
        Assert.DoesNotContain("avgPerBucket(o?.expenses", js);
    }

    [Fact]
    public async Task CategoryOverviewUsesRootLevelAndMerchantDrillUsesStableIdentity()
    {
        var analytics = await GetAsync("/pages/analytics/page.js");
        var transactions = await GetAsync("/pages/transactions/page.js");
        Assert.Contains("cats.filter(category => !category.parentId)", analytics);
        Assert.Contains("openCategoryDetail", analytics);
        Assert.Contains("data-merchant-id", analytics);
        Assert.Contains("merchantId=", analytics);
        Assert.Contains("params.get('merchantId')", transactions);
    }

    [Fact]
    public async Task MixedBudgetWindowsAreNotBlindlySummed()
    {
        var budgets = await GetAsync("/pages/budgets/page.js");
        Assert.Contains("const windows = new Set", budgets);
        Assert.Contains("comparableWindow", budgets);
        Assert.Contains("unterschiedliche aktive Zeiträume", budgets);
    }

    [Fact]
    public async Task FinanceUxModulesArePrecachedAndTouchTargetsAreAccessible()
    {
        var sw = await GetAsync("/sw.js");
        foreach (var asset in new[]
                 {
                     "'/pages/transactions/page.js'",
                     "'/pages/analytics/page.js'",
                     "'/pages/contracts/page.js'",
                     "'/features/ux-kit.js'"
                 })
            Assert.Contains(asset, sw);

        var css = await GetAsync("/styles/components.css") + await GetAsync("/styles/app.css")
            + await GetAsync("/pages/networth/page.css") + await GetAsync("/pages/transactions/page.css")
            + await GetAsync("/pages/analytics/page.css");
        Assert.Contains(".an-period-hit:focus-visible", css);
        Assert.Contains("min-height:44px", css);
        Assert.Contains(".fw-cycle button", css);
        Assert.Contains(".contracts-filter-open", css);
    }

    private async Task<string> GetAsync(string path)
    {
        using var response = await client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }
}
