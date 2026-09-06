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

            Assert.Contains("/bff/backend/", js);
            Assert.False(js.Contains("http://fullworth-backend", StringComparison.OrdinalIgnoreCase));
            Assert.False(js.Contains("X-FullWorth-Key", StringComparison.OrdinalIgnoreCase));
        }

        using var center = await client.GetAsync("/features/import-center-page.js");
        var centerJs = await center.Content.ReadAsStringAsync();
        Assert.Contains("api/import-mapping/detect", centerJs);
        Assert.Contains("api/investment-import/detect", centerJs);
        Assert.Contains("presetSuggestedMapping", centerJs);
        Assert.Contains("preset==='outbank'", centerJs);
        Assert.Contains("preset==='finanzfluss'", centerJs);
        Assert.Contains("preset==='parqet'", centerJs);
        Assert.Contains("preset==='traderepublic'", centerJs);
        Assert.Contains("isTradeRepublicExport", centerJs);
        Assert.Contains("createPortfolio", centerJs);
        Assert.Contains("transaction_id", centerJs);
        Assert.Contains("assetClass", centerJs);
        Assert.Contains("sourceProvider='trade_republic'", centerJs);
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
    public async Task CloudProviderImports_SnapshotFilesBeforeMultipartUpload()
    {
        using var security = await client.GetAsync("/security/browser-fetch.js");
        security.EnsureSuccessStatusCode();
        var securityJs = await security.Content.ReadAsStringAsync();
        Assert.Contains("snapshotUploadFile", securityJs);
        Assert.Contains("file.arrayBuffer()", securityJs);
        Assert.Contains("new File([bytes]", securityJs);

        foreach (var path in new[] { "/features/finanzguru-import-page.js", "/features/investment-import-ui.js" })
        {
            using var response = await client.GetAsync(path);
            response.EnsureSuccessStatusCode();
            var js = await response.Content.ReadAsStringAsync();
            Assert.Contains("financeFileUpload?.snapshot", js);
            if (path.EndsWith("investment-import-ui.js", StringComparison.Ordinal))
            {
                Assert.Contains("Trade Republic", js);
                Assert.Contains("createPortfolio", js);
                Assert.Contains("assetClass", js);
                Assert.Contains("sourceProvider='trade_republic'", js);
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
