using FullWorth.Backend.Modules.Compensation;

namespace FullWorth.Backend.Tests.Compensation;

public sealed class PayslipParserTests
{
    [Fact]
    public void Parse_ExtractsCommonGermanPayslipFields()
    {
        const string text = """
            Abrechnungsmonat 08/2026
            Gesamtbrutto 5.000,00 EUR
            Nettoverdienst 3.112,34 EUR
            Auszahlungsbetrag 3.012,34 EUR
            Lohnsteuer 735,10 EUR
            Solidaritätszuschlag 0,00 EUR
            Kirchensteuer 0,00 EUR
            RV AN 465,00 EUR
            AV AN 65,00 EUR
            KV AN 420,00 EUR
            PV AN 102,56 EUR
            Firmenwagen 250,00 EUR
            Entgeltumwandlung 100,00 EUR
            AG-Zuschuss bAV 15,00 EUR
            Bonus 500,00 EUR
            """;

        var result = PayslipTextParser.Parse(text);

        Assert.Equal(new DateOnly(2026, 8, 31), result.Period);
        Assert.Equal(5_000m, result.GrossPay);
        Assert.Equal(3_112.34m, result.NetPay);
        Assert.Equal(3_012.34m, result.Payout);
        Assert.Equal(735.10m, result.WageTax);
        Assert.Equal(465m, result.PensionInsurance);
        Assert.Equal(65m, result.UnemploymentInsurance);
        Assert.Equal(420m, result.HealthInsurance);
        Assert.Equal(102.56m, result.CareInsurance);
        Assert.Equal(250m, result.CompanyCarTaxableBenefit);
        Assert.Equal(100m, result.BavEmployee);
        Assert.Equal(15m, result.BavEmployer);
        Assert.Equal(500m, result.Bonus);
        Assert.True(result.ConfidencePercent >= 70m);
    }

    [Fact]
    public void Parse_LowInformationTextReturnsWarnings()
    {
        var result = PayslipTextParser.Parse("Abrechnung September 2026");

        Assert.Equal(new DateOnly(2026, 9, 30), result.Period);
        Assert.Null(result.GrossPay);
        Assert.Null(result.NetPay);
        Assert.True(result.ConfidencePercent < 70m);
        Assert.NotEmpty(result.Warnings);
    }
}
