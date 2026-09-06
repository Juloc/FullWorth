namespace FullWorth.Backend.Modules.Compensation;

public sealed record CompanyCarInput(
    bool Enabled = false,
    decimal ListPrice = 0m,
    decimal TaxableListPriceFactor = 1m,
    string VehicleType = "manual",
    DateOnly? AcquisitionDate = null,
    decimal ElectricRangeKm = 0m,
    decimal Co2GramsPerKm = 0m,
    decimal OneWayCommuteKm = 0m,
    string CommuteMethod = "monthly",
    int CommuteDaysPerMonth = 0,
    decimal EmployeeContributionMonthly = 0m,
    decimal EmployerCostMonthly = 0m,
    decimal PrivateAlternativeCostMonthly = 0m);

public sealed record OccupationalPensionInput(
    decimal EmployeeContributionMonthly = 0m,
    decimal EmployerContributionMonthly = 0m,
    int ProjectionYears = 30,
    decimal ExpectedAnnualReturnPercent = 3m);

public sealed record CompensationBenefitInput(
    string Name,
    decimal EmployerCostMonthly = 0m,
    decimal PersonalValueMonthly = 0m,
    decimal TaxableBenefitMonthly = 0m,
    decimal EmployeeCostMonthly = 0m);

public sealed record CompensationProfileInput(
    string Name,
    decimal AnnualGross,
    decimal AnnualBonus = 0m,
    string GrossInputMode = "annual",
    int SalaryPaymentsPerYear = 12,
    int TaxClass = 1,
    decimal TaxClass4Factor = 1m,
    string StateCode = "BW",
    bool ChurchTax = false,
    int ChildrenUnder25 = 0,
    bool ChildlessCareSurcharge = true,
    decimal HealthInsuranceAdditionalRatePercent = 2.9m,
    decimal WeeklyHours = 40m,
    int VacationDays = 30,
    decimal SpouseAnnualTaxableIncome = 0m,
    CompanyCarInput? CompanyCar = null,
    OccupationalPensionInput? OccupationalPension = null,
    IReadOnlyList<CompensationBenefitInput>? Benefits = null);

public sealed record TaxBreakdown(
    decimal EstimatedIncomeTaxAnnual,
    decimal EstimatedSolidaritySurchargeAnnual,
    decimal EstimatedChurchTaxAnnual,
    decimal EstimatedTaxableIncomeAnnual);

public sealed record SocialInsuranceBreakdown(
    decimal PensionAnnual,
    decimal UnemploymentAnnual,
    decimal HealthAnnual,
    decimal CareAnnual,
    decimal TotalAnnual,
    decimal EmployerPensionAnnual,
    decimal EmployerUnemploymentAnnual,
    decimal EmployerHealthAnnual,
    decimal EmployerCareAnnual,
    decimal EmployerTotalAnnual);

public sealed record CompanyCarAnalysis(
    decimal TaxableBenefitMonthly,
    decimal TaxableBenefitAnnual,
    decimal EmployeeContributionAnnual,
    decimal EmployerCostAnnual,
    decimal PrivateAlternativeValueAnnual,
    decimal EstimatedNetCashImpactAnnual,
    decimal EstimatedEffectivePersonalValueAnnual);

public sealed record OccupationalPensionAnalysis(
    decimal EmployeeContributionAnnual,
    decimal EmployerContributionAnnual,
    decimal TaxExemptEmployeeContributionAnnual,
    decimal SocialInsuranceExemptEmployeeContributionAnnual,
    decimal EstimatedCurrentNetSacrificeAnnual,
    decimal TotalInvestedAnnual,
    decimal BenefitEfficiency,
    decimal ProjectedValue);

public sealed record BenefitAnalysis(
    string Name,
    decimal EmployerCostAnnual,
    decimal PersonalValueAnnual,
    decimal TaxableBenefitAnnual,
    decimal EmployeeCostAnnual);

public sealed record CompensationCalculationResult(
    string Name,
    decimal ContractualGrossAnnual,
    decimal BonusAnnual,
    decimal CashGrossAnnual,
    decimal EstimatedCashNetAnnual,
    decimal EstimatedCashNetMonthly,
    decimal EstimatedTotalDeductionsAnnual,
    decimal EstimatedNetRatioPercent,
    decimal EmployerTotalCostAnnual,
    decimal PersonalBenefitsValueAnnual,
    decimal FullWorthCompensationValueAnnual,
    decimal EffectiveNetValuePerWorkingHour,
    decimal MarginalNetFromNext100Gross,
    TaxBreakdown Taxes,
    SocialInsuranceBreakdown SocialInsurance,
    CompanyCarAnalysis CompanyCar,
    OccupationalPensionAnalysis OccupationalPension,
    IReadOnlyList<BenefitAnalysis> Benefits,
    CompensationAssumptions Assumptions);

public sealed record CompensationAssumptions(
    int TaxYear,
    string CalculationKind,
    string TaxSource,
    string SocialInsuranceSource,
    string InflationSource,
    string DataAsOf,
    string Disclaimer);

public sealed record CompensationComparisonRequest(
    CompensationProfileInput Left,
    CompensationProfileInput Right);

public sealed record CompensationComparisonResult(
    CompensationCalculationResult Left,
    CompensationCalculationResult Right,
    decimal CashNetDeltaAnnual,
    decimal EmployerCostDeltaAnnual,
    decimal FullWorthValueDeltaAnnual,
    decimal EffectiveHourlyValueDelta);

public sealed record SalaryNegotiationRequest(
    decimal PreviousAnnualGross,
    DateOnly PreviousDate,
    decimal CurrentAnnualGross,
    decimal DesiredAnnualGross,
    decimal AdditionalRealAdjustmentPercent = 0m,
    DateOnly? ComparisonDate = null);

public sealed record SalaryNegotiationResult(
    decimal PreviousAnnualGross,
    DateOnly PreviousDate,
    decimal CurrentAnnualGross,
    decimal DesiredAnnualGross,
    DateOnly ComparisonDate,
    decimal PreviousCpi,
    decimal CurrentCpi,
    decimal CumulativeInflationPercent,
    decimal PurchasingPowerMaintenanceSalary,
    decimal CurrentNominalChangePercent,
    decimal CurrentRealChangePercent,
    decimal DesiredNominalChangePercent,
    decimal DesiredRealChangePercent,
    decimal DesiredAmountAboveInflationCompensation,
    decimal SuggestedReferenceSalary,
    string InflationSource,
    string DataAsOf);

public sealed record InflationPoint(DateOnly Date, decimal Index, bool IsFinal);

public sealed record InflationMetadata(
    string Source,
    string Base,
    string DataAsOf,
    IReadOnlyList<InflationPoint> Points);

public sealed record SavedCompensationProfile(
    Guid FullWorthSpaceId,
    CompensationProfileInput Profile,
    DateTimeOffset UpdatedAt);

public sealed record CompensationScenarioWrite(string Name, CompensationProfileInput Profile);

public sealed record CompensationScenarioView(
    Guid Id,
    Guid FullWorthSpaceId,
    string Name,
    CompensationProfileInput Profile,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
