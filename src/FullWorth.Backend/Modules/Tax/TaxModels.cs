namespace FullWorth.Backend.Modules.Tax;

public static class TaxCandidateStatuses
{
    public const string Detected = "detected";
    public const string NeedsReview = "needs_review";
    public const string Confirmed = "confirmed";
    public const string Rejected = "rejected";
    public const string Ignored = "ignored";
    public const string NeedsDocument = "needs_document";
    public const string Incomplete = "incomplete";

    public static bool IsValid(string? value) => value is
        Detected or NeedsReview or Confirmed or Rejected or Ignored or NeedsDocument or Incomplete;
}

public static class TaxDetectionSources
{
    public const string Rule = "rule";
    public const string MerchantMapping = "merchant_mapping";
    public const string CategoryMapping = "category_mapping";
    public const string Keyword = "keyword";
    public const string PurchaseItem = "purchase_item";
    public const string Document = "document";
    public const string RecurringPattern = "recurring_pattern";
    public const string UserMapping = "user_mapping";
    public const string Ai = "ai";
    public const string Manual = "manual";
    public const string Combined = "combined";
}

public static class TaxSourceTypes
{
    public const string Transaction = "transaction";
    public const string Purchase = "purchase";
    public const string PurchaseItem = "purchase_item";
    public const string PurchaseDocument = "purchase_document";
    public const string Contract = "contract";
    public const string Manual = "manual";
}

public sealed class TaxSettings
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FullWorthSpaceId { get; set; }
    public bool Enabled { get; set; } = true;
    public string CountryCode { get; set; } = "DE";
    public int DefaultTaxYear { get; set; } = DateTime.UtcNow.Year;
    public bool AutomaticAnalysisEnabled { get; set; } = true;
    public bool AiAnalysisEnabled { get; set; }
    public bool AnalyzeTransactions { get; set; } = true;
    public bool AnalyzePurchases { get; set; } = true;
    public bool AnalyzeDocuments { get; set; } = true;
    public bool ShowTaxNotifications { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class TaxProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FullWorthSpaceId { get; set; }
    public Guid? UserId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string CountryCode { get; set; } = "DE";
    public bool AssistantEnabled { get; set; } = true;
    public bool Active { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class TaxCategory
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string CountryCode { get; set; } = "DE";
    public string Code { get; set; } = string.Empty;
    public string? ParentCode { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int ValidFromTaxYear { get; set; }
    public int? ValidUntilTaxYear { get; set; }
    public bool Active { get; set; } = true;
}

public sealed class TaxCandidate
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FullWorthSpaceId { get; set; }
    public Guid TaxProfileId { get; set; }
    public int TaxYear { get; set; }
    public string Status { get; set; } = TaxCandidateStatuses.NeedsReview;
    public Guid? TaxCategoryId { get; set; }
    public decimal GrossAmount { get; set; }
    public decimal EligibleAmount { get; set; }
    public decimal EligiblePercentage { get; set; } = 100m;
    public string Currency { get; set; } = "EUR";
    public decimal Confidence { get; set; }
    public string DetectionSource { get; set; } = TaxDetectionSources.Rule;
    public string ReasonCode { get; set; } = string.Empty;
    public string Explanation { get; set; } = string.Empty;
    public string CountryCode { get; set; } = "DE";
    public string RuleVersion { get; set; } = string.Empty;
    public string SourceFingerprint { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ReviewedAt { get; set; }
    public Guid? ReviewedByUserId { get; set; }
}

public sealed class TaxCandidateSource
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TaxCandidateId { get; set; }
    public string SourceType { get; set; } = string.Empty;
    public Guid SourceId { get; set; }
    public bool IsPrimary { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class TaxRuleDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string CountryCode { get; set; } = "DE";
    public int TaxYearFrom { get; set; }
    public int? TaxYearTo { get; set; }
    public string RuleCode { get; set; } = string.Empty;
    public int Priority { get; set; } = 100;
    public bool Enabled { get; set; } = true;
    public string RuleType { get; set; } = string.Empty;
    public string ConfigurationJson { get; set; } = "{}";
    public string Version { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class TaxUserMapping
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FullWorthSpaceId { get; set; }
    public Guid TaxProfileId { get; set; }
    public string MatchType { get; set; } = string.Empty;
    public string MatchValue { get; set; } = string.Empty;
    public Guid? TaxCategoryId { get; set; }
    public decimal EligiblePercentage { get; set; } = 100m;
    public string Action { get; set; } = "suggest";
    public Guid? CreatedFromCandidateId { get; set; }
    public bool Active { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class TaxFeedback
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FullWorthSpaceId { get; set; }
    public Guid TaxCandidateId { get; set; }
    public Guid UserId { get; set; }
    public string OriginalStatus { get; set; } = string.Empty;
    public Guid? OriginalCategoryId { get; set; }
    public string Decision { get; set; } = string.Empty;
    public Guid? NewCategoryId { get; set; }
    public decimal? NewEligiblePercentage { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class TaxAnalysisRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FullWorthSpaceId { get; set; }
    public Guid TaxProfileId { get; set; }
    public int TaxYear { get; set; }
    public string Trigger { get; set; } = "manual";
    public string RuleVersion { get; set; } = string.Empty;
    public int SourcesAnalyzed { get; set; }
    public int CandidatesCreated { get; set; }
    public int CandidatesChanged { get; set; }
    public string Status { get; set; } = "running";
    public string? ErrorCode { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }
}
