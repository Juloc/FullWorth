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
    public async Task WealthTrendShowsImportedBookingCoverageWithoutTreatingItAsNetWorth()
    {
        var js = await GetAsync("/features/networth.js");
        var css = await GetAsync("/app.css");
        Assert.Contains("api/wealth/booking-activity", js);
        Assert.Contains("bookingActivityMarkup", js);
        Assert.Contains("parseChartDate(point.date)", js);
        Assert.Contains("nw-chart-activity", js);
        Assert.Contains("bookingHistoryHint", js);
        Assert.Contains(".nw-chart-activity", css);
        Assert.Contains(".nw-booking-history-hint", css);
    }

    [Fact]
    public async Task ImportedDataCompletenessWarningsReachWealthAnalyticsAndDashboard()
    {
        var helper = await GetAsync("/features/data-completeness.js");
        var wealth = await GetAsync("/features/networth.js");
        var analytics = await GetAsync("/features/analytics.js");
        var dashboard = await GetAsync("/ui/dashboard.js");
        var css = await GetAsync("/app.css");

        Assert.Contains("api/import/finanzguru/accounts", helper);
        Assert.Contains("needsBalance", helper);
        Assert.Contains("Kontostand ergänzen", helper);
        Assert.Contains("Importkonto verbinden", helper);
        Assert.Contains("Historie bestätigen", helper);
        Assert.Contains("loadFinanzguruCompleteness", wealth);
        Assert.Contains("finanzguruCompletenessNotice", wealth);
        Assert.Contains("bookingOnlyHint", wealth);
        Assert.Contains("loadFinanzguruCompleteness", analytics);
        Assert.Contains("scope: 'analytics'", analytics);
        Assert.Contains("loadFinanzguruCompleteness", dashboard);
        Assert.Contains("dashboard-data-completeness", dashboard);
        Assert.Contains(".data-completeness-warning", css);
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
        var css = await GetAsync("/styles/features/wealth-specialized-assets.css");

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
        var css = await GetAsync("/styles/features/wealth-investment-consolidation.css");

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
        // stopImmediatePropagation is gone: it used to guard against a legacy patch-layer handler
        // double-firing alongside the real one. app.js now wires #export-data to downloadWealthBackup
        // with a single owned listener, and FrontendArchitectureGuardTests.NoNewPatchLayerFileNames
        // blocks reintroducing that kind of patch layer, so the double-fire this guarded against is
        // structurally impossible now.
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
                     "/styles/features/wealth-assets.css",
                     "/styles/features/wealth-real-estate.css",
                     "/styles/features/wealth-real-estate-operations.css",
                     "/styles/features/wealth-real-estate-advanced.css",
                     "/styles/features/wealth-specialized-assets.css",
                     "/styles/features/wealth-investment-consolidation.css"
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
                     "'/features/networth.js'", "'/styles/features/wealth-assets.css'",
                     "'/features/wealth-real-estate.js'", "'/features/wealth-real-estate-core.js'",
                     "'/features/wealth-real-estate-operations.js'", "'/features/wealth-real-estate-advanced.js'",
                     "'/styles/features/wealth-real-estate.css'", "'/styles/features/wealth-real-estate-operations.css'",
                     "'/features/wealth-specialized-assets.js'", "'/features/wealth-specialized-assets-extra.js'",
                     "'/styles/features/wealth-specialized-assets.css'",
                     "'/features/wealth-investment-consolidation.js'",
                     "'/styles/features/wealth-investment-consolidation.css'",
                     "'/features/wealth-portability.js'",
                     "'/features/investment-performance-ui.js'",
                     "'/styles/features/investment-performance.css'",
                     "'/features/receipt-imports.js'",
                     "'/ui/accessibility-release.js'"
                 })
            Assert.Contains(path, sw);
    }

    [Fact]
    public async Task WealthProjectionIsACalculationNotAForecastAndIsTranslatedBothWays()
    {
        var js = await GetAsync("/features/networth.js");
        var css = await GetAsync("/app.css");

        // Monthly compounding, because the savings arrive monthly. A yearly formula would silently
        // overstate the growth on the payments made during the current year.
        Assert.Contains("/ 100 / 12", js);
        Assert.Contains("value = value * (1 + rate) + monthlySavings", js);

        // The inputs and the breakdown are what make it a calculation rather than a promise: paid in,
        // growth, purchasing power. Drop those and the preview becomes a number nobody can check.
        Assert.Contains("data-projection-savings", js);
        Assert.Contains("data-projection-return", js);
        Assert.Contains("data-projection-inflation", js);
        Assert.Contains("projectionContributed", js);
        Assert.Contains("projectionGrowth", js);
        Assert.Contains("projectionReal", js);
        Assert.Contains(".nw-projection-note", css);

        // Wording: it must say what it is, in both languages. This is a calculator, not advice.
        Assert.Contains("keine Anlageempfehlung", js);
        Assert.Contains("not investment advice", js);

        // A key present in only one language table renders as the bare key name for that language.
        var used = System.Text.RegularExpressions.Regex.Matches(js, @"t\('(projection[A-Za-z]*)'\)")
            .Select(match => match.Groups[1].Value).Distinct().ToArray();
        Assert.NotEmpty(used);
        foreach (var key in used)
        {
            var declarations = System.Text.RegularExpressions.Regex.Matches(js, $@"\b{key}:").Count;
            Assert.True(declarations == 2,
                $"{key} is declared {declarations} time(s); it must appear exactly once per language table.");
        }
    }

    [Fact]
    public async Task WealthProjectionIsDrawnIntoTheTrendChartAndNotAsASecondRepresentation()
    {
        var js = await GetAsync("/features/networth.js");
        var css = await GetAsync("/app.css");

        // The preview is the trend curve continued past today: ONE chart, one value scale, a dashed
        // forward segment and a "today" divider. The old tile drew a second chart of the same numbers.
        Assert.Contains("nw-chart-forecast", js);
        Assert.Contains("nw-chart-today", js);
        Assert.Contains("${forecastMarkup()}", js);
        Assert.DoesNotContain("buildProjectionCard", js);
        Assert.DoesNotContain("nw-projection-card", js);
        Assert.DoesNotContain("nw-projection-chart", js);
        Assert.Contains(".nw-chart-forecast{stroke-dasharray", css);

        // Both halves go through the geometry's own value scale, so the projected part cannot be drawn
        // on a scale of its own that makes it look like a measurement standing next to the history.
        Assert.Contains("y: yFor(point.value)", js);
        Assert.Contains("values.concat(forecast ? forecast.points.map(point => point.value) : [])", js);

        // Configurable, immediately, and still persisted in the existing preference - including the
        // horizon, where 0 years means "off" rather than a new preference field.
        Assert.Contains("api/preferences/wealth.projection", js);
        Assert.Contains("data-projection-years=", js);
        Assert.Contains("projectionOff", js);
        Assert.Contains("addEventListener('input'", js);

        // The savings default follows the loaded window, so the preview cannot keep quoting a range
        // that is no longer on screen.
        Assert.Contains("forecastEl.outerHTML = forecastMarkup()", js);
    }

    [Fact]
    public async Task WealthProjectionNeverReadsAsAMeasuredValue()
    {
        var js = await GetAsync("/features/networth.js");
        var css = await GetAsync("/app.css");

        // Scrubbing a projected point must leave the headline net worth alone: it reads out in the
        // preview line, with its own marker, and says that it was calculated rather than measured.
        Assert.Contains("data-forecast-readout", js);
        Assert.Contains("point.projected", js);
        Assert.Contains("projectionNotMeasured", js);
        Assert.Contains("className: 'projected'", js);
        Assert.Contains(".fw-chart-scrub-marker.projected", css);

        // The assumption travels with the curve instead of hiding behind a disclosure.
        Assert.Contains("projectionAssumption", js);
        Assert.Contains("nw-forecast-assumption", js);

        // Number(null) is 0 AND finite, so that filter let an unknown net worth through as a zero -
        // into the curve, into the trend delta and (worst) as the anchor of the projection.
        Assert.Contains("function measuredValue(", js);
        Assert.DoesNotContain("Number.isFinite(Number(point.netWorth))", js);
    }

    [Fact]
    public async Task AllocationDonutNeverClaimsTheHeroAssetsWording()
    {
        var js = await GetAsync("/features/networth.js");
        var css = await GetAsync("/app.css");

        // A donut cannot draw a negative slice, so its own total only ever covers the categories it can
        // show. Labelling that total "Vermögenswerte"/"Assets" - the same wording the hero card uses for
        // a total that DOES include a negative category - put two different numbers on screen under the
        // identical name. The donut must use its own, differently worded label instead.
        Assert.Contains("assetMix: 'Anlagemix'", js);
        Assert.Contains("assetMix: 'Asset mix'", js);
        Assert.Contains("donutSvg(segments, assetSum, t('assetMix'), currency)", js);
        Assert.DoesNotContain("wealthCap", js);

        // When a category nets negative it is dropped from the ring - never added back in as a positive
        // amount, never subtracted from the ring's total either - and the page says so instead of
        // silently presenting a smaller, unexplained "asset mix" number.
        Assert.Contains("hasHiddenNegative", js);
        Assert.Contains("nw-alloc-note", js);
        Assert.Contains(".nw-alloc-note", css);
    }

    private async Task<string> GetAsync(string path)
    {
        using var response = await client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }
}
