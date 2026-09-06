using FullWorth.Backend.Modules.Compensation;

namespace FullWorth.Backend.Tests.Compensation;

public sealed class CompensationCalculatorTests
{
    [Fact]
    public void IncomeTax2026_UsesTaxFreeAllowance()
    {
        Assert.Equal(0m, GermanCompensationCalculator.IncomeTax2026(12_348m));
        Assert.True(GermanCompensationCalculator.IncomeTax2026(12_349m) > 0m);
    }

    [Fact]
    public void IncomeTax2026_Uses42PercentZoneFormula()
    {
        var tax = GermanCompensationCalculator.IncomeTax2026(100_000m);
        Assert.Equal(30_864.37m, tax);
    }

    [Fact]
    public void WageTax2026_Class1MatchesBmfReferenceWithinOneCentPerMonth()
    {
        var profile = BasicProfile(60_000m) with
        {
            TaxClass = 1,
            ChildrenUnder25 = 0,
            ChildlessCareSurcharge = true,
            HealthInsuranceAdditionalRatePercent = 2.5m
        };

        var tax = GermanCompensationCalculator.WageTax2026(60_000m, profile);

        // BMF PAP 2026 reference: RE4=500000 cents/month, STKL=1, KVZ=2.50, PVZ=1
        // => Lohnsteuer 785.83 EUR/month. The annualized planning path rounds to 785.84 EUR/month.
        Assert.InRange(tax.EstimatedIncomeTaxAnnual / 12m, 785.83m, 785.84m);
    }

    [Fact]
    public void TaxClasses_ChangeEstimatedNetInExpectedDirection()
    {
        var class1 = GermanCompensationCalculator.Calculate(BasicProfile(60_000m) with { TaxClass = 1 });
        var class3 = GermanCompensationCalculator.Calculate(BasicProfile(60_000m) with { TaxClass = 3 });
        var class5 = GermanCompensationCalculator.Calculate(BasicProfile(60_000m) with { TaxClass = 5 });

        Assert.True(class3.EstimatedCashNetAnnual > class1.EstimatedCashNetAnnual);
        Assert.True(class1.EstimatedCashNetAnnual > class5.EstimatedCashNetAnnual);
    }

    [Fact]
    public void TaxClass4Factor_ReducesClass4WageTax()
    {
        var normal = GermanCompensationCalculator.WageTax2026(60_000m, BasicProfile(60_000m) with
        {
            TaxClass = 4,
            TaxClass4Factor = 1m
        });
        var factored = GermanCompensationCalculator.WageTax2026(60_000m, BasicProfile(60_000m) with
        {
            TaxClass = 4,
            TaxClass4Factor = 0.8m
        });

        Assert.Equal(Math.Round(normal.EstimatedIncomeTaxAnnual * 0.8m, 2, MidpointRounding.AwayFromZero), factored.EstimatedIncomeTaxAnnual);
        Assert.True(factored.EstimatedIncomeTaxAnnual < normal.EstimatedIncomeTaxAnnual);
    }

    [Fact]
    public void CompanyCar_AutomaticallyChoosesQuarterRuleForQualifyingElectricCar()
    {
        var input = new CompanyCarInput(
            Enabled: true,
            ListPrice: 80_000m,
            VehicleType: "electric",
            AcquisitionDate: new DateOnly(2026, 1, 1));

        Assert.Equal(0.25m, GermanCompensationCalculator.DetermineCompanyCarListPriceFactor(input));
    }

    [Fact]
    public void CompanyCar_2026HybridNeeds80KmOrLowCo2ForHalfRule()
    {
        var qualifying = new CompanyCarInput(
            Enabled: true,
            ListPrice: 50_000m,
            VehicleType: "hybrid",
            AcquisitionDate: new DateOnly(2026, 1, 1),
            ElectricRangeKm: 80m,
            Co2GramsPerKm: 60m);
        var notQualifying = qualifying with { ElectricRangeKm = 70m };

        Assert.Equal(0.5m, GermanCompensationCalculator.DetermineCompanyCarListPriceFactor(qualifying));
        Assert.Equal(1m, GermanCompensationCalculator.DetermineCompanyCarListPriceFactor(notQualifying));
    }

    [Fact]
    public void CompanyCar_DailyCommuteMethodUsesActualDays()
    {
        var monthly = GermanCompensationCalculator.CompanyCarTaxableBenefitAnnual(new CompanyCarInput(
            Enabled: true,
            ListPrice: 50_000m,
            TaxableListPriceFactor: 1m,
            VehicleType: "manual",
            OneWayCommuteKm: 30m,
            CommuteMethod: "monthly"));
        var daily = GermanCompensationCalculator.CompanyCarTaxableBenefitAnnual(new CompanyCarInput(
            Enabled: true,
            ListPrice: 50_000m,
            TaxableListPriceFactor: 1m,
            VehicleType: "manual",
            OneWayCommuteKm: 30m,
            CommuteMethod: "daily",
            CommuteDaysPerMonth: 5));

        Assert.True(daily < monthly);
    }

    [Fact]
    public void CompanyCar_UsesOnePercentAndCommuteMethod()
    {
        var annual = GermanCompensationCalculator.CompanyCarTaxableBenefitAnnual(new CompanyCarInput(
            Enabled: true,
            ListPrice: 50_000m,
            TaxableListPriceFactor: 1m,
            OneWayCommuteKm: 30m));

        Assert.Equal(11_400m, annual);
    }

    [Fact]
    public void CompanyCar_QuarterFactorReducesTaxableBenefit()
    {
        var annual = GermanCompensationCalculator.CompanyCarTaxableBenefitAnnual(new CompanyCarInput(
            Enabled: true,
            ListPrice: 50_000m,
            TaxableListPriceFactor: 0.25m,
            OneWayCommuteKm: 30m));

        Assert.Equal(2_850m, annual);
    }

    [Fact]
    public void FullWorth_CompanyCarDoesNotDoubleSubtractCashImpact()
    {
        var withoutCar = GermanCompensationCalculator.Calculate(BasicProfile(60_000m));
        var withCar = GermanCompensationCalculator.Calculate(BasicProfile(60_000m) with
        {
            CompanyCar = new CompanyCarInput(
                Enabled: true,
                ListPrice: 50_000m,
                TaxableListPriceFactor: 1m,
                OneWayCommuteKm: 20m,
                PrivateAlternativeCostMonthly: 600m)
        });

        var fullWorthDelta = withCar.FullWorthCompensationValueAnnual - withoutCar.FullWorthCompensationValueAnnual;
        Assert.Equal(withCar.CompanyCar.EstimatedEffectivePersonalValueAnnual, fullWorthDelta);
    }

    [Fact]
    public void BavLimits_AreDerivedFrom2026PensionCeiling()
    {
        Assert.Equal(8_112m, GermanCompensationCalculator.BavTaxFreeLimit2026);
        Assert.Equal(4_056m, GermanCompensationCalculator.BavSocialFreeLimit2026);
    }

    [Fact]
    public void FullWorth_BavIncludesEmployeeAndEmployerInvestedAmount()
    {
        var withoutBav = GermanCompensationCalculator.Calculate(BasicProfile(60_000m));
        var withBav = GermanCompensationCalculator.Calculate(BasicProfile(60_000m) with
        {
            OccupationalPension = new OccupationalPensionInput(
                EmployeeContributionMonthly: 100m,
                EmployerContributionMonthly: 50m)
        });

        var expectedDelta = withBav.OccupationalPension.TotalInvestedAnnual
            - withBav.OccupationalPension.EstimatedCurrentNetSacrificeAnnual;
        var actualDelta = withBav.FullWorthCompensationValueAnnual - withoutBav.FullWorthCompensationValueAnnual;
        Assert.Equal(expectedDelta, actualDelta);
    }

    [Fact]
    public void SocialInsurance_IsCappedAtContributionCeilings()
    {
        var lower = GermanCompensationCalculator.Calculate(BasicProfile(150_000m));
        var higher = GermanCompensationCalculator.Calculate(BasicProfile(250_000m));

        Assert.Equal(lower.SocialInsurance.TotalAnnual, higher.SocialInsurance.TotalAnnual);
        Assert.Equal(lower.SocialInsurance.EmployerTotalAnnual, higher.SocialInsurance.EmployerTotalAnnual);
    }

    [Fact]
    public void ChildlessCareSurcharge_IncreasesEmployeeContribution()
    {
        var childless = GermanCompensationCalculator.Calculate(BasicProfile(50_000m) with
        {
            ChildrenUnder25 = 0,
            ChildlessCareSurcharge = true
        });
        var parent = GermanCompensationCalculator.Calculate(BasicProfile(50_000m) with
        {
            ChildrenUnder25 = 1,
            ChildlessCareSurcharge = false
        });

        Assert.True(childless.SocialInsurance.CareAnnual > parent.SocialInsurance.CareAnnual);
    }

    [Fact]
    public void Inflation_Adjusts2023SalaryTo2026PurchasingPower()
    {
        var adjusted = InflationIndex.AdjustForPurchasingPower(
            50_000m,
            new DateOnly(2023, 12, 31),
            new DateOnly(2026, 7, 31));

        Assert.InRange(adjusted, 53_810m, 53_820m);
    }

    [Fact]
    public void Negotiation_SeparatesNominalAndRealRaise()
    {
        var result = InflationIndex.Analyze(new SalaryNegotiationRequest(
            50_000m,
            new DateOnly(2023, 12, 31),
            54_000m,
            57_000m,
            3m,
            new DateOnly(2026, 7, 31)));

        Assert.Equal(8m, result.CurrentNominalChangePercent);
        Assert.True(result.CurrentRealChangePercent < 1m);
        Assert.True(result.DesiredRealChangePercent > result.CurrentRealChangePercent);
        Assert.True(result.SuggestedReferenceSalary > result.PurchasingPowerMaintenanceSalary);
    }

    [Fact]
    public void Comparison_ReturnsNetAndFullWorthDeltas()
    {
        var current = BasicProfile(60_000m);
        var offer = current with
        {
            Name = "Offer",
            AnnualGross = 66_000m,
            Benefits = new[] { new CompensationBenefitInput("Deutschlandticket", 49m, 49m) }
        };

        var result = GermanCompensationCalculator.Compare(new CompensationComparisonRequest(current, offer));

        Assert.True(result.CashNetDeltaAnnual > 0m);
        Assert.True(result.FullWorthValueDeltaAnnual > result.CashNetDeltaAnnual);
    }

    private static CompensationProfileInput BasicProfile(decimal annualGross) => new(
        Name: "Current",
        AnnualGross: annualGross,
        StateCode: "BW",
        ChurchTax: false,
        ChildrenUnder25: 1,
        ChildlessCareSurcharge: false,
        HealthInsuranceAdditionalRatePercent: 2.9m);
}
