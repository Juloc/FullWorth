using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Web.Tests;

public sealed class PurchaseAdvancedActionsUiBaselineTests : IClassFixture<FullWorthWebFactory>
{
    private readonly FullWorthWebFactory factory;

    public PurchaseAdvancedActionsUiBaselineTests(FullWorthWebFactory factory) => this.factory = factory;

    [Fact]
    public void Normal_purchase_scan_loads_advanced_workspace_installer()
    {
        var js = Read("features", "purchases-gpt-normal.js");
        Assert.Contains("import './purchase-articles-advanced-installer.js';", js);
    }

    [Fact]
    public void Purchase_workspace_exposes_tags_returns_documents_ocr_and_exports()
    {
        var js = Read("features", "purchase-articles-advanced-actions.js");

        Assert.Contains("api/purchases/${purchase.id}/tags", js);
        Assert.Contains("/items/${item.id}/returns", js);
        Assert.Contains("/documents/${doc.id}/extract", js);
        Assert.Contains("/apply-extraction/${runId}", js);
        Assert.Contains("api/purchases/export?format=", js);
        Assert.Contains("api/purchases/warranty/upcoming", js);
        Assert.Contains("data-document-type", js);
    }

    [Fact]
    public void Product_workspace_exposes_alias_barcode_archive_merge_and_camera_scan()
    {
        var js = Read("features", "purchase-articles-advanced-actions.js");

        Assert.Contains("/aliases", js);
        Assert.Contains("/barcodes", js);
        Assert.Contains("api/products/merge", js);
        Assert.Contains("BarcodeDetector", js);
        Assert.Contains("getUserMedia", js);
        Assert.Contains("data-product-archive", js);
    }

    [Fact]
    public void Advanced_installer_reuses_existing_workspace_instead_of_replacing_it()
    {
        var js = Read("features", "purchase-articles-advanced-installer.js");

        Assert.Contains(".pa-workspace", js);
        Assert.Contains(".pa-product-detail", js);
        Assert.Contains("api/purchases/${id}/workspace", js);
        Assert.Contains("mountPurchaseAdvancedActions", js);
        Assert.Contains("mountProductAdvancedActions", js);
        Assert.Contains("MutationObserver", js);
    }

    [Fact]
    public void Payment_picker_never_relabels_foreign_currency_transactions()
    {
        var js = Read("features", "purchase-articles-advanced-installer.js");
        var css = Read("features", "purchase-articles-workspace.css");

        Assert.Contains("mountCurrencySafePaymentPicker", js);
        Assert.Contains("FX-Konvertierung erforderlich", js);
        Assert.Contains("data-currency", js);
        Assert.Contains("currency: button.dataset.currency", js);
        Assert.Contains(".pa-fx-blocked", css);
    }

    [Fact]
    public void Payment_picker_does_not_offer_another_amount_when_purchase_is_fully_linked()
    {
        var js = Read("features", "purchase-articles-advanced-installer.js");

        Assert.Contains("const fullyLinked = remaining <= 0.005", js);
        Assert.Contains("Der Kauf ist bereits vollständig mit Zahlungen verknüpft.", js);
        Assert.Contains("Math.min(Math.abs(Number(row.amount || 0)), remaining)", js);
        Assert.DoesNotContain("remaining || Math.abs(Number(row.amount || 0))", js);
    }

    [Fact]
    public void Advanced_styles_never_use_hover_transforms()
    {
        var css = Read("features", "purchase-articles-workspace.css");
        Assert.Contains(".pa-export-actions .button:hover{transform:none!important", css);
        Assert.Contains(".pa-chip-remove:hover{transform:none!important", css);
        Assert.Contains(".pa-fx-blocked:hover{transform:none!important", css);
    }

    [Fact]
    public void Service_worker_precaches_article_modules_without_blocking_receipt_scan_assets()
    {
        var sw = Read("sw.js");

        Assert.Contains("/features/purchase-articles-workspace.js", sw);
        Assert.Contains("/features/purchase-articles-workspace.css", sw);
        Assert.Contains("/features/purchase-articles-advanced-installer.js", sw);
        Assert.Contains("/features/purchase-articles-advanced-actions.js", sw);
        Assert.Contains("/features/receipt-scan-ai.js", sw);
        Assert.DoesNotContain("url.pathname.includes('/receipt')", sw);
    }

    private string Read(params string[] path)
    {
        var environment = factory.Services.GetRequiredService<IWebHostEnvironment>();
        return File.ReadAllText(Path.Combine(new[] { environment.WebRootPath }.Concat(path).ToArray()));
    }
}
