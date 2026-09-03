using FullWorth.Backend.Modules.Purchases.Amazon;

namespace FullWorth.Backend.Tests.Purchases;

public sealed class AmazonDiscountParserTests
{
    [Fact]
    public void ExplicitPromotionAndCouponAreParsedAsDiscounts()
    {
        const string text = """
            Zwischensumme: 40,00 €
            Aktionsrabatt: -5,00 €
            Coupon angewendet: -3,00 €
            Gesamtsumme: 32,00 €
            """;

        Assert.Equal((40m, "EUR"), AmazonPageParser.FindSubtotal(text, "EUR"));
        var discounts = AmazonPageParser.FindDiscounts(text, "EUR", 32m);

        Assert.Equal(2, discounts.Count);
        Assert.Contains(discounts, x => x.Type == "promotion" && x.Amount == 5m);
        Assert.Contains(discounts, x => x.Type == "coupon" && x.Amount == 3m);
    }

    [Fact]
    public void GiftCardBalanceRefundAndReturnAreNeverDiscounts()
    {
        const string text = """
            Gesamtsumme: 50,00 €
            Geschenkgutschein-Guthaben: 20,00 €
            Gift Card applied 5,00 €
            Rückerstattung: 10,00 €
            Return discount-looking text: 4,00 €
            """;

        Assert.Equal(20m, AmazonPageParser.FindNonBankPaymentAmount(text, "EUR", 50m));
        Assert.Empty(AmazonPageParser.FindDiscounts(text, "EUR", 50m));
    }

    [Fact]
    public void GenericDiscountUsesOtherInsteadOfInventingPromotionType()
    {
        const string text = """
            Rabatt: -2,50 €
            Gesamtsumme: 17,50 €
            """;

        var discount = Assert.Single(AmazonPageParser.FindDiscounts(text, "EUR", 17.50m));
        Assert.Equal("other", discount.Type);
        Assert.Equal(2.50m, discount.Amount);
    }

    [Fact]
    public void DiscountHeadingDoesNotConsumeFollowingOrderTotal()
    {
        const string text = """
            Rabatt
            Gesamtsumme: 49,99 €
            """;

        Assert.Empty(AmazonPageParser.FindDiscounts(text, "EUR", 49.99m));
    }

    [Fact]
    public void DiscountHeadingMayUseAnAdjacentMoneyOnlyLine()
    {
        const string text = """
            Coupon angewendet
            -3,00 €
            Gesamtsumme: 27,00 €
            """;

        var discount = Assert.Single(AmazonPageParser.FindDiscounts(text, "EUR", 27m));
        Assert.Equal("coupon", discount.Type);
        Assert.Equal(3m, discount.Amount);
    }
}
