using FullWorth.Backend.Modules.Compensation;

namespace FullWorth.Backend.Tests.Compensation;

/// <summary>
/// Der Dienstrad-/JobRad-Rechner (#179) - erfundene Zahlen, echte Formeln: die 0,25-%-Regel exakt wie sie
/// JobRad selbst rechnet (UVP geviertelt, auf 100 € abgerundet, 1 % davon), Entgeltpunkte nach §70 SGB VI
/// gegen die 2026er Tabellenwerte (Durchschnittsentgelt 51.944 €, aktueller Rentenwert 42,52 €), und die
/// Grenzfaelle, die das Issue selbst als Akzeptanzkriterien nennt.
/// </summary>
public sealed class JobRadCalculatorTests
{
    private static CompensationProfileInput Salary(decimal annualGross, int children = 0) => new(
        Name: "Test",
        AnnualGross: annualGross,
        StateCode: "BW",
        TaxClass: 1,
        ChildrenUnder25: children,
        TaxYear: 2026);

    private static JobRadLeasingInput Leasing(
        decimal listPrice = 3_000m, decimal rate = 100m, int term = 36,
        bool salaryConversion = true, decimal subsidy = 0m, decimal takeoverPercent = 0m) => new(
        ListPriceGross: listPrice,
        MonthlyLeasingRateGross: rate,
        TermMonths: term,
        SalaryConversion: salaryConversion,
        EmployerSubsidyMonthly: subsidy,
        TakeoverPricePercentOfList: takeoverPercent);

    private static JobRadCashPurchaseInput Cash(decimal price) => new(price);

    [Fact]
    public void MonthlyTaxableBenefit_MatchesTheOfficialTwoStepRoundingExample()
    {
        // JobRads eigenes Beispiel: UVP 2.500 € -> geviertelt 625 € -> abgerundet auf 600 € -> 1 % davon = 6 €.
        // 0,25 % von 2.500 € direkt gerechnet wäre 6,25 € - die zweistufige Rundung trifft das nicht.
        var leasing = new JobRadLeasingInput(ListPriceGross: 2_500m, MonthlyLeasingRateGross: 80m);

        Assert.Equal(6.00m, leasing.MonthlyTaxableBenefit);
    }

    [Fact]
    public void MonthlyTaxableBenefit_IsZeroUnderGehaltsextra()
    {
        // § 3 Nr. 37 EStG: on top of the salary already owed, private use is fully tax- and SV-free.
        var leasing = Leasing(salaryConversion: false);

        Assert.Equal(0m, leasing.MonthlyTaxableBenefit);
    }

    [Fact]
    public void SalaryConversion_CostsRealNetIncome_ButGehaltsextraDoesNot()
    {
        var conversion = JobRadCalculator.Compare(new(Salary(60_000m), Leasing(), Cash(2_400m)));
        var extra = JobRadCalculator.Compare(new(Salary(60_000m), Leasing(salaryConversion: false), Cash(2_400m)));

        Assert.True(conversion.NetMonthlyImpact < 0m);
        Assert.Equal(0m, extra.NetMonthlyImpact);
        Assert.True(conversion.Employer.MonthlySavingsFromLowerContributions > 0m);
        Assert.Equal(0m, extra.Employer.MonthlySavingsFromLowerContributions);
    }

    [Fact]
    public void EmployerSubsidy_ReducesTheNetCostWithoutDoubleCounting()
    {
        var withoutSubsidy = JobRadCalculator.Compare(new(Salary(60_000m), Leasing(rate: 100m), Cash(2_400m)));
        var withSubsidy = JobRadCalculator.Compare(new(Salary(60_000m), Leasing(rate: 100m, subsidy: 40m), Cash(2_400m)));

        // Only the employee's own 60 € still leaves the gross salary - less than the full 100 €, so the net
        // hit shrinks, but it does not vanish (the subsidy is not the employee's own money either way).
        Assert.True(withSubsidy.NetMonthlyImpact > withoutSubsidy.NetMonthlyImpact);
        Assert.True(withSubsidy.NetMonthlyImpact < 0m);
    }

    [Fact]
    public void PensionImpact_IsZeroWhenAlreadyAboveTheContributionCeiling()
    {
        // 2026 RV/AV-Beitragsbemessungsgrenze West: 101.400 €. 150.000 € liegt weit darüber, und eine
        // Umwandlung von 1.200 €/Jahr bleibt es auch danach - der Kappung, nicht der Umwandlung, unterliegt
        // das Entgelt, also entsteht keine Rentenwirkung.
        var result = JobRadCalculator.Compare(new(Salary(150_000m), Leasing(rate: 100m), Cash(2_400m)));

        Assert.True(result.Pension.AboveContributionCeiling);
        Assert.Equal(0m, result.Pension.LostEntgeltpunkteOverTerm);
        Assert.Equal(0m, result.Pension.MonthlyPensionReductionAtCurrentValue);
    }

    [Fact]
    public void PensionImpact_ComputesRealEntgeltpunkteBelowTheCeiling()
    {
        var result = JobRadCalculator.Compare(new(Salary(60_000m), Leasing(rate: 100m, term: 36), Cash(2_400m)));

        Assert.False(result.Pension.AboveContributionCeiling);
        Assert.Equal(51_944m, result.Pension.AverageEarningsAnnual);
        Assert.Equal(42.52m, result.Pension.CurrentPensionValueMonthly);
        // Von Hand: SV-Bemessung sinkt um rate (100€) minus geldwerter Vorteil (7€/Monat bei 3.000€ UVP) =
        // 93 €/Monat = 1.116 €/Jahr, geteilt durch 51.944 € Durchschnittsentgelt ≈ 0,02149 EP/Jahr, über
        // 3 Jahre Laufzeit ≈ 0,0645 EP, mal 42,52 € aktueller Rentenwert ≈ 2,74 €/Monat weniger Rente.
        Assert.InRange(result.Pension.LostEntgeltpunkteOverTerm, 0.060m, 0.069m);
        Assert.InRange(result.Pension.MonthlyPensionReductionAtCurrentValue, 2.50m, 3.00m);
    }

    [Fact]
    public void PensionImpact_IsZeroUnderGehaltsextra()
    {
        var result = JobRadCalculator.Compare(new(Salary(60_000m), Leasing(salaryConversion: false), Cash(2_400m)));

        Assert.Equal(0m, result.Pension.LostEntgeltpunkteOverTerm);
    }

    [Fact]
    public void TakeoverPrice_IsPartOfTheTotalLeaseCostButNotOfCashPurchase()
    {
        var noTakeover = JobRadCalculator.Compare(new(Salary(60_000m), Leasing(rate: 50m, takeoverPercent: 0m), Cash(2_400m)));
        var withTakeover = JobRadCalculator.Compare(new(Salary(60_000m), Leasing(rate: 50m, takeoverPercent: 20m), Cash(2_400m)));

        // 20 % von 3.000 € UVP = 600 €.
        Assert.Equal(600m, withTakeover.TotalLeaseCostUntilOwnership - noTakeover.TotalLeaseCostUntilOwnership);
        Assert.Equal(noTakeover.ComparableCashPurchaseCost, withTakeover.ComparableCashPurchaseCost);
    }

    [Fact]
    public void CashPurchase_ComparesAgainstTheActualPriceNotTheListPrice()
    {
        // Der Vergleich darf nicht gegen die UVP laufen - ein realistischer Haendlerrabatt macht den Barkauf
        // guenstiger, als die Listenpreise es vermuten liessen.
        var result = JobRadCalculator.Compare(new(
            Salary(60_000m),
            Leasing(listPrice: 3_000m, rate: 90m, term: 36),
            Cash(2_100m)));

        Assert.Equal(2_100m, result.ComparableCashPurchaseCost);
        Assert.Equal(result.TotalLeaseCostUntilOwnership - result.ComparableCashPurchaseCost, -result.DifferenceLeasingVsCash);
    }

    [Fact]
    public void TaxClasses_ChangeTheNetImpactByTheirMarginalRate()
    {
        // Eine Gehaltsumwandlung kostet netto WENIGER, je hoeher der Grenzsteuersatz an genau dieser Stelle
        // des Einkommens ist - der Fiskus traegt dort einen groesseren Teil der Einbusse mit. Steuerklasse 5
        // hat praktisch keinen eigenen Grundfreibetrag und dadurch den schaerfsten Grenzsteuersatz der drei;
        // Steuerklasse 3 hat den mildesten. Deshalb kostet dieselbe Umwandlung in Klasse 5 am wenigsten Netto
        // und in Klasse 3 am meisten - das Gegenteil von "wer schon mehr Netto hat, spuert es weniger" waere
        // hier die falsche Intuition.
        var class3 = JobRadCalculator.Compare(new(Salary(60_000m) with { TaxClass = 3 }, Leasing(rate: 100m), Cash(2_400m)));
        var class5 = JobRadCalculator.Compare(new(Salary(60_000m) with { TaxClass = 5 }, Leasing(rate: 100m), Cash(2_400m)));

        Assert.True(class5.NetMonthlyImpact > class3.NetMonthlyImpact);
    }

    [Fact]
    public void SocialBenefitEstimates_AreAllMarkedAsEstimatesWithTheirAssumptionShown()
    {
        var withChild = JobRadCalculator.Compare(new(Salary(60_000m, children: 1), Leasing(rate: 100m), Cash(2_400m)));

        Assert.Equal(3, withChild.SocialBenefitEstimates.Count);
        Assert.All(withChild.SocialBenefitEstimates, estimate =>
        {
            Assert.True(estimate.IsEstimate);
            Assert.False(string.IsNullOrWhiteSpace(estimate.Basis));
            Assert.True(estimate.MonthlyDeltaEstimate < 0m);
        });
        Assert.Contains(withChild.SocialBenefitEstimates, e => e.Label == "Arbeitslosengeld I" && e.Basis.Contains("67 %"));
    }

    [Fact]
    public void SocialBenefitEstimates_AreEmptyUnderGehaltsextra()
    {
        var result = JobRadCalculator.Compare(new(Salary(60_000m), Leasing(salaryConversion: false), Cash(2_400m)));

        Assert.Empty(result.SocialBenefitEstimates);
    }
}
