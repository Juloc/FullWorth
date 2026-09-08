namespace FullWorth.Backend.Modules.Compensation;

/// <summary>
/// The German payroll/net calculation. There is exactly one formula: every statutory figure it needs comes from
/// <see cref="TaxYearParameters"/>, resolved once per calculation from <see cref="CompensationProfileInput.TaxYear"/>.
/// A snapshot for 2019 is therefore computed with 2019 law and stays stable when later years change.
/// The same single formula also covers a partial year: <see cref="CompensationProfileInput.EmploymentStart"/>
/// and <see cref="CompensationProfileInput.EmploymentEnd"/> only decide how many of the twelve months it
/// charges, they never fork into a second calculation. See <see cref="WageTaxForPaidMonths"/> for the wage-tax
/// treatment of a partial year.
/// </summary>
public static class GermanCompensationCalculator
{
    // Kept as compile-time constants for callers and tests that pin the current year; they are cross-checked
    // against the 2026 row of <see cref="TaxYearTable"/> by a unit test so they cannot silently drift.
    public const decimal HealthCareContributionCeiling2026 = 69_750m;
    public const decimal PensionUnemploymentContributionCeiling2026 = 101_400m;
    public const decimal BavTaxFreeLimit2026 = PensionUnemploymentContributionCeiling2026 * 0.08m;
    public const decimal BavSocialFreeLimit2026 = PensionUnemploymentContributionCeiling2026 * 0.04m;

    public const int CurrentTaxYear = 2026;

    public static CompensationCalculationResult Calculate(CompensationProfileInput input)
    {
        Validate(input);
        var year = TaxYearTable.Resolve(input);

        // Beschäftigungszeitraum: how much of this calendar year the employment actually covers. A profile
        // without a period yields exactly 12m, so it is calculated bit-for-bit as it was before this existed
        // and every stored historical snapshot stays stable.
        var monthsEmployed = EmployedMonthsInYear(input, year);
        var employmentShare = monthsEmployed / 12m;

        var raw = CalculateRaw(input, year, monthsEmployed);
        var plus100 = CalculateRaw(input with { AnnualGross = input.AnnualGross + 100m }, year, monthsEmployed);
        var marginal = RoundMoney(plus100.CashNetAnnual - raw.CashNetAnnual);

        // "Netto normaler Monat" must reflect a single ordinary payslip: the regular monthly salary taxed on a
        // 12-month basis, WITHOUT the 13th/14th salary or the annual bonus (those are one-off "sonstige Bezüge"
        // that only move the yearly average, not a normal month). The regular monthly gross is the contractual
        // annual gross spread over the actual number of salary payments; twelve of those make the ordinary tax
        // year. Recurring monthly items (company car, bAV, benefits) are kept so they still count every month.
        // This makes the regular-month net independent of the bonus and of the number of salary payments —
        // and, by passing a full 12 months on purpose, also of the Beschäftigungszeitraum: a normal month is a
        // normal month whether the year had four of them or twelve.
        var salaryPayments = input.SalaryPaymentsPerYear is >= 12 and <= 14 ? input.SalaryPaymentsPerYear : 12;
        var regularMonthlyBase = RoundMoney(input.AnnualGross * 12m / salaryPayments);
        var regularRaw = CalculateRaw(
            input with { AnnualGross = regularMonthlyBase, AnnualBonus = 0m, OneOffPayments = null }, year, 12m);
        var regularMonthlyNet = RoundMoney(regularRaw.CashNetAnnual / 12m);
        // "Ø Netto pro Monat" spreads the year's FULL net (bonus, every salary payment and every one-off) over
        // the months actually employed — NOT over a fixed 12. Dividing a four-month apprenticeship year by
        // twelve would report a monthly income that never existed; dividing by the four employed months answers
        // the question the number is asked for ("what did I have per month back then"). For a full year the
        // divisor is 12, so an ordinary profile is unchanged.
        var averageMonthlyNet = monthsEmployed <= 0m ? 0m : RoundMoney(raw.CashNetAnnual / monthsEmployed);

        var noCarInput = input with { CompanyCar = (input.CompanyCar ?? new CompanyCarInput()) with { Enabled = false } };
        var noCarRaw = CalculateRaw(noCarInput, year, monthsEmployed);
        var carCashImpact = RoundMoney(noCarRaw.CashNetAnnual - raw.CashNetAnnual);

        var car = input.CompanyCar ?? new CompanyCarInput();
        var pension = input.OccupationalPension ?? new OccupationalPensionInput();
        // Every "…Annual" figure below is a monthly amount multiplied by the months actually employed, so it
        // reports what the year really carried instead of a full twelve months of it.
        var benefits = (input.Benefits ?? Array.Empty<CompensationBenefitInput>())
            .Select(b => new BenefitAnalysis(
                b.Name.Trim(),
                RoundMoney(b.EmployerCostMonthly * monthsEmployed),
                RoundMoney(b.PersonalValueMonthly * monthsEmployed),
                RoundMoney(b.TaxableBenefitMonthly * monthsEmployed),
                RoundMoney(b.EmployeeCostMonthly * monthsEmployed)))
            .ToArray();

        var carAlternative = car.Enabled ? Math.Max(0m, car.PrivateAlternativeCostMonthly * monthsEmployed) : 0m;
        var carEffectiveValue = car.Enabled ? Math.Max(0m, carAlternative - carCashImpact) : 0m;
        var carAnalysis = new CompanyCarAnalysis(
            // The monthly geldwerter Vorteil of the car itself does not depend on how long the year was.
            RoundMoney(CompanyCarTaxableBenefitAnnual(car) / 12m),
            RoundMoney(raw.CarTaxableAnnual),
            RoundMoney(raw.CarEmployeeCostAnnual),
            RoundMoney(car.Enabled ? car.EmployerCostMonthly * monthsEmployed : 0m),
            RoundMoney(carAlternative),
            RoundMoney(carCashImpact),
            RoundMoney(carEffectiveValue));

        var bavEmployeeAnnual = Math.Max(0m, pension.EmployeeContributionMonthly * monthsEmployed);
        var bavEmployerAnnual = Math.Max(0m, pension.EmployerContributionMonthly * monthsEmployed);
        var noBavRaw = CalculateRaw(input with
        {
            OccupationalPension = pension with { EmployeeContributionMonthly = 0m, EmployerContributionMonthly = 0m }
        }, year, monthsEmployed);
        var netSacrifice = Math.Max(0m, noBavRaw.CashNetAnnual - raw.CashNetAnnual);
        var totalInvested = bavEmployeeAnnual + bavEmployerAnnual;
        // The projection answers "what if I keep paying this every year", so it uses the full twelve-month
        // contribution rather than a partial year's share of it.
        var projected = ProjectRecurringAnnualContribution(
            Math.Max(0m, (pension.EmployeeContributionMonthly + pension.EmployerContributionMonthly) * 12m),
            pension.ProjectionYears,
            pension.ExpectedAnnualReturnPercent);
        var pensionAnalysis = new OccupationalPensionAnalysis(
            RoundMoney(bavEmployeeAnnual),
            RoundMoney(bavEmployerAnnual),
            // §3 Nr. 63 EStG / §1 SvEV limits are annual amounts that monthly payroll applies as 1/12 per
            // month, so a partial year gets its proportional share of them — the same pro-rating the payroll
            // base below uses.
            RoundMoney(Math.Min(bavEmployeeAnnual, year.BavTaxFreeLimit * employmentShare)),
            RoundMoney(Math.Min(bavEmployeeAnnual, year.BavSocialFreeLimit * employmentShare)),
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
        // Hours worked shrink with the employment period as well, so the €/hour figure stays comparable
        // between a partial and a full year instead of collapsing.
        var workingHours = EstimateAnnualWorkingHours(input.WeeklyHours, input.VacationDays) * employmentShare;

        return new CompensationCalculationResult(
            input.Name.Trim(),
            Math.Round(monthsEmployed, 2, MidpointRounding.AwayFromZero),
            Math.Round(salaryPayments * employmentShare, 2, MidpointRounding.AwayFromZero),
            RoundMoney(input.AnnualGross),
            RoundMoney(input.AnnualBonus),
            RoundMoney(raw.CashGrossAnnual),
            RoundMoney(raw.CashNetAnnual),
            regularMonthlyNet,
            averageMonthlyNet,
            RoundMoney(raw.CashGrossAnnual - raw.CashNetAnnual),
            raw.CashGrossAnnual <= 0m ? 0m : Math.Round(raw.CashNetAnnual / raw.CashGrossAnnual * 100m, 2),
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
            Assumptions(year));
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

    /// <summary>§32a EStG income tax for the given calendar year (clamped to the seeded year range).</summary>
    public static decimal IncomeTax(decimal taxableIncome, int? taxYear) =>
        TaxYearTable.Get(taxYear).IncomeTax.Tax(taxableIncome);

    /// <summary>§32a EStG income tax for the current tax year.</summary>
    public static decimal IncomeTax2026(decimal taxableIncome) => IncomeTax(taxableIncome, CurrentTaxYear);

    /// <summary>
    /// Tax-class-aware wage-tax planning calculation derived from the BMF PAP structure, using the parameters of
    /// the profile's tax year. It covers ordinary statutory-insurance employment, the six tax classes, the
    /// class-IV factor, ELStAM allowances and common statutory-insurance exceptions. It is intentionally not
    /// presented as a full payroll engine for every PAP input (private insurance, Midijob transition rules,
    /// pension payments and exact special-payment payroll require additional paths).
    /// </summary>
    public static TaxBreakdown WageTax(decimal annualTaxableGross, CompensationProfileInput input) =>
        WageTax(annualTaxableGross, input, TaxYearTable.Resolve(input));

    /// <summary>Wage tax pinned to the current tax year, regardless of the profile's tax year.</summary>
    public static TaxBreakdown WageTax2026(decimal annualTaxableGross, CompensationProfileInput input) =>
        WageTax(annualTaxableGross, input, TaxYearTable.Get(CurrentTaxYear));

    private static TaxBreakdown WageTax(decimal annualTaxableGross, CompensationProfileInput input, TaxYearParameters year)
    {
        Validate(input);
        var gross = Math.Max(0m, annualTaxableGross);
        var employeeLump = input.TaxClass == 6 ? 0m : year.EmployeeLumpSum;
        var specialExpense = input.TaxClass switch
        {
            6 => 0m,
            3 => year.SpecialExpenseLumpSum * 2m,
            _ => year.SpecialExpenseLumpSum
        };
        var singleParent = input.TaxClass == 2 ? year.SingleParentRelief : 0m;
        var provisionAllowance = WageTaxProvisionAllowance(gross, input, year);
        var taxableIncome = Math.Max(0m, gross - employeeLump - specialExpense - singleParent - provisionAllowance - Math.Max(0m, input.AnnualTaxAllowance));

        var incomeTax = ApplyTaxClass4Factor(WageTaxForClass(taxableIncome, input.TaxClass, year), input);
        var childAllowance = input.ChildAllowanceUnits is >= 0m
            ? input.ChildAllowanceUnits.Value * year.ChildAllowanceFull
            : input.TaxClass switch
            {
                3 => Math.Max(0, input.ChildrenUnder25) * year.ChildAllowanceFull,
                1 or 2 or 4 => Math.Max(0, input.ChildrenUnder25) * year.ChildAllowanceHalf,
                _ => 0m
            };
        var soliTaxableIncome = Math.Max(0m, taxableIncome - childAllowance);
        var soliAssessmentTax = ApplyTaxClass4Factor(WageTaxForClass(soliTaxableIncome, input.TaxClass, year), input);
        var soliLimit = input.TaxClass == 3
            ? year.SolidarityExemptionLimitAnnual * 2m
            : year.SolidarityExemptionLimitAnnual;
        var fullSoli = soliAssessmentTax * year.SolidaritySurchargeRate;
        var reducedSoli = Math.Max(0m, (soliAssessmentTax - soliLimit) * year.SolidarityGlideRate);
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

    /// <summary>
    /// The one and only payroll formula. Everything recurring is first expressed as the full twelve-month
    /// ("hochgerechneter") year — which is the basis German monthly payroll actually taxes — and then reduced
    /// to <paramref name="months"/>, the months of the calendar year actually paid. One-off special payments
    /// are never reduced: they belong to the year in full.
    /// </summary>
    /// <param name="months">Paid months of the calendar year; 12 = the whole year (the default for a profile
    /// without a Beschäftigungszeitraum), and at 12 every line below collapses to what it computed before.</param>
    private static RawResult CalculateRaw(CompensationProfileInput input, TaxYearParameters year, decimal months)
    {
        var car = input.CompanyCar ?? new CompanyCarInput();
        var pension = input.OccupationalPension ?? new OccupationalPensionInput();
        var benefits = input.Benefits ?? Array.Empty<CompensationBenefitInput>();

        var monthsPaid = Math.Clamp(months, 0m, 12m);
        var share = monthsPaid / 12m;

        // Full-year figures: what twelve months of this contract look like.
        var cashGross = Math.Max(0m, input.AnnualGross + input.AnnualBonus);
        var carTaxable = CompanyCarTaxableBenefitAnnual(car);
        var otherTaxableBenefits = benefits.Sum(b => Math.Max(0m, b.TaxableBenefitMonthly) * 12m);
        var bavEmployee = Math.Max(0m, pension.EmployeeContributionMonthly * 12m);
        var taxExemptBav = Math.Min(bavEmployee, year.BavTaxFreeLimit);
        var socialExemptBav = Math.Min(bavEmployee, year.BavSocialFreeLimit);

        // One-off special payments for the year (Weihnachtsgeld, Corona-Prämie, …). Each can be independently
        // tax-free and/or SV-free, so a tax- and SV-free payment reaches net in full while a normal one is
        // taxed and charged like the rest of the cash gross. They are a fixed amount for the year, so they are
        // NOT pro-rated by the employment period.
        var oneOff = input.OneOffPayments ?? Array.Empty<OneOffPaymentInput>();
        var oneOffTotal = oneOff.Sum(p => Math.Max(0m, p.Amount));
        var oneOffTaxable = oneOff.Where(p => p.Taxable).Sum(p => Math.Max(0m, p.Amount));
        var oneOffSocial = oneOff.Where(p => p.SocialInsuranceLiable).Sum(p => Math.Max(0m, p.Amount));

        var recurringTaxBase = Math.Max(0m, cashGross + carTaxable + otherTaxableBenefits - taxExemptBav);
        var recurringSocialBase = Math.Max(0m, cashGross + carTaxable + otherTaxableBenefits - socialExemptBav);

        // Social insurance is linear up to the Beitragsbemessungsgrenze, and payroll applies that ceiling per
        // month (1/12 of the annual BBG). Scaling base AND ceiling by the same share therefore reproduces
        // monthly payroll exactly: N paid months carry N/12 of a full year's contributions, and a one-off on
        // top is charged against the remaining proportional ceiling.
        var social = SocialInsurance(recurringSocialBase * share + oneOffSocial, input, year, monthsPaid);
        var tax = WageTaxForPaidMonths(recurringTaxBase, oneOffTaxable, share, input, year);

        var carEmployeeCost = (car.Enabled ? Math.Max(0m, car.EmployeeContributionMonthly * 12m) : 0m) * share;
        var otherEmployeeCosts = benefits.Sum(b => Math.Max(0m, b.EmployeeCostMonthly) * 12m) * share;
        var cashGrossPaid = cashGross * share;
        var bavEmployeePaid = bavEmployee * share;
        var cashNet = cashGrossPaid + oneOffTotal - bavEmployeePaid - carEmployeeCost - otherEmployeeCosts
            - social.TotalAnnual - tax.EstimatedIncomeTaxAnnual - tax.EstimatedSolidaritySurchargeAnnual - tax.EstimatedChurchTaxAnnual;

        var employerBenefitCosts = benefits.Sum(b => Math.Max(0m, b.EmployerCostMonthly) * 12m) * share;
        var employerCarCost = (car.Enabled ? Math.Max(0m, car.EmployerCostMonthly * 12m) : 0m) * share;
        var employerBav = Math.Max(0m, pension.EmployerContributionMonthly * 12m) * share;
        var employerCost = cashGrossPaid + oneOffTotal + social.EmployerTotalAnnual + employerBav + employerCarCost + employerBenefitCosts;

        return new RawResult(
            RoundMoney(cashGrossPaid),
            RoundMoney(cashNet),
            RoundMoney(employerCost),
            tax.EstimatedTaxableIncomeAnnual,
            tax.EstimatedIncomeTaxAnnual,
            tax.EstimatedSolidaritySurchargeAnnual,
            tax.EstimatedChurchTaxAnnual,
            social,
            RoundMoney(carTaxable * share),
            RoundMoney(carEmployeeCost));
    }

    /// <summary>
    /// The year's wage tax, split the way §39b EStG splits it — this is the part where a partial year is NOT
    /// simply "the full-year tax scaled down by the annual amount earned":
    /// <list type="bullet">
    /// <item>Laufender Arbeitslohn (§39b Abs. 2): every paid month is taxed on an ANNUALISED basis — the
    /// month's pay projected onto a full year, tax looked up there, one twelfth withheld. So N paid months
    /// carry exactly N/12 of the wage tax of a full year at that salary, which is what
    /// <c>recurring × share</c> below is. Taxing the part-year TOTAL as if it were the annual income (the
    /// Einkommensteuer view) would be far cheaper — an apprentice earning 4 × 1.000 € would land under the
    /// Grundfreibetrag — but that is not what the payslip does; the employee only gets that difference back
    /// later through the Einkommensteuererklärung.</item>
    /// <item>Sonstige Bezüge (§39b Abs. 3): a one-off payment is taxed as the difference between the year's
    /// wage tax with and without it, on the wage expected in that calendar year — i.e. on the already
    /// pro-rated base, not on the annualised one.</item>
    /// </list>
    /// IMPLEMENTED: the payroll-correct withholding view (monthly annualisation, proportional treatment of
    /// annual allowances, NO Lohnsteuer-Jahresausgleich and no year-end assessment). DIRECTION OF ERROR for a
    /// partial year: the reported tax is the tax actually withheld, which is HIGHER than the tax finally owed,
    /// so the partial year's net is on the conservative (low) side by roughly the refund the tax return would
    /// produce. Modelling that refund is an Einkommensteuer calculation, not a payslip, and would make the
    /// figure incomparable to the payslips this profile is built from.
    /// At <c>share == 1</c> both parts add up to exactly one <see cref="WageTax(decimal, CompensationProfileInput, TaxYearParameters)"/>
    /// call on the whole base, so a full year — and therefore every stored snapshot — is bit-for-bit unchanged.
    /// </summary>
    private static TaxBreakdown WageTaxForPaidMonths(
        decimal recurringAnnualBase,
        decimal oneOffTaxable,
        decimal share,
        CompensationProfileInput input,
        TaxYearParameters year)
    {
        var recurring = WageTax(recurringAnnualBase, input, year);
        if (oneOffTaxable <= 0m)
            return new TaxBreakdown(
                RoundMoney(recurring.EstimatedIncomeTaxAnnual * share),
                RoundMoney(recurring.EstimatedSolidaritySurchargeAnnual * share),
                RoundMoney(recurring.EstimatedChurchTaxAnnual * share),
                RoundMoney(recurring.EstimatedTaxableIncomeAnnual * share));

        var paidBase = recurringAnnualBase * share;
        var withOneOff = WageTax(paidBase + oneOffTaxable, input, year);
        var withoutOneOff = WageTax(paidBase, input, year);
        return new TaxBreakdown(
            RoundMoney(recurring.EstimatedIncomeTaxAnnual * share
                + withOneOff.EstimatedIncomeTaxAnnual - withoutOneOff.EstimatedIncomeTaxAnnual),
            RoundMoney(recurring.EstimatedSolidaritySurchargeAnnual * share
                + withOneOff.EstimatedSolidaritySurchargeAnnual - withoutOneOff.EstimatedSolidaritySurchargeAnnual),
            RoundMoney(recurring.EstimatedChurchTaxAnnual * share
                + withOneOff.EstimatedChurchTaxAnnual - withoutOneOff.EstimatedChurchTaxAnnual),
            RoundMoney(recurring.EstimatedTaxableIncomeAnnual * share
                + withOneOff.EstimatedTaxableIncomeAnnual - withoutOneOff.EstimatedTaxableIncomeAnnual));
    }

    /// <summary>
    /// The months of the profile's calendar year covered by the Beschäftigungszeitraum. No period at all means
    /// the whole year and returns exactly 12m, which keeps every existing profile and stored snapshot on its
    /// current numbers. Partial months follow the German payroll convention of 30 SV-Tage per month, so a whole
    /// month always counts 1 (February included) and an entry on the 16th counts 15/30.
    /// </summary>
    private static decimal EmployedMonthsInYear(CompensationProfileInput input, TaxYearParameters year)
    {
        if (input.EmploymentStart is null && input.EmploymentEnd is null) return 12m;

        // The profile's own calendar year, not the (clamped) tariff year, so a period is always judged against
        // the year it belongs to.
        var calendarYear = input.TaxYear ?? year.Year;
        var yearStart = new DateOnly(calendarYear, 1, 1);
        var yearEnd = new DateOnly(calendarYear, 12, 31);
        var from = input.EmploymentStart is { } start && start > yearStart ? start : yearStart;
        var to = input.EmploymentEnd is { } end && end < yearEnd ? end : yearEnd;
        if (to < from) return 0m;

        var total = 0m;
        for (var month = 1; month <= 12; month++)
        {
            var monthStart = new DateOnly(calendarYear, month, 1);
            var monthEnd = new DateOnly(calendarYear, month, DateTime.DaysInMonth(calendarYear, month));
            var first = from > monthStart ? from : monthStart;
            var last = to < monthEnd ? to : monthEnd;
            if (last < first) continue;
            var firstDay = Math.Min(first.Day, 30);
            var lastDay = last == monthEnd ? 30 : Math.Min(last.Day, 30);
            var days = Math.Clamp(lastDay - firstDay + 1, 0, 30);
            total += days == 30 ? 1m : days / 30m;
        }
        return Math.Clamp(total, 0m, 12m);
    }

    private static decimal ApplyTaxClass4Factor(decimal tax, CompensationProfileInput input)
    {
        if (input.TaxClass != 4) return tax;
        var factor = input.TaxClass4Factor <= 0m ? 1m : Math.Clamp(input.TaxClass4Factor, 0.001m, 1m);
        return RoundMoney(tax * factor);
    }

    private static decimal WageTaxForClass(decimal taxableIncome, int taxClass, TaxYearParameters year) => taxClass switch
    {
        3 => RoundMoney(2m * year.IncomeTax.Tax(taxableIncome / 2m)),
        5 or 6 => WageTaxClass56(taxableIncome, year),
        _ => year.IncomeTax.Tax(taxableIncome)
    };

    private static decimal WageTaxClass56(decimal taxableIncome, TaxYearParameters year)
    {
        var x = Math.Max(0m, taxableIncome);
        if (x <= 0m) return 0m;

        var tariff = year.IncomeTax;
        var w1 = tariff.ClassFiveW1;
        var w2 = tariff.ClassFiveW2;
        var w3 = tariff.ClassFiveW3;

        if (x > w2)
        {
            var tax = WageTaxClass56Step(w2, year);
            if (x > w3)
                tax += (w3 - w2) * tariff.Zone4Rate + (x - w3) * tariff.Zone5Rate;
            else
                tax += (x - w2) * tariff.Zone4Rate;
            return RoundMoney(tax);
        }

        var result = WageTaxClass56Step(x, year);
        if (x > w1)
        {
            var upperComparison = WageTaxClass56Step(w1, year) + (x - w1) * tariff.Zone4Rate;
            result = Math.Min(result, upperComparison);
        }
        return RoundMoney(result);
    }

    private static decimal WageTaxClass56Step(decimal taxableIncome, TaxYearParameters year)
    {
        var tariff = year.IncomeTax;
        var difference = (tariff.Tax(taxableIncome * 1.25m) - tariff.Tax(taxableIncome * 0.75m)) * 2m;
        return Math.Max(difference, taxableIncome * tariff.Zone2EntryRate / 10_000m);
    }

    private static decimal WageTaxProvisionAllowance(decimal annualGross, CompensationProfileInput input, TaxYearParameters year)
    {
        var pensionBase = Math.Min(Math.Max(0m, annualGross), year.PensionCeilingAnnual(input.StateCode));
        var pension = input.PensionInsuranceEnabled == false
            ? 0m
            : pensionBase * year.PensionEmployeeRate * year.PensionProvisionPhaseIn;
        var healthCareBase = Math.Min(Math.Max(0m, annualGross), year.HealthCareCeilingAnnual);
        // §39b PAP Vorsorgepauschale uses the reduced statutory-health employee rate (half of 14.0%),
        // plus the employee's share of the fund-specific additional contribution.
        var healthRate = year.HealthReducedEmployeeRate + AdditionalHealthEmployeeRate(input, year);
        var careRate = CareEmployeeRate(input, year);
        return pension + healthCareBase * (healthRate + careRate);
    }

    /// <param name="monthsPaid">Paid months of the year (12 = full year). The Beitragsbemessungsgrenze is a
    /// monthly ceiling in payroll, so a partial year is capped at its proportional share of the annual one.</param>
    private static SocialInsuranceBreakdown SocialInsurance(
        decimal annualBase, CompensationProfileInput input, TaxYearParameters year, decimal monthsPaid)
    {
        var ceilingShare = Math.Clamp(monthsPaid, 0m, 12m) / 12m;
        var rvAvBase = Math.Min(Math.Max(0m, annualBase), year.PensionCeilingAnnual(input.StateCode) * ceilingShare);
        var kvPvBase = Math.Min(Math.Max(0m, annualBase), year.HealthCareCeilingAnnual * ceilingShare);
        var employeeAdditional = AdditionalHealthEmployeeRate(input, year);
        var employerAdditional = AdditionalHealthRate(input, year) - employeeAdditional;

        var pension = input.PensionInsuranceEnabled == false ? 0m : rvAvBase * year.PensionEmployeeRate;
        var unemployment = input.UnemploymentInsuranceEnabled == false ? 0m : rvAvBase * year.UnemploymentEmployeeRate;
        var health = kvPvBase * (year.HealthEmployeeBaseRate + employeeAdditional);
        var care = kvPvBase * CareEmployeeRate(input, year);

        var saxony = input.StateCode.Trim().Equals("SN", StringComparison.OrdinalIgnoreCase);
        var employerPension = input.PensionInsuranceEnabled == false ? 0m : rvAvBase * year.PensionEmployeeRate;
        var employerUnemployment = input.UnemploymentInsuranceEnabled == false ? 0m : rvAvBase * year.UnemploymentEmployeeRate;
        var employerHealth = kvPvBase * (year.HealthEmployeeBaseRate + employerAdditional);
        var employerCare = kvPvBase * (saxony ? year.CareHalfRate - year.CareSaxonyEmployeeShift : year.CareHalfRate);

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

    /// <summary>
    /// The employee's age for the calculation. When a birth date and a tax year are known, this is the age
    /// reached during that year, so historical snapshots (and e.g. the childless care-insurance surcharge that
    /// starts at 23) use the age at the time rather than today's age. Falls back to the explicit Age input.
    /// </summary>
    private static int? EffectiveAge(CompensationProfileInput input)
    {
        if (input.BirthDate is { } birth)
        {
            var referenceYear = input.TaxYear ?? DateTimeOffset.UtcNow.Year;
            return Math.Clamp(referenceYear - birth.Year, 0, 120);
        }
        return input.Age;
    }

    /// <summary>
    /// The fund-specific additional health contribution (Zusatzbeitrag) as a rate. When the profile does
    /// not name one, the tax year's statutory average applies, so a historical snapshot uses that year's
    /// rate instead of today's.
    /// </summary>
    private static decimal AdditionalHealthRate(CompensationProfileInput input, TaxYearParameters year) =>
        input.HealthInsuranceAdditionalRatePercent is { } percent
            ? Math.Clamp(percent, 0m, 10m) / 100m
            : year.AverageHealthAdditionalRate;

    /// <summary>
    /// The employee's part of the Zusatzbeitrag. Shared 50/50 with the employer since the
    /// GKV-Versichertenentlastungsgesetz took effect in 2019; before that the employee carried it alone.
    /// </summary>
    private static decimal AdditionalHealthEmployeeRate(CompensationProfileInput input, TaxYearParameters year) =>
        AdditionalHealthRate(input, year) * year.HealthAdditionalRateEmployeeShare;

    private static decimal CareEmployeeRate(CompensationProfileInput input, TaxYearParameters year)
    {
        var saxony = input.StateCode.Trim().Equals("SN", StringComparison.OrdinalIgnoreCase);
        var rate = saxony ? year.CareHalfRate + year.CareSaxonyEmployeeShift : year.CareHalfRate;
        var age = EffectiveAge(input) ?? 23;
        if (age >= 23 && input.ChildrenUnder25 <= 0 && input.ChildlessCareSurcharge)
            rate += year.CareChildlessSurcharge;
        else if (input.ChildrenUnder25 > 1 && year.CareChildDiscountPerChild > 0m)
            rate = Math.Max(0m, rate - year.CareChildDiscountPerChild
                * (Math.Min(year.CareChildDiscountMaxChildren, input.ChildrenUnder25) - 1));
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

    private static CompensationAssumptions Assumptions(TaxYearParameters year) => new(
        year.Year,
        "tax-class-aware annualized wage-tax planning estimate",
        $"BMF PAP {year.Year} / EStG §32a {year.Year}; tax classes 1-6, tax-class-IV factor and statutory-insurance Vorsorgepauschale modeled locally",
        $"BMAS/BMG/BA {year.Year} contribution rates and ceilings ({year.SourceNote})",
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
        if (input.TaxYear is < 1900 or > 2200) throw new ArgumentOutOfRangeException(nameof(input.TaxYear));
        if (input.BirthDate is { Year: < 1900 or > 2200 }) throw new ArgumentOutOfRangeException(nameof(input.BirthDate));
        if (input.EmploymentStart is { Year: < 1900 or > 2200 }) throw new ArgumentOutOfRangeException(nameof(input.EmploymentStart));
        if (input.EmploymentEnd is { Year: < 1900 or > 2200 }) throw new ArgumentOutOfRangeException(nameof(input.EmploymentEnd));
        if (input.EmploymentStart is { } employmentStart && input.EmploymentEnd is { } employmentEnd
            && employmentEnd < employmentStart)
            throw new ArgumentException(
                "EmploymentEnd must not be before EmploymentStart.", nameof(input.EmploymentEnd));
        if (input.OneOffPayments is { } oneOff && oneOff.Any(p => p.Amount < 0m))
            throw new ArgumentOutOfRangeException(nameof(input.OneOffPayments));
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
        decimal CashGrossAnnual,
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
