using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Web.Tests;

public sealed class ReceiptImportUiBaselineTests : IClassFixture<FullWorthWebFactory>
{
    private readonly FullWorthWebFactory factory;

    public ReceiptImportUiBaselineTests(FullWorthWebFactory factory) => this.factory = factory;

    [Fact]
    public void Purchases_loads_bulk_receipt_import_module()
    {
        var normalScan = Read("features", "purchases-gpt-normal.js");
        var importer = Read("features", "receipt-imports.js");

        Assert.Contains("import './receipt-imports.js'", normalScan);
        Assert.Contains("import './receipt-import-batch-details.js'", normalScan);
        Assert.Contains("receipt-imports-launch", importer);
        Assert.Contains("Belege importieren", importer);
        Assert.Contains("type=\"file\" multiple", importer);
        Assert.Contains("api/purchases/receipt-imports/upload", importer);
    }

    [Fact]
    public void Import_source_tabs_cover_files_paperless_and_server_folder()
    {
        var importer = Read("features", "receipt-imports.js");

        Assert.Contains("data-tab=\"files\"", importer);
        Assert.Contains("data-tab=\"paperless\"", importer);
        Assert.Contains("data-tab=\"folder\"", importer);
        Assert.Contains("receipt-imports/paperless/options", importer);
        Assert.Contains("receipt-imports/paperless/preview", importer);
        Assert.Contains("receipt-imports/paperless/import", importer);
        Assert.Contains("receipt-imports/paperless/presets", importer);
        Assert.Contains("data-paperless-preset-auto", importer);
        Assert.Contains("Autoimport stündlich", importer);
        Assert.Contains("already imported", importer);
        Assert.Contains("data-paperless-add-filter", importer);
        Assert.Contains("data-rule-join", importer);
        Assert.Contains("data-rule-not", importer);
        Assert.Contains("data-rule-open", importer);
        Assert.Contains("data-rule-close", importer);
        Assert.Contains("buildPaperlessQuery", importer);
        Assert.Contains("receipt-imports/folder/status", importer);
        Assert.Contains("receipt-imports/folder/preview", importer);
        Assert.Contains("receipt-imports/folder/import", importer);
    }

    [Fact]
    public void Batch_ui_exposes_persistent_progress_recovery_and_review_navigation()
    {
        var importer = Read("features", "receipt-imports.js");
        var normalScan = Read("features", "purchases-gpt-normal.js");

        Assert.Contains("receipt-imports/batches?limit=10", importer);
        Assert.Contains("start-pending", importer);
        Assert.Contains("retry-failed", importer);
        Assert.Contains("batch.needsReview", importer);
        Assert.Contains("batch.skippedDuplicates", importer);
        Assert.Contains("setInterval", importer);
        Assert.Contains("data-review-import", normalScan);
        Assert.Contains("purchase-source", normalScan);
        Assert.Contains("receipt-import-dialog", normalScan);
    }

    [Fact]
    public void Batch_details_support_status_source_filters_and_individual_receipt_links()
    {
        var details = Read("features", "receipt-import-batch-details.js");
        var css = Read("features", "receipt-imports.css");
        var sw = Read("sw.js");

        Assert.Contains("data-import-batch-details", details);
        Assert.Contains("data-import-item-status-filter", details);
        Assert.Contains("data-import-item-source-filter", details);
        Assert.Contains("data-import-batch-item", details);
        Assert.Contains("api/purchases/${encodeURIComponent(purchaseId)}/receipt", details);
        Assert.Contains("target=\"_blank\"", details);
        Assert.Contains("receipt-import-batch-detail", css);
        Assert.Contains("/features/receipt-import-batch-details.js", sw);
    }

    [Fact]
    public void Bulk_upload_has_route_scoped_body_limit_and_receipt_rate_limit()
    {
        var environment = factory.Services.GetRequiredService<IWebHostEnvironment>();
        var program = File.ReadAllText(Path.Combine(environment.ContentRootPath, "Program.cs"));
        var settings = File.ReadAllText(Path.Combine(environment.ContentRootPath, "appsettings.json"));

        Assert.Contains("/bff/backend/api/purchases/receipt-imports/upload", program);
        Assert.Contains("IHttpMaxRequestBodySizeFeature", program);
        Assert.Contains("RateLimitPolicies.ReceiptUpload", program);
        Assert.Contains("ReceiptImports:MaxUploadBytes", program);
        Assert.DoesNotContain("\"Kestrel\"", settings);
        Assert.Contains("\"ReceiptImports\"", settings);
    }

    private string Read(params string[] path)
    {
        var environment = factory.Services.GetRequiredService<IWebHostEnvironment>();
        return File.ReadAllText(Path.Combine(new[] { environment.WebRootPath }.Concat(path).ToArray()));
    }
}
