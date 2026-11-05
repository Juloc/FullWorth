using FullWorth.Backend.Modules.Purchases.Extraction;

namespace FullWorth.Backend.Tests.Purchases.Extraction;

/// <summary>
/// The local reader must be right or say so. A receipt is regular, so its own arithmetic is the check:
/// items - discounts + deposits = total. What adds up is trustworthy (confidence 0.9); what does not,
/// or needed a corrected price, is left low so nothing confirms it by itself.
/// </summary>
public sealed class ReceiptReadingTests
{
    private const decimal AutoConfirmThreshold = 0.85m;

    [Fact]
    public void A_discount_and_a_returned_deposit_under_their_items_add_up_to_the_total()
    {
        var result = ReceiptTextParser.Parse("""
            Penny
            Musterstr. 1
            EUR
            RINDERSALAMI 2,19 B
            App-Coupon -0,33 B
            BANANE GELB 0,72 B
            0,716 kg x 1,008 EUR/kg
            H-MILCH 1,5% LAK 2,10 B
            2 Stk x 1,05
            LEERGUT EINWEG -0,25 A *
            SUMME EUR 4,43
            Geg. VISA EUR 4,43
            Steuer % Netto Steuer Brutto
            B= 7,0% 3,88 0,27 4,15
            Datum: 13.05.2026
            """);

        Assert.Equal("Penny", result.Merchant);
        Assert.Equal(new DateOnly(2026, 5, 13), result.PurchaseDate);
        Assert.Equal(4.43m, result.Total);
        Assert.Equal(["RINDERSALAMI", "BANANE GELB", "H-MILCH 1,5% LAK"], result.Items.Select(i => i.Name));
        var banana = result.Items[1];
        Assert.Equal(0.716m, banana.Quantity);
        Assert.Equal(1.008m, banana.UnitPrice);
        Assert.Equal("kg", banana.QuantityUnit);
        Assert.Equal(2m, result.Items[2].Quantity);
        Assert.Equal([0.33m, 0.25m], result.StructuredDiscounts!.Select(d => d.Amount));
        Assert.Equal(0, result.StructuredDiscounts![0].ItemIndex);
        Assert.Equal(ReceiptTextParser.ConsistentConfidence, result.Confidence);
    }

    /// <summary>Kaufland prints the name above "4 * 1,69  6,76 B", the saving as an extra line, and a "Sie sparen" note that is no money.</summary>
    [Fact]
    public void A_name_on_its_own_line_takes_the_quantity_line_below_it()
    {
        var result = ReceiptTextParser.Parse("""
            Kaufland
            Preis EUR
            Nacken/Kamm 1,089 kg 7,07 B
            Sie sparen 19% oder 1,63 EUR!
            K.Frischkäse Kräut
            4 * 1,69 6,76 B
            K.Speisequark 3 * 1,29 3,87 B
            K Card Rabatt -0,59
            SUMME 17,11
            Kartenzahlung 17,11
            Datum: 03.07.2025
            """);

        Assert.Equal(3, result.Items.Count);
        var cheese = result.Items[1];
        Assert.Equal("K.Frischkäse Kräut", cheese.Name);
        Assert.Equal(4m, cheese.Quantity);
        Assert.Equal(1.69m, cheese.UnitPrice);
        Assert.Equal(6.76m, cheese.TotalPrice);
        Assert.Equal("K.Speisequark", result.Items[2].Name);
        Assert.Equal(.59m, Assert.Single(result.StructuredDiscounts!).Amount);
        Assert.Equal(ReceiptTextParser.ConsistentConfidence, result.Confidence);
    }

    /// <summary>OCR breaks and spaces amounts: "13," over "29", "16, 21", "2 ,49".</summary>
    [Fact]
    public void An_amount_the_scan_broke_is_read_as_one()
    {
        var result = ReceiptTextParser.Parse("Markt\nDatum: 01.02.2026\nBrot 2 ,49 B\nKäse 11, 80 B\nSUMME EUR 14,\n29");

        Assert.Equal(14.29m, result.Total);
        Assert.Equal([2.49m, 11.80m], result.Items.Select(i => i.TotalPrice));
        Assert.Equal(ReceiptTextParser.ConsistentConfidence, result.Confidence);
    }

    /// <summary>"0,70 B" reads as "0,708": the tax class glued to the cents is not part of the amount.</summary>
    [Fact]
    public void A_tax_class_glued_to_the_cents_is_not_part_of_the_amount()
    {
        var result = ReceiptTextParser.Parse("Penny\nDatum: 01.02.2026\nBrot 2,19 B\nApp-Preis-Rabatt -0,708\nSUMME EUR 1,49");

        Assert.Equal(.70m, Assert.Single(result.StructuredDiscounts!).Amount);
        Assert.Equal(ReceiptTextParser.ConsistentConfidence, result.Confidence);
    }

    [Fact]
    public void An_unreadable_SUMME_word_is_replaced_by_the_paid_amount()
    {
        var result = ReceiptTextParser.Parse("Kaufland\nDatum: 03.07.2025\nBrot 2,00 B\nMilch 1,50 B\nJUNE 3,50\nKartenzahlung 3,50");

        Assert.Equal(3.5m, result.Total);
        Assert.Equal(2, result.Items.Count);
        Assert.Equal(ReceiptTextParser.ConsistentConfidence, result.Confidence);
    }

    /// <summary>
    /// Exactly one price that, changed in one digit to a look-alike, makes the receipt add up is a misread
    /// (a 5 read as a 9). The price is corrected - and the result still does not reach auto-confirm.
    /// </summary>
    [Fact]
    public void One_misread_digit_is_corrected_and_the_result_stays_below_auto_confirm()
    {
        var result = ReceiptTextParser.Parse("""
            Penny
            Datum: 24.10.2025
            Best M. Ente 9,99 B
            Gustoria Bami 3,49 B
            PIZZA DIAVOLO 6,98 B
            2 Stk x 3,49
            LEERG. EW E. ST -0,25B*
            SUMME EUR 16, 21
            """);

        Assert.Equal(5.99m, result.Items[0].TotalPrice);
        Assert.Equal(ReceiptTextParser.RepairedConfidence, result.Confidence);
        Assert.True(result.Confidence < AutoConfirmThreshold);
    }

    /// <summary>When no single look-alike price fixes the sum, nothing is touched: the receipt is reported as unsure.</summary>
    [Fact]
    public void A_receipt_that_does_not_add_up_stays_unsure_and_unchanged()
    {
        var result = ReceiptTextParser.Parse("Markt\nDatum: 01.02.2026\nBrot 1,00 B\nMilch 2,00 B\nSUMME EUR 5,00");

        Assert.Equal([1.00m, 2.00m], result.Items.Select(i => i.TotalPrice));
        Assert.Equal(ReceiptTextParser.InconsistentConfidence, result.Confidence);
    }

    /// <summary>A scan that reads the year as 3025 must not date the purchase in the future; the signature line has the real date.</summary>
    [Fact]
    public void An_impossible_date_is_skipped()
    {
        var result = ReceiptTextParser.Parse("Penny\n17.10.3025 18:41\nBrot 1,00 B\nSUMME EUR 1,00\nTSE-Start: 2025-10-17T18:41:22.000");

        Assert.Equal(new DateOnly(2025, 10, 17), result.PurchaseDate);
    }

    /// <summary>The same page is read twice; the reading whose sums agree wins, and a reading most others agree with beats a lone one.</summary>
    [Fact]
    public void The_reading_that_adds_up_is_kept()
    {
        var wrong = ReceiptTextParser.Parse("Penny\nDatum: 01.02.2026\nBrot 1,00 B\nMilch 2,00 B\nSUMME EUR 5,00");
        var right = ReceiptTextParser.Parse("Penny\nDatum: 01.02.2026\nBrot 1,00 B\nMilch 2,00 B\nSUMME EUR 3,00");

        Assert.Same(right, ReceiptReadingSelector.Choose([wrong, right]));
        Assert.Same(right, ReceiptReadingSelector.Choose([right, wrong]));
    }

    [Fact]
    public void Of_two_readings_that_both_add_up_the_one_more_readings_agree_with_wins()
    {
        var a = ReceiptTextParser.Parse("Penny\nDatum: 01.02.2026\nBrot 1,00 B\nSUMME EUR 1,00");
        var b = ReceiptTextParser.Parse("Penny\nDatum: 01.02.2026\nBrot 2,00 B\nSUMME EUR 2,00");
        var b2 = ReceiptTextParser.Parse("Penny\nDatum: 01.02.2026\nBrot 2,00 B\nSUMME EUR 2,00");

        Assert.Equal(2.00m, ReceiptReadingSelector.Choose([a, b, b2]).Total);
    }
}
