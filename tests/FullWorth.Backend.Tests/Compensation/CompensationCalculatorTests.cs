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
    public void AnnualTaxAllowance_ReducesEstimatedWageTax()
    {
        var normal = GermanCompensationCalculator.WageTax2026(60_000m, BasicProfile(60_000m));
        var withAllowance = GermanCompensationCalculator.WageTax2026(60_000m, BasicProfile(60_000m) with { AnnualTaxAllowance = 2_000m });

        Assert.True(withAllowance.EstimatedIncomeTaxAnnual < normal.EstimatedIncomeTaxAnnual);
    }

    [Fact]
    public void ChildlessCareSurcharge_DoesNotApplyBelowAge23()
    {
        var under23 = GermanCompensationCalculator.Calculate(BasicProfile(50_000m) with
        {
            ChildrenUnder25 = 0,
            Age = 22,
            ChildlessCareSurcharge = true
        });
        var age23 = GermanCompensationCalculator.Calculate(BasicProfile(50_000m) with
        {
            ChildrenUnder25 = 0,
            Age = 23,
            ChildlessCareSurcharge = true
        });

        Assert.True(age23.SocialInsurance.CareAnnual > under23.SocialInsurance.CareAnnual);
    }

    [Fact]
    public void InsuranceExemptions_RemoveConfiguredRvAndAvContributions()
    {
        var exempt = GermanCompensationCalculator.Calculate(BasicProfile(60_000m) with
        {
            PensionInsuranceEnabled = false,
            UnemploymentInsuranceEnabled = false
        });

        Assert.Equal(0m, exempt.SocialInsurance.PensionAnnual);
        Assert.Equal(0m, exempt.SocialInsurance.UnemploymentAnnual);
        Assert.Equal(0m, exempt.SocialInsurance.EmployerPensionAnnual);
        Assert.Equal(0m, exempt.SocialInsurance.EmployerUnemploymentAnnual);
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

    [Fact]
    public void RegularMonthNet_IsIndependentOfBonusAndSalaryCount()
    {
        const decimal monthly = 3_240m;
        var profile = BasicProfile(monthly * 13m) with
        {
            SalaryPaymentsPerYear = 13,
            AnnualBonus = 4_000m,
            OccupationalPension = new OccupationalPensionInput(EmployeeContributionMonthly: 210.43m),
            CompanyCar = new CompanyCarInput(Enabled: true, ListPrice: 45_000m, TaxableListPriceFactor: 1m, OneWayCommuteKm: 20m)
        };

        var withBonus = GermanCompensationCalculator.Calculate(profile);
        var noBonus = GermanCompensationCalculator.Calculate(profile with { AnnualBonus = 0m });
        var moreSalaries = GermanCompensationCalculator.Calculate(profile with { SalaryPaymentsPerYear = 14, AnnualGross = monthly * 14m });

        // A normal month must not move when only the bonus or the number of salary payments changes.
        Assert.Equal(withBonus.EstimatedCashNetMonthly, noBonus.EstimatedCashNetMonthly);
        Assert.Equal(withBonus.EstimatedCashNetMonthly, moreSalaries.EstimatedCashNetMonthly);

        // The yearly average, however, DOES move: the bonus and the extra salary raise the annual net.
        Assert.True(withBonus.EstimatedAverageCashNetMonthly > noBonus.EstimatedAverageCashNetMonthly);
        Assert.True(moreSalaries.EstimatedAverageCashNetMonthly > withBonus.EstimatedAverageCashNetMonthly);
    }

    [Fact]
    public void RegularMonthNet_ExcludesTheBonusThatTheYearlyAverageIncludes()
    {
        var result = GermanCompensationCalculator.Calculate(
            BasicProfile(42_120m) with { SalaryPaymentsPerYear = 13, AnnualBonus = 4_000m });

        // The normal month is taxed on the regular salary only (42.120 × 12/13 = 38.880 a year), so it is a
        // smaller, bonus-free figure. The yearly average spreads the 13th salary and the bonus over 12 months
        // and is therefore higher — the whole point of keeping the two numbers separate.
        Assert.True(result.EstimatedAverageCashNetMonthly > result.EstimatedCashNetMonthly);
        Assert.Equal(
            Math.Round(result.EstimatedCashNetAnnual / 12m, 2, MidpointRounding.AwayFromZero),
            result.EstimatedAverageCashNetMonthly);

        // The normal month must equal the net of a bonus-free, 12-payment year over twelve months.
        var regularOnly = GermanCompensationCalculator.Calculate(
            BasicProfile(38_880m) with { SalaryPaymentsPerYear = 12, AnnualBonus = 0m });
        Assert.Equal(regularOnly.EstimatedCashNetMonthly, result.EstimatedCashNetMonthly);
    }

    [Fact]
    public void SimpleSalary_RegularMonthEqualsAverageMonth()
    {
        var result = GermanCompensationCalculator.Calculate(BasicProfile(60_000m));

        // Twelve equal payments, no bonus: the normal month and the yearly average are the same value.
        Assert.Equal(result.EstimatedAverageCashNetMonthly, result.EstimatedCashNetMonthly);
        Assert.Equal(
            Math.Round(result.EstimatedCashNetAnnual / 12m, 2, MidpointRounding.AwayFromZero),
            result.EstimatedCashNetMonthly);
    }

    [Fact]
    public void OneOffTaxFreePayment_AddsToAnnualNetInFull_ButLeavesRegularMonthUnchanged()
    {
        var baseProfile = BasicProfile(42_000m);
        var withCorona = baseProfile with
        {
            OneOffPayments = new[]
            {
                new OneOffPaymentInput("Corona-Prämie", 500m, Month: 12, Taxable: false, SocialInsuranceLiable: false)
            }
        };

        var baseline = GermanCompensationCalculator.Calculate(baseProfile);
        var result = GermanCompensationCalculator.Calculate(withCorona);

        // A tax- and SV-free one-off reaches the yearly net in full…
        Assert.Equal(baseline.EstimatedCashNetAnnual + 500m, result.EstimatedCashNetAnnual);
        // …but a normal monthly payslip is unaffected.
        Assert.Equal(baseline.EstimatedCashNetMonthly, result.EstimatedCashNetMonthly);
    }

    [Fact]
    public void OneOffTaxablePayment_AddsLessThanItsGross_AndLeavesRegularMonthUnchanged()
    {
        var baseProfile = BasicProfile(42_000m);
        var withChristmasPay = baseProfile with
        {
            OneOffPayments = new[]
            {
                new OneOffPaymentInput("Weihnachtsgeld", 2_000m, Month: 11, Taxable: true, SocialInsuranceLiable: true)
            }
        };

        var baseline = GermanCompensationCalculator.Calculate(baseProfile);
        var result = GermanCompensationCalculator.Calculate(withChristmasPay);

        var netGain = result.EstimatedCashNetAnnual - baseline.EstimatedCashNetAnnual;
        Assert.True(netGain > 0m && netGain < 2_000m); // taxed and charged, so net gain is below the gross.
        Assert.Equal(baseline.EstimatedCashNetMonthly, result.EstimatedCashNetMonthly);
    }

    [Fact]
    public void OneOffPayment_TaxableButSvFree_IsTaxedWithoutSocialInsurance()
    {
        var baseProfile = BasicProfile(42_000m);
        var svFreeButTaxable = baseProfile with
        {
            OneOffPayments = new[]
            {
                new OneOffPaymentInput("SV-freier Zuschuss", 1_000m, Month: 6, Taxable: true, SocialInsuranceLiable: false)
            }
        };
        var fullyContributory = baseProfile with
        {
            OneOffPayments = new[]
            {
                new OneOffPaymentInput("Voll beitragspflichtig", 1_000m, Month: 6, Taxable: true, SocialInsuranceLiable: true)
            }
        };

        var baseline = GermanCompensationCalculator.Calculate(baseProfile);
        var svFree = GermanCompensationCalculator.Calculate(svFreeButTaxable);
        var contributory = GermanCompensationCalculator.Calculate(fullyContributory);

        var svFreeGain = svFree.EstimatedCashNetAnnual - baseline.EstimatedCashNetAnnual;
        var contributoryGain = contributory.EstimatedCashNetAnnual - baseline.EstimatedCashNetAnnual;

        // Taxable, so it nets less than its gross, but SV-free keeps more than a fully contributory payment.
        Assert.True(svFreeGain > 0m && svFreeGain < 1_000m);
        Assert.True(svFreeGain > contributoryGain);
        // An SV-free payment leaves social-insurance contributions untouched…
        Assert.Equal(baseline.SocialInsurance.TotalAnnual, svFree.SocialInsurance.TotalAnnual);
        // …and never touches a normal month.
        Assert.Equal(baseline.EstimatedCashNetMonthly, svFree.EstimatedCashNetMonthly);
    }

    [Fact]
    public void HistoricalAge_FromBirthDate_DrivesTheChildlessCareSurcharge()
    {
        // Born mid-2000: 20 in tax year 2020 (below 23 → no surcharge), 26 in 2026 (surcharge applies).
        var profile = BasicProfile(30_000m) with
        {
            ChildrenUnder25 = 0,
            ChildlessCareSurcharge = true,
            Age = null,
            BirthDate = new DateOnly(2000, 6, 1)
        };

        var young = GermanCompensationCalculator.Calculate(profile with { TaxYear = 2020 });
        var older = GermanCompensationCalculator.Calculate(profile with { TaxYear = 2026 });

        Assert.True(older.SocialInsurance.CareAnnual > young.SocialInsurance.CareAnnual);
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
