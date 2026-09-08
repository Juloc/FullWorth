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

    // ---------------------------------------------------------------------------------------------------
    // Year-aware calculation (§32a tariff, contribution rates and ceilings of the snapshot's calendar year)
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void TaxYearTable_CoversEveryYearFrom2018ToTheCurrentYear()
    {
        Assert.Equal(2018, TaxYearTable.FirstYear);
        Assert.Equal(GermanCompensationCalculator.CurrentTaxYear, TaxYearTable.LastYear);
        Assert.Equal(
            Enumerable.Range(2018, GermanCompensationCalculator.CurrentTaxYear - 2017).ToArray(),
            TaxYearTable.Years);
    }

    [Fact]
    public void TaxYearTable_ResolvesUnsetAndOutOfRangeYearsWithoutThrowing()
    {
        Assert.Equal(TaxYearTable.LastYear, TaxYearTable.Get(null).Year);
        Assert.Equal(TaxYearTable.LastYear, TaxYearTable.Get(2099).Year);
        Assert.Equal(TaxYearTable.FirstYear, TaxYearTable.Get(1999).Year);
        Assert.Equal(2019, TaxYearTable.Get(2019).Year);
    }

    [Fact]
    public void CurrentYearConstants_StayInSyncWithTheTaxYearTable()
    {
        var current = TaxYearTable.Get(GermanCompensationCalculator.CurrentTaxYear);

        Assert.Equal(GermanCompensationCalculator.HealthCareContributionCeiling2026, current.HealthCareCeilingAnnual);
        Assert.Equal(GermanCompensationCalculator.PensionUnemploymentContributionCeiling2026, current.PensionCeilingAnnualWest);
        Assert.Equal(GermanCompensationCalculator.BavTaxFreeLimit2026, current.BavTaxFreeLimit);
        Assert.Equal(GermanCompensationCalculator.BavSocialFreeLimit2026, current.BavSocialFreeLimit);
    }

    [Fact]
    public void ClassFiveThresholds_ReproduceThePublished2026Values()
    {
        // The BMF publishes W1/W2/W3 for §39b Abs. 2 Satz 7 EStG; they are derived from the year's §32a tariff
        // here instead of being tabulated a second time. For 2026 the derivation must hit them exactly.
        var tariff = TaxYearTable.Get(2026).IncomeTax;

        Assert.Equal(14_071m, tariff.ClassFiveW1);
        Assert.Equal(34_939m, tariff.ClassFiveW2);
        Assert.Equal(222_260m, tariff.ClassFiveW3);
    }

    [Theory]
    // §32a EStG proportional ("42 %") zone: 0,42 × 60.000 − Subtrahend of the respective year. These are the
    // statutory amounts an official Einkommensteuer table shows for a taxable income of 60.000 €.
    [InlineData(2018, 16_578.25)]
    [InlineData(2019, 16_419.10)]
    [InlineData(2020, 16_236.26)]
    [InlineData(2021, 16_063.37)]
    [InlineData(2022, 15_863.55)]
    public void IncomeTax_MatchesTheStatutoryTariffOfTheYear(int year, double expected)
    {
        Assert.Equal((decimal)expected, GermanCompensationCalculator.IncomeTax(60_000m, year));
    }

    [Theory]
    [InlineData(2018, 9_000)]
    [InlineData(2019, 9_168)]
    [InlineData(2020, 9_408)]
    [InlineData(2021, 9_744)]
    [InlineData(2022, 10_347)]
    [InlineData(2023, 10_908)]
    [InlineData(2024, 11_784)]
    [InlineData(2025, 12_096)]
    [InlineData(2026, 12_348)]
    public void Grundfreibetrag_IsTaxFreeAndTheNextEuroIsTaxed(int year, int basicAllowance)
    {
        Assert.Equal(0m, GermanCompensationCalculator.IncomeTax(basicAllowance, year));
        Assert.True(GermanCompensationCalculator.IncomeTax(basicAllowance + 1m, year) > 0m);
    }

    [Fact]
    public void IncomeTaxTariff_IsContinuousAtEveryZoneBoundary_ForEveryYear()
    {
        // A typo in any coefficient of any year shows up as a jump at a zone boundary, so this pins the whole
        // seeded table at once. The statute itself rounds its constants, hence the small tolerances.
        foreach (var year in TaxYearTable.Years)
        {
            var t = TaxYearTable.Get(year).IncomeTax;

            Assert.InRange(Math.Abs(t.Tax(t.Zone2Upper) - t.Zone3Base), 0m, 0.20m);
            Assert.InRange(
                Math.Abs(t.Tax(t.Zone3Upper) - (t.Zone4Rate * t.Zone3Upper - t.Zone4Subtrahend)),
                0m, 2m);
            Assert.InRange(
                Math.Abs((t.Zone4Rate * t.Zone4Upper - t.Zone4Subtrahend)
                    - (t.Zone5Rate * t.Zone4Upper - t.Zone5Subtrahend)),
                0m, 0.01m);
        }
    }

    [Fact]
    public void SameGross_YieldsADifferentNetIn2019ThanIn2025()
    {
        var profile = BasicProfile(60_000m);
        var in2019 = GermanCompensationCalculator.Calculate(profile with { TaxYear = 2019 });
        var in2025 = GermanCompensationCalculator.Calculate(profile with { TaxYear = 2025 });

        Assert.NotEqual(in2019.EstimatedCashNetAnnual, in2025.EstimatedCashNetAnnual);
        // 2019 taxed the same nominal income far harder (Grundfreibetrag 9.168 € vs 12.096 €, and the
        // Vorsorgepauschale only recognised 76 % of the pension contribution), and it still charged
        // Solidaritätszuschlag, which the 2021 Freigrenze reform removed for this income.
        Assert.True(in2019.Taxes.EstimatedIncomeTaxAnnual > in2025.Taxes.EstimatedIncomeTaxAnnual);
        Assert.True(in2019.Taxes.EstimatedSolidaritySurchargeAnnual > 0m);
        Assert.Equal(0m, in2025.Taxes.EstimatedSolidaritySurchargeAnnual);
        // Contributions moved the other way: rates and ceilings both rose.
        Assert.True(in2025.SocialInsurance.TotalAnnual > in2019.SocialInsurance.TotalAnnual);
        Assert.True(in2025.EstimatedCashNetAnnual > in2019.EstimatedCashNetAnnual);

        Assert.Equal(2019, in2019.Assumptions.TaxYear);
        Assert.Equal(2025, in2025.Assumptions.TaxYear);
    }

    [Theory]
    // Pinned so a historical snapshot can never silently move when a new tax year is seeded.
    [InlineData(2018, 34_802.43, 12_573.23)]
    [InlineData(2019, 35_477.03, 11_924.74)]
    [InlineData(2023, 37_108.91, 12_614.33)]
    [InlineData(2026, 37_798.27, 12_690.00)]
    public void HistoricalSnapshot_StaysPinnedToItsOwnYear(int year, double expectedNet, double expectedSocial)
    {
        var result = GermanCompensationCalculator.Calculate(BasicProfile(60_000m) with { TaxYear = year });

        Assert.Equal((decimal)expectedNet, result.EstimatedCashNetAnnual);
        // Employee contributions are the exactly reproducible part: 2018 still put the full Zusatzbeitrag on the
        // employee, and every year has its own rates and Beitragsbemessungsgrenzen.
        Assert.Equal((decimal)expectedSocial, result.SocialInsurance.TotalAnnual);
    }

    [Fact]
    public void UnsetTaxYear_UsesTheNewestSeededYear()
    {
        var withoutYear = GermanCompensationCalculator.Calculate(BasicProfile(60_000m));
        var currentYear = GermanCompensationCalculator.Calculate(
            BasicProfile(60_000m) with { TaxYear = GermanCompensationCalculator.CurrentTaxYear });

        Assert.Equal(currentYear.EstimatedCashNetAnnual, withoutYear.EstimatedCashNetAnnual);
        Assert.Equal(GermanCompensationCalculator.CurrentTaxYear, withoutYear.Assumptions.TaxYear);
    }

    [Fact]
    public void BavLimits_FollowThePensionCeilingOfTheYear()
    {
        // §3 Nr. 63 EStG (8 %) and §1 SvEV (4 %) of the west pension ceiling: 80.400 € in 2019, 101.400 € in 2026.
        Assert.Equal(6_432m, TaxYearTable.Get(2019).BavTaxFreeLimit);
        Assert.Equal(3_216m, TaxYearTable.Get(2019).BavSocialFreeLimit);
        Assert.Equal(8_112m, TaxYearTable.Get(2026).BavTaxFreeLimit);
        Assert.Equal(4_056m, TaxYearTable.Get(2026).BavSocialFreeLimit);

        var bav = new OccupationalPensionInput(EmployeeContributionMonthly: 700m);
        var in2019 = GermanCompensationCalculator.Calculate(
            BasicProfile(80_000m) with { TaxYear = 2019, OccupationalPension = bav });
        var in2026 = GermanCompensationCalculator.Calculate(
            BasicProfile(80_000m) with { TaxYear = 2026, OccupationalPension = bav });

        Assert.Equal(6_432m, in2019.OccupationalPension.TaxExemptEmployeeContributionAnnual);
        Assert.Equal(8_112m, in2026.OccupationalPension.TaxExemptEmployeeContributionAnnual);
    }

    [Fact]
    public void PensionCeiling_UsedTheEastRechtskreisBeforeItWasUnifiedIn2025()
    {
        Assert.Equal(80_400m, TaxYearTable.Get(2019).PensionCeilingAnnual("BW"));
        Assert.Equal(73_800m, TaxYearTable.Get(2019).PensionCeilingAnnual("SN"));
        Assert.Equal(96_600m, TaxYearTable.Get(2025).PensionCeilingAnnual("BW"));
        Assert.Equal(96_600m, TaxYearTable.Get(2025).PensionCeilingAnnual("SN"));

        var west = GermanCompensationCalculator.Calculate(BasicProfile(90_000m) with { TaxYear = 2019, StateCode = "BW" });
        var east = GermanCompensationCalculator.Calculate(BasicProfile(90_000m) with { TaxYear = 2019, StateCode = "TH" });

        Assert.True(east.SocialInsurance.PensionAnnual < west.SocialInsurance.PensionAnnual);
    }

    [Fact]
    public void CareInsurance_UsesTheChildDiscountOnlyFromTheYearItWasIntroduced()
    {
        // The per-child discounts (−0,25 points per child from the 2nd to the 5th) came in on 1.7.2023.
        var manyChildren = BasicProfile(50_000m) with { ChildrenUnder25 = 4, ChildlessCareSurcharge = false };
        var oneChild = manyChildren with { ChildrenUnder25 = 1 };

        var in2022 = GermanCompensationCalculator.Calculate(manyChildren with { TaxYear = 2022 });
        var in2022OneChild = GermanCompensationCalculator.Calculate(oneChild with { TaxYear = 2022 });
        var in2024 = GermanCompensationCalculator.Calculate(manyChildren with { TaxYear = 2024 });
        var in2024OneChild = GermanCompensationCalculator.Calculate(oneChild with { TaxYear = 2024 });

        Assert.Equal(in2022OneChild.SocialInsurance.CareAnnual, in2022.SocialInsurance.CareAnnual);
        Assert.True(in2024.SocialInsurance.CareAnnual < in2024OneChild.SocialInsurance.CareAnnual);
    }

    [Fact]
    public void HealthZusatzbeitrag_WasEmployeeOnlyIn2018AndSharedFrom2019()
    {
        var profile = BasicProfile(40_000m) with { HealthInsuranceAdditionalRatePercent = 1.0m };
        var in2018 = GermanCompensationCalculator.Calculate(profile with { TaxYear = 2018 });
        var in2019 = GermanCompensationCalculator.Calculate(profile with { TaxYear = 2019 });

        // 40.000 € stays below both KV ceilings, so only the split changed: 7,3 % + 1,0 % vs 7,3 % + 0,5 %
        // for the employee, and the mirror image for the employer.
        Assert.Equal(40_000m * 0.083m, in2018.SocialInsurance.HealthAnnual);
        Assert.Equal(40_000m * 0.073m, in2018.SocialInsurance.EmployerHealthAnnual);
        Assert.Equal(40_000m * 0.078m, in2019.SocialInsurance.HealthAnnual);
        Assert.Equal(in2019.SocialInsurance.HealthAnnual, in2019.SocialInsurance.EmployerHealthAnnual);
    }

    [Fact]
    public void RegularMonthNet_StaysIndependentOfBonusAndSalaryCount_InAHistoricalYear()
    {
        const decimal monthly = 3_240m;
        var profile = BasicProfile(monthly * 13m) with
        {
            TaxYear = 2019,
            SalaryPaymentsPerYear = 13,
            AnnualBonus = 4_000m,
            OccupationalPension = new OccupationalPensionInput(EmployeeContributionMonthly: 210.43m)
        };

        var withBonus = GermanCompensationCalculator.Calculate(profile);
        var noBonus = GermanCompensationCalculator.Calculate(profile with { AnnualBonus = 0m });
        var moreSalaries = GermanCompensationCalculator.Calculate(
            profile with { SalaryPaymentsPerYear = 14, AnnualGross = monthly * 14m });

        Assert.Equal(withBonus.EstimatedCashNetMonthly, noBonus.EstimatedCashNetMonthly);
        Assert.Equal(withBonus.EstimatedCashNetMonthly, moreSalaries.EstimatedCashNetMonthly);
        Assert.True(withBonus.EstimatedAverageCashNetMonthly > noBonus.EstimatedAverageCashNetMonthly);

        // …and the historical year is genuinely a different calculation, not the current one.
        var today = GermanCompensationCalculator.Calculate(profile with { TaxYear = null });
        Assert.NotEqual(today.EstimatedCashNetMonthly, withBonus.EstimatedCashNetMonthly);
    }

    [Fact]
    public void Inflation_CoversTwentyEighteenAndClampsOnlyBeforeIt()
    {
        Assert.Equal(2018, InflationIndex.EarliestYear);
        Assert.Equal(98.1m, InflationIndex.GetIndex(new DateOnly(2018, 12, 31)));
        Assert.Equal(99.5m, InflationIndex.GetIndex(new DateOnly(2019, 12, 31)));
        Assert.Equal(100.0m, InflationIndex.GetIndex(new DateOnly(2020, 12, 31)));
        // Anything before the first published year falls back to it instead of to 2020.
        Assert.Equal(98.1m, InflationIndex.GetIndex(new DateOnly(2015, 6, 30)));

        // 2018 → 2026 is roughly +28 % cumulative German CPI, so an old salary needs a clearly larger figure now.
        var adjusted = InflationIndex.AdjustForPurchasingPower(
            30_000m, new DateOnly(2018, 12, 31), new DateOnly(2026, 7, 31));
        Assert.InRange(adjusted, 38_000m, 38_800m);
    }

    [Fact]
    public void UnsetHealthAdditionalRate_UsesTheTaxYearsAverageNotTodays()
    {
        // The average Zusatzbeitrag nearly tripled between 2018 (1,0 %) and 2026 (2,9 %). A snapshot that
        // does not name a fund rate must use its own year's average, or every historical net is too low.
        var profile = BasicProfile(40_000m) with { HealthInsuranceAdditionalRatePercent = null };

        var old = GermanCompensationCalculator.Calculate(profile with { TaxYear = 2018 });
        var pinnedToTodaysRate = GermanCompensationCalculator.Calculate(
            profile with { TaxYear = 2018, HealthInsuranceAdditionalRatePercent = 2.9m });

        // 2018 carried the Zusatzbeitrag employee-only, so a 1,0 % year is markedly cheaper than 2,9 %.
        Assert.True(
            old.SocialInsurance.HealthAnnual < pinnedToTodaysRate.SocialInsurance.HealthAnnual,
            "the 2018 average (1,0 %) must cost the employee less than today's 2,9 %");

        // An explicitly named rate still wins over the year's average.
        var named = GermanCompensationCalculator.Calculate(
            profile with { TaxYear = 2018, HealthInsuranceAdditionalRatePercent = 0m });
        Assert.True(named.SocialInsurance.HealthAnnual < old.SocialInsurance.HealthAnnual);

        // For the current year the fallback is a no-op: the seeded average IS 2,9 %.
        var current = GermanCompensationCalculator.Calculate(profile with { TaxYear = 2026 });
        var currentPinned = GermanCompensationCalculator.Calculate(
            profile with { TaxYear = 2026, HealthInsuranceAdditionalRatePercent = 2.9m });
        Assert.Equal(currentPinned.EstimatedCashNetAnnual, current.EstimatedCashNetAnnual);
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
