using FullWorth.Backend.Modules.Purchases;

namespace FullWorth.Backend.Tests.Purchases;

public sealed class PurchaseDiscountReconciliationTests
{
    [Fact]
    public void Item_discount_is_already_embedded_in_effective_item_total()
    {
        var item = new PurchaseItem
        {
            Id = Guid.NewGuid(), Name = "Artikel", Quantity = 1m, OriginalUnitPrice = 10m,
            UnitPrice = 8m, TotalPrice = 8m, DiscountAmount = 2m, Currency = "EUR"
        };
        var discount = new PurchaseDiscount
        {
            PurchaseItemId = item.Id, Type = "price_reduction", Label = "2 EUR Rabatt", Amount = 2m
        };

        var result = PurchaseArticleCalculator.Reconcile(
            8m, [item], [discount], [], "EUR",
            subtotalAmount: 10m, declaredDiscountAmount: 2m, declaredDepositAmount: 0m, roundingAmount: 0m);

        Assert.Equal(8m, result.MerchandiseTotal);
        Assert.Equal(2m, result.ItemDiscountTotal);
        Assert.Equal(0m, result.BasketDiscountTotal);
        Assert.Equal(8m, result.ItemTotal);
        Assert.True(result.ItemsReconciled);
        Assert.True(result.FormulaReconciled);
    }

    [Fact]
    public void Basket_coupon_reduces_purchase_after_effective_item_totals()
    {
        var item = new PurchaseItem { Name = "Ware", Quantity = 1m, UnitPrice = 15m, TotalPrice = 15m, Currency = "EUR" };
        var coupon = new PurchaseDiscount { Type = "coupon", Label = "Coupon", Amount = 2m };

        var result = PurchaseArticleCalculator.Reconcile(
            13m, [item], [coupon], [], "EUR",
            subtotalAmount: 15m, declaredDiscountAmount: 2m, declaredDepositAmount: 0m, roundingAmount: 0m);

        Assert.Equal(15m, result.MerchandiseTotal);
        Assert.Equal(2m, result.BasketDiscountTotal);
        Assert.Equal(13m, result.ItemTotal);
        Assert.True(result.ItemsReconciled);
        Assert.True(result.FormulaReconciled);
    }

    [Fact]
    public void Deposit_is_separate_from_merchandise_total()
    {
        var item = new PurchaseItem
        {
            Name = "Getränk", Quantity = 1m, UnitPrice = 1m, TotalPrice = 1m,
            DepositAmount = .25m, Currency = "EUR"
        };

        var result = PurchaseArticleCalculator.Reconcile(
            1.25m, [item], [], [], "EUR",
            subtotalAmount: 1m, declaredDiscountAmount: 0m, declaredDepositAmount: .25m, roundingAmount: 0m);

        Assert.Equal(1m, result.MerchandiseTotal);
        Assert.Equal(.25m, result.DepositTotal);
        Assert.Equal(1.25m, result.ItemTotal);
        Assert.True(result.ItemsReconciled);
    }

    [Fact]
    public void Signed_rounding_participates_in_receipt_formula()
    {
        var item = new PurchaseItem { Name = "Ware", Quantity = 1m, TotalPrice = 9.99m, Currency = "EUR" };

        var result = PurchaseArticleCalculator.Reconcile(
            10m, [item], [], [], "EUR",
            subtotalAmount: 9.99m, declaredDiscountAmount: 0m, declaredDepositAmount: 0m, roundingAmount: .01m);

        Assert.Equal(.01m, result.RoundingAmount);
        Assert.Equal(10m, result.ItemTotal);
        Assert.Equal(10m, result.FormulaTotal);
        Assert.True(result.FullyReconciled);
    }

    [Fact]
    public void Shipping_is_included_in_subtotal_discount_formula()
    {
        var item = new PurchaseItem { Name = "Ware", Quantity = 1m, UnitPrice = 20m, TotalPrice = 20m, Currency = "EUR" };
        var coupon = new PurchaseDiscount { Type = "coupon", Label = "Aktionsgutschein", Amount = 2m };

        var result = PurchaseArticleCalculator.Reconcile(
            21m, [item], [coupon], [], "EUR",
            subtotalAmount: 20m, declaredDiscountAmount: 2m, declaredDepositAmount: 0m, roundingAmount: 0m,
            shippingAmount: 3m);

        Assert.Equal(3m, result.AdditionalChargeTotal);
        Assert.Equal(21m, result.ItemTotal);
        Assert.Equal(21m, result.FormulaTotal);
        Assert.True(result.ItemsReconciled);
        Assert.True(result.FormulaReconciled);
        Assert.True(result.FullyReconciled);
    }

    [Fact]
    public void Legacy_negative_coupon_line_is_supported_only_without_canonical_discounts()
    {
        var product = new PurchaseItem { Name = "Ware", Quantity = 1m, TotalPrice = 15m, Currency = "EUR" };
        var legacyCoupon = new PurchaseItem { Name = "Rabatt", Quantity = 1m, TotalPrice = -2m, Currency = "EUR", LineType = "discount" };

        var legacy = PurchaseArticleCalculator.Reconcile(
            13m, [product, legacyCoupon], [], [], "EUR",
            subtotalAmount: null, declaredDiscountAmount: 2m, declaredDepositAmount: 0m, roundingAmount: 0m);
        Assert.Equal(2m, legacy.BasketDiscountTotal);
        Assert.True(legacy.ItemsReconciled);

        var canonical = new PurchaseDiscount { Type = "coupon", Label = "Coupon", Amount = 2m };
        var migrated = PurchaseArticleCalculator.Reconcile(
            13m, [product, legacyCoupon], [canonical], [], "EUR",
            subtotalAmount: 15m, declaredDiscountAmount: 2m, declaredDepositAmount: 0m, roundingAmount: 0m);
        Assert.Equal(2m, migrated.BasketDiscountTotal);
        Assert.True(migrated.ItemsReconciled);
    }
}