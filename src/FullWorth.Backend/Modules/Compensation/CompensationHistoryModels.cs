namespace FullWorth.Backend.Modules.Compensation;

public sealed record CompensationHistoryWrite(
    DateOnly EffectiveDate,
    string EventType,
    string Title,
    string? Note,
    CompensationProfileInput Profile);

public sealed record CompensationHistoryDelta(
    decimal GrossAnnual,
    decimal CashNetAnnual,
    decimal EmployerCostAnnual,
    decimal FullWorthValueAnnual,
    decimal EffectiveHourlyValue,
    decimal TaxesAnnual,
    decimal SocialInsuranceAnnual);

public sealed record CompensationHistoryEntry(
    Guid Id,
    Guid FullWorthSpaceId,
    DateOnly EffectiveDate,
    int Sequence,
    string EventType,
    string Title,
    string? Note,
    IReadOnlyList<string> ChangedFields,
    CompensationProfileInput ResolvedProfile,
    CompensationCalculationResult Calculation,
    CompensationHistoryDelta? DeltaFromPrevious,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record CompensationTimelinePoint(
    DateOnly Date,
    decimal ContractualGrossAnnual,
    decimal EstimatedCashNetAnnual,
    decimal FullWorthCompensationValueAnnual,
    decimal EmployerTotalCostAnnual,
    decimal EffectiveNetValuePerWorkingHour,
    decimal MarginalNetFromNext100Gross,
    decimal TaxesAnnual,
    decimal SocialInsuranceAnnual,
    decimal PersonalBenefitsValueAnnual,
    decimal CompanyCarNetCashImpactAnnual,
    decimal PurchasingPowerMaintenanceGrossAnnual,
    decimal NominalChangeFromBaselinePercent,
    decimal InflationFromBaselinePercent,
    decimal RealChangeFromBaselinePercent,
    // Separate income track: "sonstige regelmäßige Einkünfte" (e.g. Halbwaisenrente). Deliberately NOT
    // part of any employer figure above — those come straight from the untouched salary calculator.
    decimal OtherRegularIncomeAnnual,
    decimal OtherRegularIncomeCountedAnnual,
    // Net salary plus only the other income flagged as "counts toward personally available income".
    decimal PersonallyAvailableTotalIncomeAnnual,
    Guid? SourceEventId,
    string? SourceEventTitle);

public sealed record CompensationTimelineSummary(
    DateOnly BaselineDate,
    DateOnly CurrentDate,
    decimal BaselineGrossAnnual,
    decimal CurrentGrossAnnual,
    decimal CurrentNetAnnual,
    decimal CurrentFullWorthValueAnnual,
    decimal PurchasingPowerMaintenanceGrossAnnual,
    decimal NominalChangePercent,
    decimal InflationPercent,
    decimal RealChangePercent,
    decimal CurrentOtherRegularIncomeAnnual,
    decimal CurrentPersonallyAvailableTotalIncomeAnnual);

public sealed record CompensationTimelineResult(
    DateOnly From,
    DateOnly To,
    IReadOnlyList<CompensationHistoryEntry> Events,
    IReadOnlyList<CompensationTimelinePoint> Points,
    CompensationTimelineSummary? Summary,
    // The raw other-income records behind the OtherRegularIncome* series, so the chart can label the
    // track and draw per-type sub-series without a second round trip.
    IReadOnlyList<CompensationOtherIncomeEntry> OtherIncome);
