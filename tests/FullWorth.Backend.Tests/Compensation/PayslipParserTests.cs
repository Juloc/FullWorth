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

    // Das Layout, das viele Lohnprogramme drucken: je Zeile "Monat  Jahr" (Jahressumme seit Januar), die
    // Abzuege als Kuerzel, darunter eine Tabelle der Beitragsgrundlagen. Alle Zahlen sind erfunden.
    [Fact]
    public void Parse_ReadsTheMonthColumnNotTheYearToDateColumn()
    {
        const string text = """
            Muster GmbH
            Lohn- und Gehaltsabrechnung 02.2026
            Eintrittsdatum 01.09.2020
            1000 Gehalt  3.000,00  * * *
            5100 Pkw private Nutzung  250,00  * * *
            7130 AG-Direktversich. lfd.  80,00
            GESAMTBRUTTO: 3.330,00  10.000,00
            Lohnsteuer 100,00  800,00
            KV-Beitrag AN² 270,00  540,00
            RV-Beitrag AN 300,00  600,00
            AV-Beitrag AN 40,00  80,00
            PV-Beitrag AN¹ 70,00  140,00
            Summe gesetzlicher Abzüge -780,00
            NETTOVERDIENST: 2.550,00  7.500,00
            7000 Entgeltumw.Altersv.lfd. -200,00
            Summe sonst. Abzüge/Zuzahlungen -500,00
            ZAHLBETRAG: 2.050,00
            Pauschale Lohnsteuer 6,00
            Pauschaler Solidaritätszuschlag 0,30
            WERTE:  Monat  Jahr  SV-Brutto  Monat  Jahr  AG-Ant. (M)²  AG-Ant. (J)²
            Steuerbrutto: 3.100,00  10.000,00  Krankenversicherung: 3.100,00  6.200,00  270,00  540,00
            Rentenversicherung: 3.100,00  6.200,00  300,00  600,00
            Pflegeversicherung: 3.100,00  6.200,00  60,00  120,00
            """;

        var result = PayslipTextParser.Parse(text);

        Assert.Equal(new DateOnly(2026, 2, 28), result.Period);
        Assert.Equal(3_330m, result.GrossPay);
        Assert.Equal(2_550m, result.NetPay);
        Assert.Equal(2_050m, result.Payout);
        Assert.Equal(100m, result.WageTax);
        Assert.Equal(300m, result.PensionInsurance);
        Assert.Equal(40m, result.UnemploymentInsurance);
        Assert.Equal(270m, result.HealthInsurance);
        Assert.Equal(70m, result.CareInsurance);
        Assert.Equal(250m, result.CompanyCarTaxableBenefit);
        Assert.Equal(200m, result.BavEmployee);
        Assert.Equal(80m, result.BavEmployer);
        // "Pauschaler Solidaritaetszuschlag" zahlt der Arbeitgeber; ein Soli des Arbeitnehmers steht hier nicht.
        Assert.Null(result.SolidaritySurcharge);
        Assert.Null(result.Bonus);
    }
}
