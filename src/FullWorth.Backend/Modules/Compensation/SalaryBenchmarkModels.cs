namespace FullWorth.Backend.Modules.Compensation;

// Shape of salary-benchmarks-de.json (see the "method" block inside that file for how the
// numbers were produced). Every value in the dataset is an estimate; nothing is a measured value.

public sealed record SalaryBenchmarkSource(
    string Key,
    string Label,
    string? Url,
    string? Note);

public sealed record SalaryBenchmarkSourceSet(
    string Key,
    IReadOnlyList<string> Sources);

/// <summary>
/// Confidence marker. <paramref name="Derived"/> means the number was computed from another
/// number (year roll-forward, regional factor, spread) rather than taken from a published figure.
/// <paramref name="Measured"/> is <c>false</c> for every level in this dataset — there is no
/// microdata behind it — and the dataset guard tests assert that stays true.
/// </summary>
public sealed record SalaryBenchmarkQualityLevel(
    string Key,
    string Label,
    bool Derived,
    bool Measured,
    string Confidence,
    string Explanation);

public sealed record SalaryBenchmarkExperienceBand(
    string Key,
    string Label,
    int? MinYears,
    int? MaxYears,
    string? Note);

public sealed record SalaryBenchmarkSpreadGroup(
    string Key,
    decimal P25Factor,
    decimal P75Factor,
    string Quality,
    string Note);

public sealed record SalaryBenchmarkWagePoint(
    int Year,
    decimal Index,
    bool Projected,
    IReadOnlyList<string> Sources);

public sealed record SalaryBenchmarkState(
    string Key,
    string Name,
    decimal Factor,
    bool National,
    string Quality,
    IReadOnlyList<string> Sources);

public sealed record SalaryBenchmarkProfession(
    string Key,
    string Name,
    string Group,
    string SpreadGroup,
    decimal AnchorOverallMedian,
    string AnchorOverallQuality,
    IReadOnlyList<string> AnchorOverallSources,
    IReadOnlyList<string> ExperienceBands);

public sealed record SalaryBenchmarkNationalRow(
    string Profession,
    int Year,
    string Experience,
    decimal Median,
    decimal P25,
    decimal P75,
    string Quality,
    string QuartileQuality,
    string SourceSet);

public sealed record SalaryBenchmarkMethod(
    string Summary,
    IReadOnlyList<string> Steps,
    IReadOnlyList<string> Limitations,
    string Revision);

public sealed record SalaryBenchmarkFile(
    int SchemaVersion,
    string DatasetKey,
    string Currency,
    string Basis,
    string DataAsOf,
    int AnchorYear,
    IReadOnlyList<int> Years,
    IReadOnlyList<int> ProjectedYears,
    string NationalStateKey,
    string Disclaimer,
    SalaryBenchmarkMethod Method,
    IReadOnlyList<SalaryBenchmarkSource> Sources,
    IReadOnlyList<SalaryBenchmarkSourceSet> SourceSets,
    IReadOnlyList<SalaryBenchmarkQualityLevel> QualityLevels,
    IReadOnlyList<SalaryBenchmarkExperienceBand> ExperienceBands,
    IReadOnlyList<SalaryBenchmarkSpreadGroup> SpreadGroups,
    IReadOnlyList<SalaryBenchmarkWagePoint> WageIndex,
    IReadOnlyList<SalaryBenchmarkState> States,
    IReadOnlyList<SalaryBenchmarkProfession> Professions,
    IReadOnlyList<SalaryBenchmarkNationalRow> NationalRows);

// --- read API shapes ---

public sealed record SalaryBenchmarkQuery(
    string Profession,
    string? Bundesland,
    int? Year,
    string? Experience,
    decimal? AnnualGross);

public sealed record SalaryBenchmarkComparison(
    decimal AnnualGross,
    decimal DeltaToMedian,
    decimal PercentOfMedian,
    string Position,
    string PositionLabel);

public sealed record SalaryBenchmarkRecord(
    string ProfessionKey,
    string ProfessionName,
    string ProfessionGroup,
    string StateKey,
    string StateName,
    decimal StateFactor,
    bool StateIsNational,
    int Year,
    string ExperienceKey,
    string ExperienceLabel,
    decimal AnnualGrossMedian,
    decimal AnnualGrossP25,
    decimal AnnualGrossP75,
    string Currency,
    string Basis,
    string Quality,
    string QualityLabel,
    bool Derived,
    bool Measured,
    string Confidence,
    string QualityExplanation,
    string QuartileQuality,
    string QuartileQualityExplanation,
    IReadOnlyList<string> SourceKeys,
    string SourceLabel,
    IReadOnlyList<SalaryBenchmarkSource> Sources,
    string DataAsOf,
    string Disclaimer,
    bool YearIsProjected,
    bool StateFallbackApplied,
    bool YearFallbackApplied,
    bool ExperienceFallbackApplied,
    int RequestedYear,
    string? RequestedBundesland,
    string? RequestedExperience,
    IReadOnlyList<string> DerivationNotes,
    SalaryBenchmarkComparison? Comparison);

public sealed record SalaryBenchmarkSeriesPoint(
    int Year,
    decimal AnnualGrossMedian,
    decimal AnnualGrossP25,
    decimal AnnualGrossP75,
    string Quality,
    bool Derived,
    bool YearIsProjected);

public sealed record SalaryBenchmarkSeries(
    string ProfessionKey,
    string ProfessionName,
    string StateKey,
    string StateName,
    string ExperienceKey,
    string ExperienceLabel,
    string Currency,
    string DataAsOf,
    string Disclaimer,
    bool StateFallbackApplied,
    bool ExperienceFallbackApplied,
    IReadOnlyList<string> DerivationNotes,
    IReadOnlyList<SalaryBenchmarkSeriesPoint> Points);

public sealed record SalaryBenchmarkMetadata(
    int SchemaVersion,
    string DatasetKey,
    string Currency,
    string Basis,
    string DataAsOf,
    int AnchorYear,
    IReadOnlyList<int> Years,
    IReadOnlyList<int> ProjectedYears,
    string NationalStateKey,
    string Disclaimer,
    SalaryBenchmarkMethod Method,
    IReadOnlyList<SalaryBenchmarkSource> Sources,
    IReadOnlyList<SalaryBenchmarkQualityLevel> QualityLevels,
    IReadOnlyList<SalaryBenchmarkExperienceBand> ExperienceBands,
    IReadOnlyList<SalaryBenchmarkSpreadGroup> SpreadGroups,
    IReadOnlyList<SalaryBenchmarkWagePoint> WageIndex,
    IReadOnlyList<SalaryBenchmarkState> States,
    IReadOnlyList<SalaryBenchmarkProfession> Professions,
    int RecordCount);
