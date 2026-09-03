namespace FullWorth.Backend.Modules.Tax;

public sealed record TaxSettingsUpdateRequest(
    bool Enabled,
    string CountryCode,
    int DefaultTaxYear,
    bool AutomaticAnalysisEnabled,
    bool AiAnalysisEnabled,
    bool AnalyzeTransactions,
    bool AnalyzePurchases,
    bool AnalyzeDocuments,
    bool ShowTaxNotifications);

public sealed record TaxProfileSettingsUpdateRequest(bool AssistantEnabled);
public sealed record TaxProfileSettingsView(bool AssistantEnabled);

public sealed record TaxCandidateUpdateRequest(
    Guid? TaxCategoryId,
    decimal? EligiblePercentage,
    string? Status);

public sealed record TaxCandidateView(
    Guid Id,
    int TaxYear,
    string Status,
    Guid? TaxCategoryId,
    string? TaxCategoryCode,
    string? TaxCategoryName,
    decimal GrossAmount,
    decimal EligibleAmount,
    decimal EligiblePercentage,
    string Currency,
    decimal Confidence,
    string DetectionSource,
    string ReasonCode,
    string Explanation,
    string? SourceType,
    Guid? SourceId,
    string SourceTitle,
    DateOnly? SourceDate,
    bool HasDocument,
    DateTimeOffset UpdatedAt);

public sealed record TaxExportRow(
    DateOnly? Date,
    string Source,
    string Description,
    string TaxCategory,
    decimal GrossAmount,
    decimal EligibleAmount,
    decimal EligiblePercentage,
    string Currency,
    bool HasDocument,
    string SourceType,
    string Status);

public sealed record TaxYearExport(
    int TaxYear,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<TaxExportRow> Rows);

public sealed record TaxAnalysisRequest(int? TaxYear);

public sealed record TaxSummaryView(
    int TaxYear,
    decimal SuggestedAmount,
    decimal ConfirmedAmount,
    int NeedsReviewCount,
    int NeedsDocumentCount,
    int ConfirmedCount);

public sealed record TaxAnalysisResult(
    bool Enabled,
    int TaxYear,
    int SourcesAnalyzed,
    int CandidatesCreated,
    int CandidatesChanged,
    string RuleVersion);
