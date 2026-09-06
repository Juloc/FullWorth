namespace FullWorth.Backend.Modules.Compensation;

public static class GermanCompensationCalculator
{
    public const decimal HealthCareContributionCeiling2026 = 69_750m;
    public const decimal PensionUnemploymentContributionCeiling2026 = 101_400m;
    public const decimal BavTaxFreeLimit2026 = PensionUnemploymentContributionCeiling2026 * 0.08m;
    public const decimal BavSocialFreeLimit2026 = PensionUnemploymentContributionCeiling2026 * 0.04m;

    private const decimal EmployeeLumpSum = 1_230m;
    private const decimal SpecialExpenseLumpSum = 36m;
    private const decimal SingleParentRelief = 4_260m;
    private const decimal ChildAllowanceFull2026 = 9_756m;
    private const decimal ChildAllowanceHalf2026 = 4_878m;
    private const decimal WageTaxClass5W1 = 14_071m;
    private const decimal WageTaxClass5W2 = 34_939m;
    private const decimal WageTaxClass5W3 = 222_260m;

    public static CompensationCalculationResult Calculate(CompensationProfileInput input)
    {
        Validate(input);
        var raw = CalculateRaw(input);
        var plus100 = CalculateRaw(input with { AnnualGross = input.AnnualGross + 100m });
        var marginal = RoundMoney(plus100.CashNetAnnual - raw.CashNetAnnual);

        var noCarInput = input with { CompanyCar = (input.CompanyCar ?? new CompanyCarInput()) with { Enabled = false } };
        var noCarRaw = CalculateRaw(noCarInput);
        var carCashImpact = RoundMoney(noCarRaw.CashNetAnnual - raw.CashNetAnnual);

        var car = input.CompanyCar ?? new CompanyCarInput();
        var pension = input.OccupationalPension ?? new OccupationalPensionInput();
        var benefits = (input.Benefits ?? Array.Empty<CompensationBenefitInput>())
            .Select(b => new BenefitAnalysis(
                b.Name.Trim(),
                RoundMoney(b.EmployerCostMonthly * 12m),
                RoundMoney(b.PersonalValueMonthly * 12m),
                RoundMoney(b.TaxableBenefitMonthly * 12m),
                RoundMoney(b.EmployeeCostMonthly * 12m)))
            .ToArray();

        var carAlternative = car.Enabled ? Math.Max(0m, car.PrivateAlternativeCostMonthly * 12m) : 0m;
        var carEffectiveValue = car.Enabled ? Math.Max(0m, carAlternative - carCashImpact) : 0m;
        var carAnalysis = new CompanyCarAnalysis(
            RoundMoney(raw.CarTaxableAnnual / 12m),
            RoundMoney(raw.CarTaxableAnnual),
            RoundMoney(raw.CarEmployeeCostAnnual),
            RoundMoney(car.Enabled ? car.EmployerCostMonthly * 12m : 0m),
            RoundMoney(carAlternative),
            RoundMoney(carCashImpact),
            RoundMoney(carEffectiveValue));

        var bavEmployeeAnnual = Math.Max(0m, pension.EmployeeContributionMonthly * 12m);
        var bavEmployerAnnual = Math.Max(0m, pension.EmployerContributionMonthly * 12m);
        var noBavRaw = CalculateRaw(input with
        {
            OccupationalPension = pension with { EmployeeContributionMonthly = 0m, EmployerContributionMonthly = 0m }
        });
        var netSacrifice = Math.Max(0m, noBavRaw.CashNetAnnual - raw.CashNetAnnual);
        var totalInvested = bavEmployeeAnnual + bavEmployerAnnual;
        var projected = ProjectRecurringAnnualContribution(totalInvested, pension.ProjectionYears, pension.ExpectedAnnualReturnPercent);
        var pensionAnalysis = new OccupationalPensionAnalysis(
            RoundMoney(bavEmployeeAnnual),
            RoundMoney(bavEmployerAnnual),
            RoundMoney(Math.Min(bavEmployeeAnnual, BavTaxFreeLimit2026)),
            RoundMoney(Math.Min(bavEmployeeAnnual, BavSocialFreeLimit2026)),
            RoundMoney(netSacrifice),
            RoundMoney(totalInvested),
            netSacrifice <= 0m ? 0m : Math.Round(totalInvested / netSacrifice, 3),
            RoundMoney(projected));

        // Cash net already contains taxes, employee contributions and other cash costs. FullWorth adds
        // the economic value received on top of that cash: private-car replacement value, the full
        // amount invested into bAV and other benefits. Car net cost must not be subtracted twice.
        var personalBenefits = benefits.Sum(b => b.PersonalValueAnnual) + carAlternative + totalInvested;
        var totalEmployerCost = raw.EmployerCostAnnual;
        var fullWorth = raw.CashNetAnnual + personalBenefits;
        var workingHours = EstimateAnnualWorkingHours(input.WeeklyHours, input.VacationDays);

        return new CompensationCalculationResult(
            input.Name.Trim(),
            RoundMoney(input.AnnualGross),
            RoundMoney(input.AnnualBonus),
            RoundMoney(input.AnnualGross + input.AnnualBonus),
            RoundMoney(raw.CashNetAnnual),
            RoundMoney(raw.CashNetAnnual / 12m),
            RoundMoney((input.AnnualGross + input.AnnualBonus) - raw.CashNetAnnual),
            input.AnnualGross + input.AnnualBonus <= 0m ? 0m : Math.Round(raw.CashNetAnnual / (input.AnnualGross + input.AnnualBonus) * 100m, 2),
            RoundMoney(totalEmployerCost),
            RoundMoney(personalBenefits),
            RoundMoney(fullWorth),
            workingHours <= 0m ? 0m : RoundMoney(fullWorth / workingHours),
            marginal,
            new TaxBreakdown(raw.IncomeTaxAnnual, raw.SoliAnnual, raw.ChurchTaxAnnual, raw.TaxableIncomeAnnual),
            raw.SocialInsurance,
            carAnalysis,
            pensionAnalysis,
            benefits,
            Assumptions());
    }

    public static CompensationComparisonResult Compare(CompensationComparisonRequest request)
    {
        var left = Calculate(request.Left);
        var right = Calculate(request.Right);
        return new CompensationComparisonResult(
            left,
            right,
            RoundMoney(right.EstimatedCashNetAnnual - left.EstimatedCashNetAnnual),
            RoundMoney(right.EmployerTotalCostAnnual - left.EmployerTotalCostAnnual),
            RoundMoney(right.FullWorthCompensationValueAnnual - left.FullWorthCompensationValueAnnual),
            RoundMoney(right.EffectiveNetValuePerWorkingHour - left.EffectiveNetValuePerWorkingHour));
    }

    public static decimal IncomeTax2026(decimal taxableIncome)
    {
        var x = Math.Floor(Math.Max(0m, taxableIncome));
        decimal tax;
        if (x <= 12_348m) tax = 0m;
        else if (x <= 17_799m)
        {
            var y = (x - 12_348m) / 10_000m;
            tax = (914.51m * y + 1_400m) * y;
        }
        else if (x <= 69_878m)
        {
            var z = (x - 17_799m) / 10_000m;
            tax = (173.10m * z + 2_397m) * z + 1_034.87m;
        }
        else if (x <= 277_825m) tax = 0.42m * x - 11_135.63m;
        else tax = 0.45m * x - 19_470.38m;
        return RoundMoney(Math.Max(0m, tax));
    }

    /// <summary>
    /// Tax-class-aware 2026 wage-tax planning calculation derived from the BMF PAP structure.
    /// It covers ordinary statutory-insurance employment and the six tax classes. It is intentionally
    /// not presented as a full payroll engine for every PAP input (private insurance, allowances,
    /// pension payments, factor procedure and special payments can require additional inputs).
    /// </summary>
    public static TaxBreakdown WageTax2026(decimal annualTaxableGross, CompensationProfileInput input)
    {
        Validate(input);
        var gross = Math.Max(0m, annualTaxableGross);
        var employeeLump = input.TaxClass == 6 ? 0m : EmployeeLumpSum;
        var specialExpense = input.TaxClass switch
        {
            6 => 0m,
            3 => SpecialExpenseLumpSum * 2m,
            _ => SpecialExpenseLumpSum
        };
        var singleParent = input.TaxClass == 2 ? SingleParentRelief : 0m;
        var provisionAllowance = WageTaxProvisionAllowance2026(gross, input);
        var taxableIncome = Math.Max(0m, gross - employeeLump - specialExpense - singleParent - provisionAllowance - Math.Max(0m, input.AnnualTaxAllowance));

        var incomeTax = ApplyTaxClass4Factor(WageTaxForClass2026(taxableIncome, input.TaxClass), input);
        var childAllowance = input.ChildAllowanceUnits is >= 0m
            ? input.ChildAllowanceUnits.Value * ChildAllowanceFull2026
            : input.TaxClass switch
            {
                3 => Math.Max(0, input.ChildrenUnder25) * ChildAllowanceFull2026,
                1 or 2 or 4 => Math.Max(0, input.ChildrenUnder25) * ChildAllowanceHalf2026,
                _ => 0m
            };
        var soliTaxableIncome = Math.Max(0m, taxableIncome - childAllowance);
        var soliAssessmentTax = ApplyTaxClass4Factor(WageTaxForClass2026(soliTaxableIncome, input.TaxClass), input);
        var soliLimit = input.TaxClass == 3 ? 40_700m : 20_350m;
        var fullSoli = soliAssessmentTax * 0.055m;
        var reducedSoli = Math.Max(0m, (soliAssessmentTax - soliLimit) * 0.119m);
        var soli = soliAssessmentTax <= soliLimit ? 0m : Math.Min(fullSoli, reducedSoli);
        var churchRate = input.StateCode.Trim().ToUpperInvariant() is "BW" or "BY" ? 0.08m : 0.09m;
        var church = input.ChurchTax ? soliAssessmentTax * churchRate : 0m;

        return new TaxBreakdown(
            RoundMoney(incomeTax),
            RoundMoney(soli),
            RoundMoney(church),
            RoundMoney(taxableIncome));
    }

    public static decimal DetermineCompanyCarListPriceFactor(CompanyCarInput input)
    {
        var manual = input.TaxableListPriceFactor is 0.25m or 0.5m or 1m
            ? input.TaxableListPriceFactor
            : 1m;
        var type = (input.VehicleType ?? "manual").Trim().ToLowerInvariant();
        if (type is "" or "manual") return manual;
        if (type is "combustion" or "ice") return 1m;

        var acquisition = input.AcquisitionDate ?? new DateOnly(2026, 1, 1);
        if (type is "electric" or "bev")
        {
            var limit = acquisition >= new DateOnly(2025, 7, 1) ? 100_000m
                : acquisition >= new DateOnly(2024, 1, 1) ? 70_000m
                : 60_000m;
            return input.ListPrice <= limit ? 0.25m : 0.5m;
        }

        if (type is "hybrid" or "phev")
        {
            var minimumRange = acquisition >= new DateOnly(2025, 1, 1) ? 80m
                : acquisition >= new DateOnly(2022, 1, 1) ? 60m
                : 40m;
            var qualifiesByRange = input.ElectricRangeKm >= minimumRange;
            var qualifiesByCo2 = input.Co2GramsPerKm > 0m && input.Co2GramsPerKm <= 50m;
            return qualifiesByRange || qualifiesByCo2 ? 0.5m : 1m;
        }

        return manual;
    }

    public static decimal CompanyCarTaxableBenefitAnnual(CompanyCarInput input)
    {
        if (!input.Enabled || input.ListPrice <= 0m) return 0m;
        var factor = DetermineCompanyCarListPriceFactor(input);
        var taxableListPrice = input.ListPrice * factor;
        var privateUse = taxableListPrice * 0.01m;
        var distance = Math.Max(0m, input.OneWayCommuteKm);
        var commute = string.Equals(input.CommuteMethod, "daily", StringComparison.OrdinalIgnoreCase)
            ? taxableListPrice * 0.00002m * distance * Math.Clamp(input.CommuteDaysPerMonth, 0, 31)
            : taxableListPrice * 0.0003m * distance;
        var monthly = Math.Max(0m, privateUse + commute - Math.Max(0m, input.EmployeeContributionMonthly));
        return RoundMoney(monthly * 12m);
    }

    private static RawResult CalculateRaw(CompensationProfileInput input)
    {
        var car = input.CompanyCar ?? new CompanyCarInput();
        var pension = input.OccupationalPension ?? new OccupationalPensionInput();
        var benefits = input.Benefits ?? Array.Empty<CompensationBenefitInput>();

        var cashGross = Math.Max(0m, input.AnnualGross + input.AnnualBonus);
        var carTaxable = CompanyCarTaxableBenefitAnnual(car);
        var otherTaxableBenefits = benefits.Sum(b => Math.Max(0m, b.TaxableBenefitMonthly) * 12m);
        var bavEmployee = Math.Max(0m, pension.EmployeeContributionMonthly * 12m);
        var taxExemptBav = Math.Min(bavEmployee, BavTaxFreeLimit2026);
        var socialExemptBav = Math.Min(bavEmployee, BavSocialFreeLimit2026);

        var taxPayrollBase = Math.Max(0m, cashGross + carTaxable + otherTaxableBenefits - taxExemptBav);
        var socialPayrollBase = Math.Max(0m, cashGross + carTaxable + otherTaxableBenefits - socialExemptBav);
        var social = SocialInsurance2026(socialPayrollBase, input);
        var tax = WageTax2026(taxPayrollBase, input);

        var carEmployeeCost = car.Enabled ? Math.Max(0m, car.EmployeeContributionMonthly * 12m) : 0m;
        var otherEmployeeCosts = benefits.Sum(b => Math.Max(0m, b.EmployeeCostMonthly) * 12m);
        var cashNet = cashGross - bavEmployee - carEmployeeCost - otherEmployeeCosts
            - social.TotalAnnual - tax.EstimatedIncomeTaxAnnual - tax.EstimatedSolidaritySurchargeAnnual - tax.EstimatedChurchTaxAnnual;

        var employerBenefitCosts = benefits.Sum(b => Math.Max(0m, b.EmployerCostMonthly) * 12m);
        var employerCarCost = car.Enabled ? Math.Max(0m, car.EmployerCostMonthly * 12m) : 0m;
        var employerBav = Math.Max(0m, pension.EmployerContributionMonthly * 12m);
        var employerCost = cashGross + social.EmployerTotalAnnual + employerBav + employerCarCost + employerBenefitCosts;

        return new RawResult(
            RoundMoney(cashNet),
            RoundMoney(employerCost),
            tax.EstimatedTaxableIncomeAnnual,
            tax.EstimatedIncomeTaxAnnual,
            tax.EstimatedSolidaritySurchargeAnnual,
            tax.EstimatedChurchTaxAnnual,
            social,
            carTaxable,
            carEmployeeCost);
    }

    private static decimal ApplyTaxClass4Factor(decimal tax, CompensationProfileInput input)
    {
        if (input.TaxClass != 4) return tax;
        var factor = input.TaxClass4Factor <= 0m ? 1m : Math.Clamp(input.TaxClass4Factor, 0.001m, 1m);
        return RoundMoney(tax * factor);
    }

    private static decimal WageTaxForClass2026(decimal taxableIncome, int taxClass) => taxClass switch
    {
        3 => RoundMoney(2m * IncomeTax2026(taxableIncome / 2m)),
        5 or 6 => WageTaxClass56_2026(taxableIncome),
        _ => IncomeTax2026(taxableIncome)
    };

    private static decimal WageTaxClass56_2026(decimal taxableIncome)
    {
        var x = Math.Max(0m, taxableIncome);
        if (x <= 0m) return 0m;

        if (x > WageTaxClass5W2)
        {
            var tax = WageTaxClass56Step(WageTaxClass5W2);
            if (x > WageTaxClass5W3)
                tax += (WageTaxClass5W3 - WageTaxClass5W2) * 0.42m + (x - WageTaxClass5W3) * 0.45m;
            else
                tax += (x - WageTaxClass5W2) * 0.42m;
            return RoundMoney(tax);
        }

        var result = WageTaxClass56Step(x);
        if (x > WageTaxClass5W1)
        {
            var upperComparison = WageTaxClass56Step(WageTaxClass5W1) + (x - WageTaxClass5W1) * 0.42m;
            result = Math.Min(result, upperComparison);
        }
        return RoundMoney(result);
    }

    private static decimal WageTaxClass56Step(decimal taxableIncome)
    {
        var difference = (IncomeTax2026(taxableIncome * 1.25m) - IncomeTax2026(taxableIncome * 0.75m)) * 2m;
        return Math.Max(difference, taxableIncome * 0.14m);
    }

    private static decimal WageTaxProvisionAllowance2026(decimal annualGross, CompensationProfileInput input)
    {
        var pensionBase = Math.Min(Math.Max(0m, annualGross), PensionUnemploymentContributionCeiling2026);
        var pension = input.PensionInsuranceEnabled == false ? 0m : pensionBase * 0.093m;
        var healthCareBase = Math.Min(Math.Max(0m, annualGross), HealthCareContributionCeiling2026);
        // §39b PAP Vorsorgepauschale uses the reduced 7.0% statutory-health employee rate,
        // plus half of the fund-specific additional contribution.
        var healthRate = 0.07m + Math.Clamp(input.HealthInsuranceAdditionalRatePercent, 0m, 10m) / 200m;
        var careRate = CareEmployeeRate(input);
        return pension + healthCareBase * (healthRate + careRate);
    }

    private static SocialInsuranceBreakdown SocialInsurance2026(decimal annualBase, CompensationProfileInput input)
    {
        var rvAvBase = Math.Min(Math.Max(0m, annualBase), PensionUnemploymentContributionCeiling2026);
        var kvPvBase = Math.Min(Math.Max(0m, annualBase), HealthCareContributionCeiling2026);
        var additionalRate = Math.Clamp(input.HealthInsuranceAdditionalRatePercent, 0m, 10m) / 100m;

        var pension = input.PensionInsuranceEnabled == false ? 0m : rvAvBase * 0.093m;
        var unemployment = input.UnemploymentInsuranceEnabled == false ? 0m : rvAvBase * 0.013m;
        var health = kvPvBase * (0.073m + additionalRate / 2m);
        var care = kvPvBase * CareEmployeeRate(input);

        var saxony = input.StateCode.Trim().Equals("SN", StringComparison.OrdinalIgnoreCase);
        var employerPension = input.PensionInsuranceEnabled == false ? 0m : rvAvBase * 0.093m;
        var employerUnemployment = input.UnemploymentInsuranceEnabled == false ? 0m : rvAvBase * 0.013m;
        var employerHealth = kvPvBase * (0.073m + additionalRate / 2m);
        var employerCare = kvPvBase * (saxony ? 0.013m : 0.018m);

        return new SocialInsuranceBreakdown(
            RoundMoney(pension),
            RoundMoney(unemployment),
            RoundMoney(health),
            RoundMoney(care),
            RoundMoney(pension + unemployment + health + care),
            RoundMoney(employerPension),
            RoundMoney(employerUnemployment),
            RoundMoney(employerHealth),
            RoundMoney(employerCare),
            RoundMoney(employerPension + employerUnemployment + employerHealth + employerCare));
    }

    private static decimal CareEmployeeRate(CompensationProfileInput input)
    {
        var saxony = input.StateCode.Trim().Equals("SN", StringComparison.OrdinalIgnoreCase);
        var rate = saxony ? 0.023m : 0.018m;
        var age = input.Age ?? 23;
        if (age >= 23 && input.ChildrenUnder25 <= 0 && input.ChildlessCareSurcharge)
            rate += 0.006m;
        else if (input.ChildrenUnder25 > 1)
            rate = Math.Max(0m, rate - 0.0025m * (Math.Min(5, input.ChildrenUnder25) - 1));
        return rate;
    }

    private static decimal ProjectRecurringAnnualContribution(decimal annualContribution, int years, decimal annualReturnPercent)
    {
        if (annualContribution <= 0m || years <= 0) return 0m;
        var cappedYears = Math.Min(years, 60);
        var rate = Math.Clamp(annualReturnPercent, -20m, 20m) / 100m;
        if (rate == 0m) return annualContribution * cappedYears;
        decimal value = 0m;
        for (var i = 0; i < cappedYears; i++) value = value * (1m + rate) + annualContribution;
        return Math.Max(0m, value);
    }

    private static decimal EstimateAnnualWorkingHours(decimal weeklyHours, int vacationDays)
    {
        var hours = Math.Clamp(weeklyHours, 1m, 80m);
        var days = Math.Clamp(vacationDays, 0, 60);
        var hoursPerDay = hours / 5m;
        return Math.Max(1m, hours * 52m - days * hoursPerDay);
    }

    private static CompensationAssumptions Assumptions() => new(
        2026,
        "tax-class-aware annualized wage-tax planning estimate",
        "BMF PAP 2026 / EStG §32a; tax classes 1-6, tax-class-IV factor and statutory-insurance Vorsorgepauschale modeled locally",
        "BMAS/BMG/BA 2026 contribution rates and ceilings",
        InflationIndex.Source,
        InflationIndex.DataAsOf,
        "Planning estimate only. The full BMF PAP has additional inputs for exact special-payment payroll, private insurance, individual allowances and pension income; actual payroll and tax assessment can differ.");

    private static void Validate(CompensationProfileInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Name)) throw new ArgumentException("Name is required.");
        if (input.AnnualGross < 0m || input.AnnualBonus < 0m) throw new ArgumentOutOfRangeException(nameof(input.AnnualGross));
        if (input.TaxClass is < 1 or > 6) throw new ArgumentOutOfRangeException(nameof(input.TaxClass));
        if (input.TaxClass == 4 && (input.TaxClass4Factor < 0m || input.TaxClass4Factor > 1m))
            throw new ArgumentOutOfRangeException(nameof(input.TaxClass4Factor));
        if (input.SalaryPaymentsPerYear != 0 && input.SalaryPaymentsPerYear is < 12 or > 14)
            throw new ArgumentOutOfRangeException(nameof(input.SalaryPaymentsPerYear));
        if (!string.IsNullOrWhiteSpace(input.GrossInputMode)
            && !string.Equals(input.GrossInputMode, "annual", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(input.GrossInputMode, "monthly", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("GrossInputMode must be annual or monthly.", nameof(input.GrossInputMode));
        if (input.AnnualTaxAllowance < 0m) throw new ArgumentOutOfRangeException(nameof(input.AnnualTaxAllowance));
        if (input.ChildAllowanceUnits is < 0m) throw new ArgumentOutOfRangeException(nameof(input.ChildAllowanceUnits));
        if (input.Age is < 0 or > 120) throw new ArgumentOutOfRangeException(nameof(input.Age));
        if (input.ChildrenUnder25 < 0) throw new ArgumentOutOfRangeException(nameof(input.ChildrenUnder25));
        if (input.WeeklyHours <= 0m) throw new ArgumentOutOfRangeException(nameof(input.WeeklyHours));
        if (input.CompanyCar is { } car && (car.ListPrice < 0m || car.OneWayCommuteKm < 0m || car.EmployeeContributionMonthly < 0m
            || car.ElectricRangeKm < 0m || car.Co2GramsPerKm < 0m || car.CommuteDaysPerMonth is < 0 or > 31))
            throw new ArgumentOutOfRangeException(nameof(input.CompanyCar));
        if (input.OccupationalPension is { } pension && (pension.EmployeeContributionMonthly < 0m || pension.EmployerContributionMonthly < 0m))
            throw new ArgumentOutOfRangeException(nameof(input.OccupationalPension));
    }

    private static decimal RoundMoney(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    private sealed record RawResult(
        decimal CashNetAnnual,
        decimal EmployerCostAnnual,
        decimal TaxableIncomeAnnual,
        decimal IncomeTaxAnnual,
        decimal SoliAnnual,
        decimal ChurchTaxAnnual,
        SocialInsuranceBreakdown SocialInsurance,
        decimal CarTaxableAnnual,
        decimal CarEmployeeCostAnnual);
}
