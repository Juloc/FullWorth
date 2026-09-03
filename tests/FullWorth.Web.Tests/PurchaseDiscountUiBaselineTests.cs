using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Web.Tests;

public sealed class PurchaseDiscountUiBaselineTests : IClassFixture<FullWorthWebFactory>
{
    private readonly FullWorthWebFactory factory;

    public PurchaseDiscountUiBaselineTests(FullWorthWebFactory factory) => this.factory = factory;

    [Fact]
    public void Advanced_installer_mounts_canonical_discount_editor()
    {
        var js = Read("features", "purchase-articles-advanced-installer.js");

        Assert.Contains("import { mountPurchaseDiscountActions } from './purchase-discount-actions.js';", js);
        Assert.Contains("await mountPurchaseDiscountActions", js);
        Assert.Contains("writable", js);
        Assert.Contains("refresh", js);
    }

    [Fact]
    public void Discount_editor_uses_discount_rows_instead_of_summary_mutations()
    {
        var js = Read("features", "purchase-discount-actions.js");

        Assert.Contains("api/purchases/${purchase.id}/discounts", js);
        Assert.Contains("method: row ? 'PATCH' : 'POST'", js);
        Assert.Contains("method: 'DELETE'", js);
        Assert.DoesNotContain("/summary", js);
        Assert.DoesNotContain("discountAmount:", js);
    }

    [Fact]
    public void Discount_editor_preserves_basket_and_item_assignment()
    {
        var js = Read("features", "purchase-discount-actions.js");

        Assert.Contains("purchaseItemId", js);
        Assert.Contains("Warenkorb / gesamter Kauf", js);
        Assert.Contains("Betrag gespart", js);
        Assert.Contains("amount > 0", js);
        Assert.Contains("Treue-/App-Rabatt", js);
        Assert.Contains("Coupon-Code", js);
    }

    [Fact]
    public void Discount_editor_exposes_source_and_confidence_without_treating_them_as_user_truth()
    {
        var js = Read("features", "purchase-discount-actions.js");

        Assert.Contains("row.confidence", js);
        Assert.Contains("sourceLabel(row.source)", js);
        Assert.Contains("manuellen Korrektur", js);
        Assert.Contains("AI-Confidence", js);
    }

    [Fact]
    public void Service_worker_precaches_discount_editor()
    {
        var sw = Read("sw.js");

        Assert.Contains("const VERSION = 'v55';", sw);
        Assert.Contains("/features/purchase-discount-actions.js", sw);
        Assert.DoesNotContain("/api/purchases", sw);
    }

    private string Read(params string[] path)
    {
        var environment = factory.Services.GetRequiredService<IWebHostEnvironment>();
        return File.ReadAllText(Path.Combine(new[] { environment.WebRootPath }.Concat(path).ToArray()));
    }
}
