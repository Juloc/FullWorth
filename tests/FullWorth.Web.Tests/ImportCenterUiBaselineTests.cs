using System.Reflection;
using FullWorth.Web.Modules.Import;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Web.Tests;

public sealed class ImportCenterUiBaselineTests : IClassFixture<FullWorthWebFactory>
{
    private readonly FullWorthWebFactory factory;
    private readonly HttpClient client;

    public ImportCenterUiBaselineTests(FullWorthWebFactory factory)
    {
        this.factory = factory;
        client = factory.CreateClient();
    }

    [Fact]
    public void ImportCenter_ExposesTransactionInvestmentFinanzguruAndPdfFlows()
    {
        var html = EmbeddedHtml(typeof(ImportCenterPageEndpoints));

        Assert.Contains("Daten importieren", html);
        Assert.Contains("data-import-mode=\"transactions\"", html);
        Assert.Contains("data-import-mode=\"investments\"", html);
        Assert.Contains("/settings/import/finanzguru/xlsx", html);
        Assert.Contains("/settings/import/broker-pdf", html);
        Assert.Contains("Finanzfluss Copilot", html);
        Assert.Contains("Outbank", html);
        Assert.Contains("Parqet", html);
        Assert.Contains("value=\"traderepublic\"", html);
        Assert.Contains("id=\"inv-new-portfolio-fields\"", html);
        Assert.Contains("id=\"inv-type-summary\"", html);
        Assert.Contains("id=\"inv-reconciliation\"", html);
        Assert.Contains("id=\"inv-history\"", html);
        Assert.Contains("Broker-PDF", html);
        Assert.DoesNotContain("disabled-provider", html);
    }

    [Fact]
    public void ImportPages_AreRegisteredAsProtectedWebRoutes()
    {
        var endpoints = factory.Services.GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText is not null)
            .ToLookup(endpoint => endpoint.RoutePattern.RawText!, StringComparer.OrdinalIgnoreCase);

        foreach (var route in new[]
                 {
                     "/settings/import",
                     "/settings/import/finanzguru",
                     "/settings/import/finanzguru/xlsx",
                     "/settings/import/broker-pdf"
                 })
        {
            var endpoint = Assert.Single(endpoints[route]);
            Assert.NotNull(endpoint.Metadata.GetMetadata<IAuthorizeData>());
        }
    }

    [Fact]
    public void FinanzguruProviderPage_ReturnsToImportCenter()
    {
        var html = EmbeddedHtml(typeof(FinanzguruImportPageEndpoints));

        Assert.Contains("id=\"finanzguru-form\"", html);
        Assert.Contains("id=\"import-link-list\"", html);
        Assert.Contains("href=\"/accounts\"", html);
        Assert.Contains("href=\"/settings/import\"", html);
    }

    [Fact]
    public void BrokerPdfProviderPage_UsesReviewBeforeCommit()
    {
        var html = EmbeddedHtml(typeof(BrokerPdfImportPageEndpoints));

        Assert.Contains("id=\"pdf-detect\"", html);
        Assert.Contains("id=\"pdf-stage\"", html);
        Assert.Contains("id=\"pdf-commit\"", html);
        Assert.Contains("href=\"/settings/import\"", html);
    }

    [Fact]
    public async Task ImportJavascript_UsesBackendBffOnlyAndRealPresetMappings()
    {
        foreach (var path in new[] { "/features/import-center-page.js", "/features/broker-pdf-import-page.js" })
        {
            using var response = await client.GetAsync(path);
            response.EnsureSuccessStatusCode();
            var js = await response.Content.ReadAsStringAsync();

            // Feature modules no longer build the '/bff/backend/...' URL themselves; the single
            // authenticated BFF-proxy client now lives in core/api.js and is reached here through
            // core/services.js. FrontendArchitectureGuardTests.NoNewFeatureMayCallBffDirectly forbids any
            // new file (neither of these is on its shrink-only allow-list) from reintroducing a raw
            // '/bff/(backend|banking)/' literal, so asserting indirection through the shared client is
            // the stronger, current form of this invariant.
            Assert.Contains("import { api as sharedApi } from '../core/services.js';", js);
            Assert.DoesNotContain("/bff/backend/", js);
            Assert.False(js.Contains("http://fullworth-backend", StringComparison.OrdinalIgnoreCase));
            Assert.False(js.Contains("X-FullWorth-Key", StringComparison.OrdinalIgnoreCase));
        }

        using var center = await client.GetAsync("/features/import-center-page.js");
        var centerJs = await center.Content.ReadAsStringAsync();
        Assert.Contains("api/import-mapping/detect", centerJs);
        Assert.Contains("api/investment-import/detect", centerJs);
        Assert.Contains("presetSuggestedMapping", centerJs);
        Assert.Contains("for(const alias of aliases)", centerJs);
        Assert.Contains("set('tradeDate','date','datetime')", centerJs);
        Assert.Contains("validationErrors", centerJs);
        Assert.Contains("preset==='outbank'", centerJs);
        Assert.Contains("preset==='finanzfluss'", centerJs);
        Assert.Contains("preset==='parqet'", centerJs);
        Assert.Contains("preset==='traderepublic'", centerJs);
        Assert.Contains("isTradeRepublicExport", centerJs);
        Assert.Contains("createPortfolio", centerJs);
        Assert.Contains("transaction_id", centerJs);
        Assert.Contains("assetClass", centerJs);
        Assert.Contains("sourceProvider='trade_republic'", centerJs);
        Assert.Contains("transactionTypes", centerJs);
        Assert.Contains("api/investment-import/history", centerJs);
        Assert.Contains("/rollback?", centerJs);
        Assert.Contains("renderInvestmentReconciliation", centerJs);
        Assert.Contains("identifier", centerJs);
        Assert.Contains("shares", centerJs);
        Assert.Contains("Auftraggeber/Empfänger", centerJs);
        Assert.Contains("tx-preset').addEventListener('change'", centerJs);
        Assert.Contains("inv-preset').addEventListener('change'", centerJs);

        using var pdf = await client.GetAsync("/features/broker-pdf-import-page.js");
        var pdfJs = await pdf.Content.ReadAsStringAsync();
        Assert.Contains("api/investment-import/pdf/detect", pdfJs);
        Assert.Contains("api/investment-import/pdf/ocr-detect", pdfJs);
        Assert.Contains("api/investment-import/upload", pdfJs);
        Assert.Contains("t.ocr", pdfJs);
    }

    [Fact]
    public async Task FinanzguruProviderPage_SupportsExplicitAccountLinkingAndBalanceAnchors()
    {
        using var response = await client.GetAsync("/features/finanzguru-import-page.js");
        response.EnsureSuccessStatusCode();
        var js = await response.Content.ReadAsStringAsync();

        Assert.Contains("api/import/finanzguru/accounts", js);
        Assert.Contains("/link?fullWorthSpaceId=", js);
        Assert.Contains("/confirm-history?fullWorthSpaceId=", js);
        Assert.Contains("currentBalance", js);
        Assert.Contains("/transactions?accountId=", js);
    }

    [Fact]
    public async Task CloudProviderImports_SnapshotFilesBeforeMultipartUpload()
    {
        using var security = await client.GetAsync("/security/browser-fetch.js");
        security.EnsureSuccessStatusCode();
        var securityJs = await security.Content.ReadAsStringAsync();
        Assert.Contains("snapshotUploadFile", securityJs);
        Assert.Contains("file.arrayBuffer()", securityJs);
        Assert.Contains("new File([bytes]", securityJs);

        // features/investment-import-ui.js was unreachable dead code, removed by "Remove unreachable
        // frontend patch layer"; the reachable investment-import flow now lives in
        // features/import-center-page.js (merged with transactions + broker-pdf under the Import
        // Center, see ImportJavascript_UsesBackendBffOnlyAndRealPresetMappings for its full mapping
        // coverage).
        foreach (var path in new[] { "/features/finanzguru-import-page.js", "/features/import-center-page.js" })
        {
            using var response = await client.GetAsync(path);
            response.EnsureSuccessStatusCode();
            var js = await response.Content.ReadAsStringAsync();
            // An upload must never append the live File to FormData: the picked file can change on disk
            // between validation and upload (TOCTOU), so it is copied to an immutable snapshot first.
            // The regression this used to flag is fixed, and fixed better than it was reported - the
            // protection is no longer a window.financeFileUpload global but the snapshotUploadFile
            // export of security/secure-fetch.js, imported as a module and applied inside formWithFile.
            // broker-pdf-import-page.js, named as having the same gap, does it the same way now too.
            Assert.Contains("snapshotUploadFile", js);
            Assert.Contains("security/secure-fetch.js", js);
            Assert.Matches(@"snapshotUploadFile\(file\)[\s\S]{0,200}new FormData\(\)", js);
            if (path.EndsWith("import-center-page.js", StringComparison.Ordinal))
            {
                Assert.Contains("Trade Republic", js);
                Assert.Contains("createPortfolio", js);
                Assert.Contains("assetClass", js);
                Assert.Contains("sourceProvider='trade_republic'", js);
                Assert.Contains("transactionTypes", js);
                Assert.Contains("renderInvestmentReconciliation", js);
                Assert.Contains("__new__", js);
            }
        }
    }

    private static string EmbeddedHtml(Type pageType)
    {
        var field = pageType.GetField("Html", BindingFlags.NonPublic | BindingFlags.Static);
        return Assert.IsType<string>(field?.GetRawConstantValue());
    }
}
