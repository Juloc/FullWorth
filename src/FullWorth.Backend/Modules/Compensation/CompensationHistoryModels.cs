namespace FullWorth.Backend.Modules.Compensation;

public sealed record CompensationHistoryWrite(
    DateOnly EffectiveDate,
    string EventType,
    string Title,
    string? Note,
    CompensationProfileInput Profile);

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
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record CompensationTimelinePoint(
    DateOnly Date,
    decimal ContractualGrossAnnual,
    decimal EstimatedCashNetAnnual,
    decimal FullWorthCompensationValueAnnual,
    decimal PurchasingPowerMaintenanceGrossAnnual,
    decimal NominalChangeFromBaselinePercent,
    decimal InflationFromBaselinePercent,
    decimal RealChangeFromBaselinePercent,
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
    decimal RealChangePercent);

public sealed record CompensationTimelineResult(
    DateOnly From,
    DateOnly To,
    IReadOnlyList<CompensationHistoryEntry> Events,
    IReadOnlyList<CompensationTimelinePoint> Points,
    CompensationTimelineSummary? Summary);
