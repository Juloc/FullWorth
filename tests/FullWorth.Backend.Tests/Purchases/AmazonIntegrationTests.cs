using FullWorth.Backend.Modules.Purchases.Amazon;

namespace FullWorth.Backend.Tests.Purchases;

public sealed class AmazonIntegrationTests
{
    [Fact]
    public void Default_options_allow_effectively_full_amazon_history()
    {
        var options = new AmazonIntegrationOptions();

        Assert.Equal(90, options.InitialHistoryDays);
        Assert.Equal(36500, options.MaxHistoryDays);
        Assert.Equal(5000, options.MaxOrdersPerSync);
    }

    [Fact]
    public void Parser_extracts_german_order_metadata()
    {
        const string text = """
            Bestellt am 28. August 2026
            Bestellnummer 304-1234567-1234567
            Zugestellt
            Gesamtsumme: 79,97 €
            """;

        Assert.Equal("304-1234567-1234567", AmazonPageParser.FindOrderId(text));
        Assert.Equal(new DateOnly(2026, 8, 28), AmazonPageParser.FindPurchaseDate(text));
        Assert.Equal((79.97m, "EUR"), AmazonPageParser.FindOrderTotal(text));
        Assert.Equal("delivered", AmazonPageParser.FindExternalStatus(text));
    }

    [Fact]
    public void Parser_extracts_subtotal_shipping_and_promotion_without_using_previous_money_line()
    {
        const string text = """
            Artikel-Zwischensumme: 20,00 €
            Versand & Bearbeitung: 3,00 €
            Artikelpreis 99,00 €
            Aktionsgutschein
            -2,00 €
            Gesamtsumme: 21,00 €
            """;

        Assert.Equal((20m, "EUR"), AmazonPageParser.FindSubtotal(text, "EUR"));
        Assert.Equal((3m, "EUR"), AmazonPageParser.FindShippingAmount(text, "EUR"));
        var discount = Assert.Single(AmazonPageParser.FindDiscounts(text, "EUR", 21m));
        Assert.Equal("coupon", discount.Type);
        Assert.Equal(2m, discount.Amount);
        Assert.Contains("Aktionsgutschein", discount.Label, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("99,00", discount.RawText ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void Parser_extracts_gift_balance_without_confusing_it_with_a_discount_or_gift_card_product()
    {
        const string paymentText = """
            Gesamtsumme: 79,97 €
            Geschenkgutschein-Guthaben: 20,00 €
            Belastung Visa: 59,97 €
            """;
        const string productText = """
            Gesamtsumme: 50,00 €
            Amazon Geschenkgutschein Geburtstag
            50,00 €
            """;

        Assert.Equal(20m, AmazonPageParser.FindNonBankPaymentAmount(paymentText, "EUR", 79.97m));
        Assert.Empty(AmazonPageParser.FindDiscounts(paymentText, "EUR", 79.97m));
        Assert.Equal(0m, AmazonPageParser.FindNonBankPaymentAmount(productText, "EUR", 50m));
        Assert.Empty(AmazonPageParser.FindDiscounts(productText, "EUR", 50m));
    }

    [Theory]
    [InlineData("https://www.amazon.de/dp/B0ABC12345/ref=x", "B0ABC12345")]
    [InlineData("/gp/product/B012345678?psc=1", "B012345678")]
    public void Parser_extracts_asin(string href, string expected)
    {
        Assert.Equal(expected, AmazonPageParser.FindAsin(href));
    }

    [Fact]
    public void Parser_extracts_refund_without_creating_duplicate_ids()
    {
        const string orderId = "304-1234567-1234567";
        const string text = """
            Rückerstattung
            29. August 2026
            19,99 €
            """;

        var first = AmazonPageParser.FindRefunds(orderId, text, "EUR");
        var second = AmazonPageParser.FindRefunds(orderId, text, "EUR");

        var refund = Assert.Single(first);
        Assert.Equal(19.99m, refund.Amount);
        Assert.Equal("EUR", refund.Currency);
        Assert.Equal(new DateOnly(2026, 8, 29), refund.RefundDate);
        Assert.Equal(refund.ExternalRefundId, Assert.Single(second).ExternalRefundId);
    }

    [Fact]
    public void Payment_matcher_finds_split_amazon_charge()
    {
        var date = new DateOnly(2026, 8, 28);
        var candidates = new[]
        {
            new AmazonPaymentCandidate(Guid.NewGuid(), -29.99m, date.AddDays(1), "AMAZON EU"),
            new AmazonPaymentCandidate(Guid.NewGuid(), -34.99m, date.AddDays(2), "AMZN Mktp DE"),
            new AmazonPaymentCandidate(Guid.NewGuid(), -14.99m, date.AddDays(4), "Amazon"),
            new AmazonPaymentCandidate(Guid.NewGuid(), -79.98m, date.AddDays(1), "Amazon")
        };

        var result = AmazonPurchaseMatchingService.FindBestCombination(candidates, 79.97m, date);

        Assert.Equal(3, result.Count);
        Assert.Equal(79.97m, result.Sum(x => Math.Abs(x.Amount)));
    }

    [Fact]
    public void Payment_matcher_does_not_accept_inexact_partial_charge()
    {
        var date = new DateOnly(2026, 8, 28);
        var candidates = new[]
        {
            new AmazonPaymentCandidate(Guid.NewGuid(), -20m, date, "Amazon"),
            new AmazonPaymentCandidate(Guid.NewGuid(), -30m, date, "Amazon")
        };

        Assert.Empty(AmazonPurchaseMatchingService.FindBestCombination(candidates, 79.97m, date));
    }

    [Fact]
    public void Payment_matcher_never_uses_positive_refund_as_purchase_payment()
    {
        var date = new DateOnly(2026, 8, 28);
        var candidates = new[]
        {
            new AmazonPaymentCandidate(Guid.NewGuid(), 29.99m, date.AddDays(2), "Amazon Erstattung"),
            new AmazonPaymentCandidate(Guid.NewGuid(), -29.99m, date.AddDays(2), "Amazon")
        };

        var result = AmazonPurchaseMatchingService.FindBestCombination(candidates, 29.99m, date);

        var payment = Assert.Single(result);
        Assert.True(payment.Amount < 0);
    }

    [Fact]
    public void Combined_matcher_can_allocate_one_bank_charge_to_multiple_orders()
    {
        var date = new DateOnly(2026, 8, 28);
        var candidates = new[]
        {
            new AmazonPurchaseRemainder(Guid.NewGuid(), 29.99m, date, "EUR"),
            new AmazonPurchaseRemainder(Guid.NewGuid(), 49.98m, date.AddDays(1), "EUR"),
            new AmazonPurchaseRemainder(Guid.NewGuid(), 12.50m, date, "EUR")
        };

        var result = AmazonPurchaseMatchingService.FindUniquePurchaseCombination(candidates, 79.97m);

        Assert.Equal(2, result.Count);
        Assert.Equal(79.97m, result.Sum(x => x.Amount));
    }

    [Fact]
    public void Combined_matcher_rejects_ambiguous_order_combinations()
    {
        var date = new DateOnly(2026, 8, 28);
        var candidates = new[]
        {
            new AmazonPurchaseRemainder(Guid.NewGuid(), 20m, date, "EUR"),
            new AmazonPurchaseRemainder(Guid.NewGuid(), 30m, date, "EUR"),
            new AmazonPurchaseRemainder(Guid.NewGuid(), 10m, date, "EUR"),
            new AmazonPurchaseRemainder(Guid.NewGuid(), 40m, date, "EUR")
        };

        Assert.Empty(AmazonPurchaseMatchingService.FindUniquePurchaseCombination(candidates, 50m));
    }
}