using FullWorth.Banking.Services;

namespace FullWorth.Banking.Tests;

/// <summary>
/// Die Zeile unter einer Buchung.
///
/// Sie hatte keinen Test, und genau deshalb stand in der Liste monatelang der zusammengeklebte
/// Rohtext der Bank: die Technik-Erkennung verlangte <c>MANDATE</c> unmittelbar gefolgt von
/// <c>:</c>, sodass <c>mandatereference:</c> durchrutschte - und getrennt wurde ohnehin nur an
/// <c>|</c> und Zeilenumbruch, nie an <c>;</c>, womit alles ein einziges Stueck blieb.
///
/// Der erste Test ist die gemeldete Zeile. Die anderen halten die Grenze fest: was technisch ist,
/// faellt weg; was nur beschriftet ist, verliert die Beschriftung und behaelt seinen Inhalt.
/// </summary>
public sealed class TransactionPurposeTests
{
    [Fact]
    public void The_reported_card_payment_keeps_only_what_a_person_can_read()
    {
        var raw = "mandatereference:M12345678; creditorid:DE98ZZZ09999999999; "
            + "remittanceinformation:Kaufumsatz; Datum 13.09.2026 Zeit 14:22 Kaufumsatz VISA 1234";

        var purpose = TransactionPurpose.Normalize(raw, "VISA");

        Assert.NotNull(purpose);
        Assert.DoesNotContain("mandatereference", purpose, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("creditorid", purpose, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("remittanceinformation", purpose, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Kaufumsatz", purpose, StringComparison.Ordinal);
    }

    [Fact]
    public void A_labelled_purpose_loses_the_label_and_keeps_its_content()
    {
        // Wer das ganze Stueck wegwirft, wirft genau die Information weg, die der Benutzer wollte.
        Assert.Equal("Rechnung 2026-0815", TransactionPurpose.Normalize("remittanceinformation:Rechnung 2026-0815", null));
        Assert.Equal("Miete September", TransactionPurpose.Normalize("Verwendungszweck: Miete September", null));
    }

    [Theory]
    [InlineData("mandatereference:M1")]
    [InlineData("MANDATEID:M1")]
    [InlineData("creditorid:DE98ZZZ0")]
    [InlineData("debtorid:XYZ")]
    [InlineData("endtoendid:NOTPROVIDED")]
    [InlineData("EREF+ABC123")]
    [InlineData("TXID=99")]
    public void A_purely_technical_reference_disappears(string raw) =>
        Assert.Null(TransactionPurpose.Normalize(raw, null));

    [Fact]
    public void The_sepa_purpose_still_wins_when_the_bank_sends_one()
    {
        var raw = "EREF+2026091300 SVWZ+Monatsbeitrag Fitnessstudio MREF+M4711";

        Assert.Equal("Monatsbeitrag Fitnessstudio", TransactionPurpose.Normalize(raw, null));
    }

    [Fact]
    public void The_counterparty_is_not_repeated_underneath_itself()
    {
        // Der Haendlername steht schon in der Zeile darueber.
        Assert.Null(TransactionPurpose.Normalize("REWE Markt GmbH", "REWE Markt GmbH"));
    }

    [Fact]
    public void Nothing_readable_means_nothing_is_shown()
    {
        Assert.Null(TransactionPurpose.Normalize("mandatereference:M1; creditorid:DE98", null));
        Assert.Null(TransactionPurpose.Normalize("   ", null));
        Assert.Null(TransactionPurpose.Normalize(null, null));
    }

    [Fact]
    public void A_semicolon_inside_a_real_purpose_does_not_lose_it()
    {
        // Trennen an ';' darf keinen Inhalt kosten - beide Haelften sind lesbar und bleiben.
        var purpose = TransactionPurpose.Normalize("Rechnung 123; vielen Dank", null);

        Assert.NotNull(purpose);
        Assert.Contains("Rechnung 123", purpose, StringComparison.Ordinal);
        Assert.Contains("vielen Dank", purpose, StringComparison.Ordinal);
    }
}
