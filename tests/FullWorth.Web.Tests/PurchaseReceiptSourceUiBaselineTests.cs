using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Web.Tests;

public sealed class PurchaseReceiptSourceUiBaselineTests : IClassFixture<FullWorthWebFactory>
{
    private readonly FullWorthWebFactory factory;

    public PurchaseReceiptSourceUiBaselineTests(FullWorthWebFactory factory) => this.factory = factory;

    [Fact]
    public void Purchase_dialog_mounts_multi_page_source_review()
    {
        var installer = Read("features", "purchase-articles-advanced-installer.js");
        var review = Read("features", "purchase-receipt-source-review.js");

        Assert.Contains("mountReceiptSourceReview", installer);
        Assert.Contains("api/purchases/${purchase.id}/receipt-sources", review);
        Assert.Contains("duplicateWarnings", review);
        Assert.Contains("itemSources", review);
        Assert.Contains("#page=${page}&view=FitH", review);
        Assert.Contains("data-source-index", review);
        Assert.Contains("role=\"alert\"", review);
    }

    [Fact]
    public void Source_review_uses_authenticated_bff_for_document_content()
    {
        var review = Read("features", "purchase-receipt-source-review.js");

        Assert.Contains("/bff/backend/", review);
        Assert.DoesNotContain("fetch('http", review, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Pwa_share_target_accepts_images_and_pdfs_for_one_receipt_flow()
    {
        var manifest = Read("manifest.json");
        Assert.Contains("\"share_target\"", manifest);
        Assert.Contains("\"action\": \"/share/receipt\"", manifest);
        Assert.Contains("\"name\": \"receipt\"", manifest);
        Assert.Contains("application/pdf", manifest);
        Assert.Contains("image/jpeg", manifest);
    }

    [Fact]
    public void Service_worker_precaches_review_and_bulk_import_modules_but_never_caches_sensitive_content()
    {
        var sw = Read("sw.js");

        Assert.Contains("const VERSION = 'v55'", sw);
        Assert.Contains("/features/purchase-receipt-source-review.js", sw);
        Assert.Contains("/features/receipt-imports.js", sw);
        Assert.Contains("/features/receipt-import-batch-details.js", sw);
        Assert.Contains("/features/receipt-imports.css", sw);
        Assert.Contains("url.pathname.startsWith('/share')", sw);
        Assert.Contains("url.pathname.startsWith('/bff')", sw);
    }

    private string Read(params string[] path)
    {
        var environment = factory.Services.GetRequiredService<IWebHostEnvironment>();
        return File.ReadAllText(Path.Combine(new[] { environment.WebRootPath }.Concat(path).ToArray()));
    }
}
