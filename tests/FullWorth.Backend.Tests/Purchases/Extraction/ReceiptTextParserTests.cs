using FullWorth.Backend.Modules.Purchases.Extraction;

namespace FullWorth.Backend.Tests.Purchases.Extraction;

public sealed class ReceiptTextParserTests
{
    private const string GermanReceipt = """
        REWE Markt GmbH
        Musterstrasse 1, 10115 Berlin
        Datum: 02.08.2026 14:33

        Bio Milch 1,49
        Butter 2,29
        Brot 500g 1,99
        Kaffee 6,99
        ------------------
        Zwischensumme 12,76
        MwSt 7% 0,89
        SUMME EUR 12,76
        Gegeben BAR 20,00
        Rueckgeld 7,24
        """;

    [Fact]
    public void Parse_ExtractsMerchantDateCurrencyTotalAndItems()
    {
        var result = ReceiptTextParser.Parse(GermanReceipt);

        Assert.Equal("REWE Markt GmbH", result.Merchant);
        Assert.Equal(new DateOnly(2026, 8, 2), result.PurchaseDate);
        Assert.Equal("EUR", result.Currency);
        Assert.Equal(12.76m, result.Total);
        Assert.Equal(12.76m, result.Subtotal);
        Assert.True(result.Confidence >= 0.8m);

        var names = result.Items.Select(i => i.Name).ToList();
        Assert.Equal(4, names.Count);
        Assert.Contains("Bio Milch", names);
        Assert.Contains("Butter", names);
        Assert.Contains("Kaffee", names);
        Assert.DoesNotContain(names, n => n.Contains("SUMME", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, n => n.Contains("MwSt", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, n => n.Contains("Datum", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, n => n.Contains("Gegeben", StringComparison.OrdinalIgnoreCase));

        var kaffee = result.Items.Single(i => i.Name == "Kaffee");
        Assert.Equal(6.99m, kaffee.TotalPrice);
    }

    [Fact]
    public void Parse_SeparatesDiscountDepositShippingAndRoundingFromProducts()
    {
        const string receipt = """
            Test Markt
            Ware 10,00
            Zwischensumme 10,00
            Aktionsgutschein -2,00
            Pfand 0,25
            Versand 1,00
            Rundung -0,01
            SUMME EUR 9,24
            """;

        var result = ReceiptTextParser.Parse(receipt);

        var item = Assert.Single(result.Items);
        Assert.Equal("Ware", item.Name);
        Assert.Equal(10m, item.TotalPrice);
        Assert.Equal(10m, result.Subtotal);
        Assert.Equal(2m, result.Discounts);
        Assert.Equal(.25m, result.Deposits);
        Assert.Equal(1m, result.Shipping);
        Assert.Equal(-.01m, result.Rounding);
        Assert.Equal(9.24m, result.Total);
        var discount = Assert.Single(result.StructuredDiscounts!);
        Assert.Equal("coupon", discount.Type);
        Assert.Equal(2m, discount.Amount);
        Assert.Contains("Aktionsgutschein", discount.Label, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_GiftCardBalanceIsPaymentNotDiscount()
    {
        const string receipt = """
            Amazon
            Ware 10,00
            Geschenkgutschein-Guthaben 5,00
            SUMME EUR 10,00
            """;

        var result = ReceiptTextParser.Parse(receipt);

        Assert.Empty(result.StructuredDiscounts!);
        Assert.Null(result.Discounts);
        Assert.Single(result.Items);
    }

    [Fact]
    public void Parse_PurchasedGiftCardRemainsARealArticle()
    {
        const string receipt = """
            Drogerie
            Geschenkgutschein 25,00
            SUMME EUR 25,00
            """;

        var result = ReceiptTextParser.Parse(receipt);

        var item = Assert.Single(result.Items);
        Assert.Equal("Geschenkgutschein", item.Name);
        Assert.Equal(25m, item.TotalPrice);
        Assert.Empty(result.StructuredDiscounts!);
        Assert.Null(result.Discounts);
    }

    [Fact]
    public void Parse_RecognizesWeightedOrQuantityPricePattern()
    {
        const string receipt = """
            Markt
            Bananen 0,750 kg x 2,99 2,24
            SUMME EUR 2,24
            """;

        var item = Assert.Single(ReceiptTextParser.Parse(receipt).Items);
        Assert.Equal("Bananen", item.Name);
        Assert.Equal(.750m, item.Quantity);
        Assert.Equal("kg", item.QuantityUnit);
        Assert.Equal(2.99m, item.UnitPrice);
        Assert.Equal(2.24m, item.TotalPrice);
    }

    [Fact]
    public void Parse_DoesNotDropProductNamesContainingBar()
    {
        const string receipt = """
            Markt
            Barilla Spaghetti 1,99
            SUMME EUR 1,99
            """;

        var item = Assert.Single(ReceiptTextParser.Parse(receipt).Items);
        Assert.Equal("Barilla Spaghetti", item.Name);
    }

    [Fact]
    public void Parse_EmptyText_YieldsLowConfidenceEmptyResult()
    {
        var result = ReceiptTextParser.Parse("");
        Assert.Null(result.Merchant);
        Assert.Empty(result.Items);
        Assert.Null(result.Total);
        Assert.True(result.Confidence <= 0.4m);
    }

    [Fact]
    public void Parse_UsesCurrencyHintWhenNoSymbolPresent()
    {
        var result = ReceiptTextParser.Parse("Shop\nWidget 9,99\nGesamt 9,99", currencyHint: "usd");
        Assert.Equal("USD", result.Currency);
        Assert.Equal(9.99m, result.Total);
    }
}