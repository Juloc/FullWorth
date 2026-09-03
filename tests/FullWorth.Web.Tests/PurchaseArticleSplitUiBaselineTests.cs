using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Web.Tests;

public sealed class PurchaseArticleSplitUiBaselineTests : IClassFixture<FullWorthWebFactory>
{
    private readonly FullWorthWebFactory factory;

    public PurchaseArticleSplitUiBaselineTests(FullWorthWebFactory factory) => this.factory = factory;

    [Fact]
    public void Transaction_split_exposes_all_non_zero_purchase_lines_including_discounts()
    {
        var js = ReadTransactions();

        Assert.Contains(".filter(item => Number(item.totalPrice || 0) !== 0)", js);
        Assert.Contains("['discount', 'coupon'].includes(lineType)", js);
        Assert.Contains("Rabatt/Gutschrift", js);
        Assert.DoesNotContain("!['discount', 'coupon'].includes(String(item.lineType", js);
    }

    [Fact]
    public void Suggested_article_amount_preserves_receipt_sign_before_applying_ledger_direction()
    {
        var js = ReadTransactions();

        Assert.Contains("Number(article.totalPrice || 0) * ledgerDirection", js);
        Assert.DoesNotContain("Math.abs(Number(article.totalPrice || 0)) * sign", js);
    }

    [Fact]
    public void Split_payload_keeps_purchase_item_identity()
    {
        var js = ReadTransactions();

        Assert.Contains("purchaseItemId: row.dataset.purchaseItemId || null", js);
        Assert.Contains("row.dataset.purchaseItemId = article?.id || ''", js);
    }

    private string ReadTransactions()
    {
        var environment = factory.Services.GetRequiredService<IWebHostEnvironment>();
        return File.ReadAllText(Path.Combine(environment.WebRootPath, "features", "transactions.js"));
    }
}
