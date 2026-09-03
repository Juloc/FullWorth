using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Web.Tests;

public sealed class AmazonPurchasesUiBaselineTests : IClassFixture<FullWorthWebFactory>
{
    private readonly FullWorthWebFactory factory;

    public AmazonPurchasesUiBaselineTests(FullWorthWebFactory factory) => this.factory = factory;

    [Fact]
    public void Purchases_ui_uses_account_sync_instead_of_manual_json_import()
    {
        var js = ReadPurchasesJs();

        Assert.Contains("api/purchases/amazon/connect/start", js);
        Assert.Contains("api/purchases/amazon/sync", js);
        Assert.Contains("data-sync-days=\"36500\"", js);
        Assert.DoesNotContain("itemsJson", js, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Editing_items_preserves_imported_amazon_metadata()
    {
        var js = ReadPurchasesJs();

        Assert.Contains("brand: original.brand ?? null", js);
        Assert.Contains("sku: original.sku ?? null", js);
        Assert.Contains("asin: original.asin ?? null", js);
        Assert.Contains("unitPrice: original.unitPrice ?? null", js);
    }

    [Fact]
    public void Amazon_detail_supports_allocated_payments_gift_balance_and_refunds()
    {
        var js = ReadPurchasesJs();

        Assert.Contains("amazon-payment-links", js);
        Assert.Contains("amazon-payment-candidates", js);
        Assert.Contains("allocatedAmount", js);
        Assert.Contains("suggestedAllocation", js);
        Assert.Contains("availableAmount", js);
        Assert.Contains("amazon-nonbank-payment", js);
        Assert.Contains("nonBankPaymentAmount", js);
        Assert.Contains("amazon-refunds/${refundId}/candidates", js);
        Assert.Contains("amazon-refunds/${refundId}/link", js);
        Assert.Contains("data-unlink-amazon-refund", js);
        Assert.Contains("amazon.refunds", js);
        Assert.Contains("ASIN", js);
    }

    [Fact]
    public void Gift_balance_only_amazon_order_can_leave_review_queue_when_confirmed()
    {
        var js = ReadPurchasesJs();

        Assert.Contains("r.source !== 'amazon' && !r.transactionId", js);
    }

    [Fact]
    public void Amazon_login_ui_does_not_persist_password_or_otp()
    {
        var js = ReadPurchasesJs();

        Assert.Contains("passwordBox.value = ''", js);
        Assert.Contains("amazonOtp", js);
        Assert.Contains(".value = ''; completeAmazonLogin", js);
        Assert.DoesNotContain("localStorage.setItem('amazon", js, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sessionStorage.setItem('amazon", js, StringComparison.OrdinalIgnoreCase);
    }

    private string ReadPurchasesJs()
    {
        var environment = factory.Services.GetRequiredService<IWebHostEnvironment>();
        return File.ReadAllText(Path.Combine(environment.WebRootPath, "features", "purchases.js"));
    }
}