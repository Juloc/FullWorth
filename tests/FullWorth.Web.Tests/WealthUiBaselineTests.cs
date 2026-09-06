namespace FullWorth.Web.Tests;

public sealed class WealthUiBaselineTests : IClassFixture<FullWorthWebFactory>
{
    private readonly HttpClient client;

    public WealthUiBaselineTests(FullWorthWebFactory factory) => client = factory.CreateClient();

    [Fact]
    public async Task WealthScreenUsesUnifiedBackendTotalsAndHistory()
    {
        var js = await GetAsync("/features/networth.js");
        Assert.Contains("api/wealth/overview", js);
        Assert.Contains("api/wealth/history", js);
        Assert.DoesNotContain("api/analytics/dashboard", js);
        Assert.DoesNotContain("api/net-worth/history", js);
        Assert.Contains("overview.netWorth", js);
        Assert.Contains("overview.totalAssets", js);
        Assert.Contains("overview.totalLiabilities", js);
        Assert.Contains("overview.isComplete", js);
        Assert.Contains("overview.missingCurrencies", js);
        Assert.Contains("overview.investments", js);
        Assert.DoesNotContain("api/investments/net-worth-contribution", js);
    }

    [Fact]
    public async Task WealthTrendSupportsTenYearsMaxAndCustomDateRange()
    {
        var js = await GetAsync("/features/networth.js");
        Assert.Contains("{ m: 120", js);
        Assert.Contains("{ m: 0", js);
        Assert.Contains("data-range-from", js);
        Assert.Contains("data-range-to", js);
        Assert.Contains("data-range-apply", js);
        Assert.Contains("windowMonths = -1", js);
        Assert.Contains("URLSearchParams", js);
    }

    [Fact]
    public async Task AssetWizardUsesCanonicalTaxonomyAndValuationHistory()
    {
        var js = await GetAsync("/features/networth.js");
        foreach (var kind in new[] { "real_estate", "vehicle", "precious_metal", "collectible", "receivable", "business_interest", "insurance_pension", "other" })
            Assert.Contains($"'{kind}'", js);
        Assert.Contains("wealth-type-grid", js);
        Assert.Contains("api/assets/${asset.id}/valuations", js);
        Assert.Contains("method: 'manual'", js);
        Assert.Contains("isAccepted: true", js);
        Assert.DoesNotContain("document.createElement('style')", js);
    }

    [Fact]
    public async Task RealEstateDetailsUseCoreOperationsAndAdvancedModules()
    {
        var wrapper = await GetAsync("/features/wealth-real-estate.js");
        var core = await GetAsync("/features/wealth-real-estate-core.js");
        var operations = await GetAsync("/features/wealth-real-estate-operations.js");
        var advanced = await GetAsync("/features/wealth-real-estate-advanced.js");
        Assert.Contains("wealth-real-estate-core.js", wrapper);
        Assert.Contains("wealth-real-estate-operations.js", wrapper);
        Assert.Contains("wealth-real-estate-advanced.js", wrapper);
        Assert.Contains("attachRealEstateOperations", wrapper);
        Assert.Contains("attachRealEstateAdvanced", wrapper);
        Assert.Contains("api/assets/${id}/real-estate", core);
        Assert.Contains("api/assets/${id}/debts", core);
        Assert.Contains("api/assets/${id}/real-estate/units", operations);
        Assert.Contains("api/assets/${id}/real-estate/leases", operations);
        Assert.Contains("api/assets/${id}/cashflows", operations);
        Assert.Contains("api/assets/${id}/real-estate/improvements", operations);
        Assert.Contains("api/assets/${id}/recurring-contracts", operations);
        Assert.Contains("real-estate/energy-certificates", advanced);
        Assert.Contains("api/assets/${asset.id}/documents", advanced);
        Assert.Contains("real-estate/valuation-capabilities", advanced);
        Assert.Contains("real-estate/estimate", advanced);
        Assert.Contains("real-estate/external-valuation", advanced);
        Assert.Contains("isAccepted:true", advanced);
        Assert.Contains("isPrivate()?'••••••':x.originalFileName", advanced);
    }

    [Fact]
    public async Task VehicleAndPreciousMetalDetailsUseCanonicalValuationAndDebtApis()
    {
        var wrapper = await GetAsync("/features/wealth-real-estate.js");
        var js = await GetAsync("/features/wealth-specialized-assets.js");
        var css = await GetAsync("/features/wealth-specialized-assets.css");

        Assert.Contains("wealth-specialized-assets.js", wrapper);
        Assert.Contains("api/assets/${asset.id}/vehicle", js);
        Assert.Contains("api/assets/${asset.id}/precious-metal", js);
        Assert.Contains("api/assets/${asset.id}/valuations", js);
        Assert.Contains("api/assets/${asset.id}/debts", js);
        Assert.Contains("api/loans", js);
        Assert.Contains("api/liabilities", js);
        Assert.Contains("method: 'internal_estimate'", js);
        Assert.Contains("aria-pressed", js);
        Assert.Contains("••••••", js);
        Assert.DoesNotContain("api/investments/market", js);
        Assert.Contains("var(--", css);
        Assert.DoesNotContain("linear-gradient", css);
    }

    [Fact]
    public async Task RemainingSpecializedAssetsHaveFunctionalDetailAndActivityFlows()
    {
        var wrapper = await GetAsync("/features/wealth-real-estate.js");
        var js = await GetAsync("/features/wealth-specialized-assets-extra.js");

        Assert.Contains("wealth-specialized-assets-extra.js", wrapper);
        foreach (var kind in new[] { "'collectible'", "'receivable'", "'business_interest'", "'insurance_pension'" })
            Assert.Contains(kind, js);
        Assert.Contains("business-interest", js);
        Assert.Contains("insurance-pension", js);
        Assert.Contains("api/assets/${asset.id}/valuations", js);
        Assert.Contains("${base}/payments", js);
        Assert.Contains("${base}/write-down", js);
        Assert.Contains("api/assets/${asset.id}/cashflows", js);
        Assert.Contains("type: 'distribution'", js);
        Assert.Contains("isAccepted: true", js);
        Assert.Contains("••••••", js);
        Assert.Contains("privacy() ? 'password' : 'text'", js);
    }

    [Fact]
    public async Task InvestmentWealthDrilldownReusesCanonicalPortfolioSecurityAndMarketDataApis()
    {
        var wrapper = await GetAsync("/features/wealth-real-estate.js");
        var adapter = await GetAsync("/features/wealth-investment-consolidation.js");
        var portfolioUi = await GetAsync("/features/investment-performance-ui.js");
        var css = await GetAsync("/features/wealth-investment-consolidation.css");

        Assert.Contains("wealth-investment-consolidation.js", wrapper);
        Assert.Contains("investment-performance-ui.js", adapter);
        Assert.Contains("data-portfolio", adapter);
        Assert.Contains("overview-v2", adapter);
        Assert.Contains("api/investments/securities", adapter);
        Assert.Contains("api/investments/portfolios/${portfolioId}/trades", adapter);
        Assert.Contains("api/market-data/securities/${securityId}/effective-price", adapter);
        Assert.Contains("api/market-data/securities/${securityId}/history", adapter);
        Assert.Contains("priceStateText", adapter);
        Assert.Contains("Asset allocation", adapter);
        Assert.Contains("dataset.ipSecurity", adapter);
        Assert.Contains("performance-v2", portfolioUi);
        Assert.Contains("TWR", portfolioUi);
        Assert.Contains("XIRR", portfolioUi);
        Assert.DoesNotContain("api/assets", adapter);
        Assert.DoesNotContain("api/investments/net-worth-contribution", adapter);
        Assert.Contains("var(--", css);
        Assert.DoesNotContain("linear-gradient", css);
    }

    [Fact]
    public async Task PortabilityUsesCompleteZipBackupWithoutCachingFinancialData()
    {
        var wrapper = await GetAsync("/features/wealth-real-estate.js");
        var portability = await GetAsync("/features/wealth-portability.js");
        var sw = await GetAsync("/sw.js");

        Assert.Contains("wealth-portability.js", wrapper);
        Assert.Contains("api/export/wealth-backup", portability);
        Assert.Contains("Accept: 'application/zip'", portability);
        Assert.Contains("cache: 'no-store'", portability);
        Assert.Contains("stopImmediatePropagation", portability);
        Assert.Matches(@"const\s+VERSION\s*=\s*'v\d+'", sw);
        Assert.Contains("'/features/wealth-portability.js'", sw);
        Assert.Contains("url.pathname.startsWith('/bff')", sw);
        Assert.DoesNotContain("/bff/backend/api/export/wealth-backup", sw);
    }

    [Fact]
    public async Task AccessibilityReleaseFixesAreLoadedLocalizedAndCached()
    {
        var wrapper = await GetAsync("/features/wealth-real-estate.js");
        var accessibility = await GetAsync("/ui/accessibility-release.js");
        var sw = await GetAsync("/sw.js");

        Assert.Contains("../ui/accessibility-release.js", wrapper);
        Assert.Contains("Buchungen durchsuchen", accessibility);
        Assert.Contains("Search transactions", accessibility);
        Assert.Contains("setAttribute('scope', 'col')", accessibility);
        Assert.Contains("setAttribute('aria-label', t.close)", accessibility);
        Assert.Contains("MutationObserver", accessibility);
        Assert.Contains("'/ui/accessibility-release.js'", sw);
    }

    [Fact]
    public async Task WealthStylesUseTokensAndAllModulesAreCached()
    {
        foreach (var path in new[]
                 {
                     "/features/wealth-assets.css",
                     "/features/wealth-real-estate.css",
                     "/features/wealth-real-estate-operations.css",
                     "/features/wealth-real-estate-advanced.css",
                     "/features/wealth-specialized-assets.css",
                     "/features/wealth-investment-consolidation.css"
                 })
        {
            var css = await GetAsync(path);
            Assert.Contains("var(--", css);
            Assert.DoesNotContain("linear-gradient", css);
        }
        var sw = await GetAsync("/sw.js");

        Assert.Matches(@"const\s+VERSION\s*=\s*'v\d+'", sw);
        foreach (var path in new[]
                 {
                     "'/features/networth.js'", "'/features/wealth-assets.css'",
                     "'/features/wealth-real-estate.js'", "'/features/wealth-real-estate-core.js'",
                     "'/features/wealth-real-estate-operations.js'", "'/features/wealth-real-estate-advanced.js'",
                     "'/features/wealth-real-estate.css'", "'/features/wealth-real-estate-operations.css'",
                     "'/features/wealth-specialized-assets.js'", "'/features/wealth-specialized-assets-extra.js'",
                     "'/features/wealth-specialized-assets.css'",
                     "'/features/wealth-investment-consolidation.js'",
                     "'/features/wealth-investment-consolidation.css'",
                     "'/features/wealth-portability.js'",
                     "'/features/investment-performance-ui.js'",
                     "'/investment-performance.css'",
                     "'/features/receipt-imports.js'",
                     "'/ui/accessibility-release.js'"
                 })
            Assert.Contains(path, sw);
    }

    private async Task<string> GetAsync(string path)
    {
        using var response = await client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }
}
