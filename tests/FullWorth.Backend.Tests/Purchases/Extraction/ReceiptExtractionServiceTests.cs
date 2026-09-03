using FullWorth.Backend.Modules.Purchases.Extraction;
using Microsoft.Extensions.Options;

namespace FullWorth.Backend.Tests.Purchases.Extraction;

public sealed class ReceiptExtractionServiceTests
{
    private static readonly ReceiptExtractionRequest Request = new([1, 2, 3], "application/pdf", "receipt.pdf");

    [Fact]
    public void Normalize_CanonicalizesFieldsAndItems()
    {
        var raw = new ReceiptExtractionResult(
            Provider: "test",
            Merchant: "  Rewe  ",
            PurchaseDate: new DateOnly(2026, 8, 2),
            Currency: "eur",
            Total: 12.999m,
            Discounts: -1.005m,
            Deposits: null,
            Taxes: 2m,
            Items:
            [
                new ReceiptLineItem("  Milk  ", 2m, 1.50m, null, "  groceries  ", 1.5m),
                new ReceiptLineItem("   ", 1m, 9.99m, 9.99m, null, 0.9m),
            ],
            Confidence: -0.2m);

        var result = ReceiptExtractionService.Normalize(raw);

        Assert.Equal("Rewe", result.Merchant);
        Assert.Equal("EUR", result.Currency);
        Assert.Equal(13.00m, result.Total);
        Assert.Equal(1.01m, result.Discounts);
        Assert.Equal(0m, result.Confidence);
        var item = Assert.Single(result.Items);
        Assert.Equal("Milk", item.Name);
        Assert.Equal("groceries", item.CategoryHint);
        Assert.Equal(3.00m, item.TotalPrice);
        Assert.Equal(1m, item.Confidence);
    }

    [Fact]
    public void Normalize_CanonicalizesStructuredDiscountsAndSignedRounding()
    {
        var raw = ReceiptExtractionResult.Empty("test") with
        {
            Currency = "eur",
            Rounding = -0.006m,
            StructuredDiscounts =
            [
                new ReceiptDiscount("BOGUS", "  Save  ", -2.005m, 150m, " X ", " raw ", 2m, -1)
            ]
        };

        var result = ReceiptExtractionService.Normalize(raw);
        var discount = Assert.Single(result.StructuredDiscounts!);

        Assert.Equal("other", discount.Type);
        Assert.Equal("Save", discount.Label);
        Assert.Equal(2.01m, discount.Amount);
        Assert.Equal(100m, discount.Percentage);
        Assert.Equal("X", discount.CouponCode);
        Assert.Equal("raw", discount.RawText);
        Assert.Equal(1m, discount.Confidence);
        Assert.Null(discount.ItemIndex);
        Assert.Equal(2.01m, result.Discounts);
        Assert.Equal(-0.01m, result.Rounding);
    }

    [Fact]
    public void Normalize_RemovesProviderAdjustmentRowsWithoutDoubleCountingAggregates()
    {
        var raw = ReceiptExtractionResult.Empty("legacy") with
        {
            Deposits = .25m,
            Shipping = 1m,
            Fees = .10m,
            Tip = .50m,
            Discounts = 2m,
            Items =
            [
                new ReceiptLineItem("Product", 1m, 10m, 10m, null, .9m, LineType: "product"),
                new ReceiptLineItem("Pfand", 1m, null, .25m, null, .8m, DepositAmount: .25m, LineType: "deposit"),
                new ReceiptLineItem("Coupon", 1m, null, -2m, null, .8m, DiscountAmount: 2m, DiscountLabel: "Coupon", LineType: "discount"),
                new ReceiptLineItem("Versand", 1m, null, 1m, null, .8m, LineType: "shipping"),
                new ReceiptLineItem("Fee", 1m, null, .10m, null, .8m, LineType: "fee"),
                new ReceiptLineItem("Tip", 1m, null, .50m, null, .8m, LineType: "tip")
            ]
        };

        var result = ReceiptExtractionService.Normalize(raw);

        var product = Assert.Single(result.Items);
        Assert.Equal("Product", product.Name);
        Assert.Equal(.25m, result.Deposits);
        Assert.Equal(1m, result.Shipping);
        Assert.Equal(.10m, result.Fees);
        Assert.Equal(.50m, result.Tip);
        Assert.Equal(2m, result.Discounts);
        var discount = Assert.Single(result.StructuredDiscounts!);
        Assert.Equal(2m, discount.Amount);
    }

    [Fact]
    public void Normalize_DerivesMissingAdjustmentAggregatesFromLegacyRowsAndRemapsDiscountItemIndex()
    {
        var raw = ReceiptExtractionResult.Empty("legacy") with
        {
            Items =
            [
                new ReceiptLineItem("Pfand", 1m, null, .25m, null, .8m, LineType: "deposit"),
                new ReceiptLineItem("Cola", 1m, 1.99m, 1.99m, null, .9m, DiscountAmount: .50m, DiscountLabel: "App", LineType: "product"),
                new ReceiptLineItem("Shipping", 1m, null, 1m, null, .8m, LineType: "shipping")
            ],
            StructuredDiscounts = [new ReceiptDiscount("loyalty", "App", .50m, Confidence: .9m, ItemIndex: 1)]
        };

        var result = ReceiptExtractionService.Normalize(raw);

        var product = Assert.Single(result.Items);
        Assert.Equal("Cola", product.Name);
        Assert.Equal(.25m, result.Deposits);
        Assert.Equal(1m, result.Shipping);
        Assert.Equal(.50m, result.Discounts);
        var discount = Assert.Single(result.StructuredDiscounts!);
        Assert.Equal(0, discount.ItemIndex);
    }

    [Theory]
    [InlineData("eur", "EUR")]
    [InlineData("USD", "USD")]
    [InlineData("EU", null)]
    [InlineData("EURO", null)]
    [InlineData("", null)]
    public void Normalize_OnlyKeepsThreeLetterCurrency(string input, string? expected)
    {
        var raw = ReceiptExtractionResult.Empty("test") with { Currency = input };
        Assert.Equal(expected, ReceiptExtractionService.Normalize(raw).Currency);
    }

    [Fact]
    public async Task Extract_WithNoConfiguredProvider_ReturnsEmpty()
    {
        var service = Service(provider: "none", new NullReceiptExtractor());
        var result = await service.ExtractAsync(Request, CancellationToken.None);

        Assert.Equal("none", service.ActiveProvider);
        Assert.False(service.IsProviderAvailable);
        Assert.Empty(result.Items);
        Assert.Null(result.Merchant);
    }

    [Fact]
    public async Task Extract_WithUnknownProvider_FallsBackToEmpty()
    {
        var service = Service(provider: "ghost", new NullReceiptExtractor(), new FakeExtractor("test"));
        var result = await service.ExtractAsync(Request, CancellationToken.None);

        Assert.Equal("none", service.ActiveProvider);
        Assert.Equal("ghost", result.Provider);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task Extract_WithConfiguredProvider_RunsAndNormalizes()
    {
        var fake = new FakeExtractor("test", ReceiptExtractionResult.Empty("test") with { Merchant = " Aldi ", Currency = "eur", Confidence = 2m });
        var service = Service(provider: "test", new NullReceiptExtractor(), fake);

        var result = await service.ExtractAsync(Request, CancellationToken.None);

        Assert.True(service.IsProviderAvailable);
        Assert.Equal("Aldi", result.Merchant);
        Assert.Equal("EUR", result.Currency);
        Assert.Equal(1m, result.Confidence);
    }

    private static ReceiptExtractionService Service(string provider, params IReceiptExtractor[] extractors) =>
        new(extractors, Options.Create(new ReceiptExtractionOptions { Provider = provider }));

    private sealed class FakeExtractor(string provider, ReceiptExtractionResult? result = null) : IReceiptExtractor
    {
        private readonly ReceiptExtractionResult result = result ?? ReceiptExtractionResult.Empty(provider);
        public string Provider => provider;
        public Task<ReceiptExtractionResult> ExtractAsync(ReceiptExtractionRequest request, CancellationToken ct) => Task.FromResult(result);
    }
}