using FullWorth.Backend.Modules.Parity;

namespace FullWorth.Backend.Tests.Parity;

public sealed class BrokerPdfTradeParserTests
{
    [Fact]
    public void ParsesTradeRepublicBuyConfirmation()
    {
        const string text = """
TRADE REPUBLIC BANK GMBH
Wertpapierabrechnung Kauf
Ausführungstag 15.08.2026
Wertpapier Vanguard FTSE All-World UCITS ETF
ISIN IE00BK5BQT80
Stück 1,250000
Ausführungskurs 120,40 EUR
Kurswert 150,50 EUR
Fremde Spesen 1,00 EUR
Ausmachender Betrag 151,50 EUR
""";

        var result = BrokerPdfTradeParser.Parse(text, "pdf:tr");

        Assert.Equal("trade-republic", result.Broker);
        Assert.NotNull(result.Trade);
        Assert.Equal("buy", result.Trade!.TradeType);
        Assert.Equal("2026-08-15", result.Trade.TradeDate);
        Assert.Equal("IE00BK5BQT80", result.Trade.Isin);
        Assert.Equal("1.25", result.Trade.Quantity);
        Assert.Equal("120.4", result.Trade.Price);
        Assert.Equal("151.5", result.Trade.Amount);
        Assert.Equal("1", result.Trade.Fees);
        Assert.Equal("EUR", result.Trade.Currency);
    }

    [Fact]
    public void ParsesIngSellAndTaxes()
    {
        const string text = """
ING-DiBa AG
Wertpapierabrechnung Verkauf
Schlusstag 20.08.2026
Bezeichnung iShares Core MSCI World UCITS ETF
ISIN IE00B4L5Y983 WKN A0RPWH
Nominale 2,000 STK
Kurs 104,50 EUR
Kurswert 209,00 EUR
Kapitalertragsteuer 4,20 EUR
Solidaritätszuschlag 0,23 EUR
Endbetrag 204,57 EUR
""";

        var result = BrokerPdfTradeParser.Parse(text, "pdf:ing");

        Assert.Equal("ing", result.Broker);
        Assert.NotNull(result.Trade);
        Assert.Equal("sell", result.Trade!.TradeType);
        Assert.Equal("IE00B4L5Y983", result.Trade.Isin);
        Assert.Equal("A0RPWH", result.Trade.Wkn);
        Assert.Equal("2", result.Trade.Quantity);
        Assert.Equal("4.43", result.Trade.Taxes);
        Assert.Equal("204.57", result.Trade.Amount);
    }

    [Fact]
    public void ParsesDividendWithWithholdingTax()
    {
        const string text = """
comdirect bank AG
Dividendengutschrift
Datum 25.08.2026
Bezeichnung Microsoft Corp.
ISIN US5949181045 WKN 870747
Bruttobetrag 25,00 USD
Quellensteuer 3,75 USD
Gutschrift 21,25 USD
""";

        var result = BrokerPdfTradeParser.Parse(text, "pdf:dividend");

        Assert.Equal("comdirect", result.Broker);
        Assert.NotNull(result.Trade);
        Assert.Equal("dividend", result.Trade!.TradeType);
        Assert.Equal("US5949181045", result.Trade.Isin);
        Assert.Equal("25", result.Trade.GrossAmount);
        Assert.Equal("3.75", result.Trade.WithholdingTax);
        Assert.Equal("21.25", result.Trade.Amount);
        Assert.Equal("USD", result.Trade.Currency);
    }

    [Fact]
    public void ParsesDkbSavingsPlanAsBuy()
    {
        const string text = """
Deutsche Kreditbank AG DKB
Sparplanausführung
Ausführungsdatum 01.09.2026
Wertpapierbezeichnung Vanguard FTSE All-World UCITS ETF
ISIN IE00BK5BQT80
Stückzahl 0,5123
Ausführungskurs 117,12 EUR
Gesamtbetrag 60,00 EUR
""";

        var result = BrokerPdfTradeParser.Parse(text, "pdf:dkb");

        Assert.Equal("dkb", result.Broker);
        Assert.NotNull(result.Trade);
        Assert.Equal("buy", result.Trade!.TradeType);
        Assert.Equal("2026-09-01", result.Trade.TradeDate);
        Assert.Equal("0.5123", result.Trade.Quantity);
        Assert.Equal("60", result.Trade.Amount);
    }

    [Fact]
    public void RejectsDocumentWithoutRecognizableTransactionType()
    {
        var result = BrokerPdfTradeParser.Parse("Depotübersicht zum 31.08.2026\nGesamtwert 10.000,00 EUR");

        Assert.Null(result.Trade);
        Assert.Contains(result.Warnings, warning => warning.Contains("type", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RejectsTradeWithoutAnyUsableAmount()
    {
        const string text = """
flatexDEGIRO Bank AG
Wertpapierabrechnung Kauf
Handelstag 31.08.2026
Wertpapier Test ETF
ISIN DE000A0F5UH1
Stück 1,0
""";

        var result = BrokerPdfTradeParser.Parse(text);

        Assert.Null(result.Trade);
        Assert.Contains(result.Warnings, warning => warning.Contains("amount", StringComparison.OrdinalIgnoreCase));
    }
}
