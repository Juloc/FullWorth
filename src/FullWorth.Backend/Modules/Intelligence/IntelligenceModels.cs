using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Intelligence;

public static class IntelligenceProviders
{
    public const string OpenAi = "openai";
    public const string OpenAiCompatible = "openai-compatible";
    public const string Codex = "codex";
}

public static class IntelligenceSuggestionStatuses
{
    public const string Pending = "pending";
    public const string Accepted = "accepted";
    public const string Rejected = "rejected";
    public const string Expired = "expired";
    public const string Superseded = "superseded";
}

public static class IntelligenceJobStatuses
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Deferred = "deferred";
}

public static class AiRunStatuses
{
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Deferred = "deferred";
}

public sealed class AiCredential
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? OwnerUserId { get; set; }
    public string Provider { get; set; } = IntelligenceProviders.OpenAi;
    public string Name { get; set; } = "OpenAI";
    public string ProtectedSecret { get; set; } = string.Empty;
    public string SecretFingerprint { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastTestedAt { get; set; }
    public bool? LastTestSucceeded { get; set; }
}

public sealed class AiInstanceSettings
{
    public const string InstanceScopeKey = "instance";

    public Guid Id { get; set; } = Guid.NewGuid();
    public string ScopeKey { get; set; } = InstanceScopeKey;
    public bool Enabled { get; set; }
    public string Provider { get; set; } = IntelligenceProviders.OpenAi;
    public Guid? CredentialId { get; set; }
    public bool AllowUserCredentials { get; set; }
    public string DefaultTextModel { get; set; } = "gpt-5.6";
    public string DefaultVisionModel { get; set; } = "gpt-5.6";
    public decimal? DailyBudgetEur { get; set; }
    public decimal? MonthlyBudgetEur { get; set; }
    public bool DailyScanEnabled { get; set; }
    public bool WeeklyDeepScanEnabled { get; set; }
    public bool MonthlyReviewEnabled { get; set; }
    public bool ReceiptAiEnabled { get; set; }
    public bool MerchantAiEnabled { get; set; }
    public bool CategoryAiEnabled { get; set; }
    public bool ContractAiEnabled { get; set; }
    public bool ProductAiEnabled { get; set; }
    public bool LogoResearchEnabled { get; set; }
    public bool InternetResearchEnabled { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class AiUserSettings
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public bool Enabled { get; set; } = true;
    public Guid? CredentialId { get; set; }
    public string? TextModel { get; set; }
    public string? VisionModel { get; set; }
    public bool? ReceiptAiEnabled { get; set; }
    public bool? MerchantAiEnabled { get; set; }
    public bool? CategoryAiEnabled { get; set; }
    public bool? ContractAiEnabled { get; set; }
    public bool? ProductAiEnabled { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class AiRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? UserId { get; set; }
    public Guid? FullWorthSpaceId { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string Capability { get; set; } = string.Empty;
    public string JobType { get; set; } = string.Empty;
    public string Status { get; set; } = AiRunStatuses.Running;
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public int InputItemCount { get; set; }
    public int OutputItemCount { get; set; }
    public long? InputTokens { get; set; }
    public long? OutputTokens { get; set; }
    public decimal? EstimatedCostEur { get; set; }
    public decimal? ActualCostEur { get; set; }
    public string CorrelationId { get; set; } = Guid.NewGuid().ToString("N");
    public string? ErrorSummary { get; set; }
}

public sealed class AiRunItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RunId { get; set; }
    public string SubjectType { get; set; } = string.Empty;
    public string SubjectId { get; set; } = string.Empty;
    public string InputSummaryJson { get; set; } = "{}";
    public string OutputSummaryJson { get; set; } = "{}";
    public string Status { get; set; } = AiRunStatuses.Running;
    public string? ErrorCode { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class IntelligenceSuggestion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? FullWorthSpaceId { get; set; }
    public Guid? UserId { get; set; }
    public string Type { get; set; } = string.Empty;
    public string SubjectType { get; set; } = string.Empty;
    public string SubjectId { get; set; } = string.Empty;
    public string SemanticKey { get; set; } = string.Empty;
    public string ProposedPayloadJson { get; set; } = "{}";
    public string EvidenceJson { get; set; } = "{}";
    public string Provider { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public decimal Confidence { get; set; }
    public string Status { get; set; } = IntelligenceSuggestionStatuses.Pending;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ReviewedAt { get; set; }
    public Guid? ReviewedByUserId { get; set; }
    public Guid? RunId { get; set; }
}

public sealed class IntelligenceFeedbackEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FullWorthSpaceId { get; set; }
    public Guid UserId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string SubjectType { get; set; } = string.Empty;
    public string SubjectId { get; set; } = string.Empty;
    public string SubjectFingerprint { get; set; } = string.Empty;
    public string OldValueJson { get; set; } = "{}";
    public string NewValueJson { get; set; } = "{}";
    public string Source { get; set; } = "user";
    public bool CloudEligible { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class IntelligenceJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Type { get; set; } = string.Empty;
    public string ScopeKey { get; set; } = "instance";
    public DateTimeOffset ScheduledFor { get; set; } = DateTimeOffset.UtcNow;
    public string IdempotencyKey { get; set; } = string.Empty;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string Status { get; set; } = IntelligenceJobStatuses.Queued;
    public int RetryCount { get; set; }
    public DateTimeOffset? NextRetryAt { get; set; }
    public string? ErrorCode { get; set; }
    public string PayloadJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public static class IntelligenceModelConfiguration
{
    public static void Configure(ModelBuilder b)
    {
        b.Entity<AiCredential>(e =>
        {
            e.HasIndex(x => new { x.OwnerUserId, x.Provider, x.Name });
            e.Property(x => x.Provider).HasMaxLength(40);
            e.Property(x => x.Name).HasMaxLength(120);
            e.Property(x => x.SecretFingerprint).HasMaxLength(80);
            e.Property(x => x.ProtectedSecret).HasColumnType("text");
        });

        b.Entity<AiInstanceSettings>(e =>
        {
            e.HasIndex(x => x.ScopeKey).IsUnique();
            e.Property(x => x.ScopeKey).HasMaxLength(40);
            e.Property(x => x.Provider).HasMaxLength(40);
            e.Property(x => x.DefaultTextModel).HasMaxLength(120);
            e.Property(x => x.DefaultVisionModel).HasMaxLength(120);
            e.Property(x => x.DailyBudgetEur).HasPrecision(18, 4);
            e.Property(x => x.MonthlyBudgetEur).HasPrecision(18, 4);
        });

        b.Entity<AiUserSettings>(e =>
        {
            e.HasIndex(x => x.UserId).IsUnique();
            e.Property(x => x.TextModel).HasMaxLength(120);
            e.Property(x => x.VisionModel).HasMaxLength(120);
        });

        b.Entity<AiRun>(e =>
        {
            e.HasIndex(x => x.StartedAt);
            e.HasIndex(x => new { x.UserId, x.StartedAt });
            e.Property(x => x.Provider).HasMaxLength(40);
            e.Property(x => x.Model).HasMaxLength(120);
            e.Property(x => x.Capability).HasMaxLength(80);
            e.Property(x => x.JobType).HasMaxLength(80);
            e.Property(x => x.Status).HasMaxLength(32);
            e.Property(x => x.CorrelationId).HasMaxLength(80);
            e.Property(x => x.EstimatedCostEur).HasPrecision(18, 6);
            e.Property(x => x.ActualCostEur).HasPrecision(18, 6);
            e.Property(x => x.ErrorSummary).HasMaxLength(2000);
        });

        b.Entity<AiRunItem>(e =>
        {
            e.HasIndex(x => x.RunId);
            e.Property(x => x.SubjectType).HasMaxLength(80);
            e.Property(x => x.SubjectId).HasMaxLength(160);
            e.Property(x => x.Status).HasMaxLength(32);
            e.Property(x => x.ErrorCode).HasMaxLength(120);
            e.Property(x => x.InputSummaryJson).HasColumnType("jsonb");
            e.Property(x => x.OutputSummaryJson).HasColumnType("jsonb");
            e.HasOne<AiRun>().WithMany().HasForeignKey(x => x.RunId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<IntelligenceSuggestion>(e =>
        {
            e.HasIndex(x => new { x.Status, x.CreatedAt });
            e.HasIndex(x => new { x.FullWorthSpaceId, x.Status });
            e.HasIndex(x => new { x.SubjectType, x.SubjectId, x.SemanticKey, x.Status });
            e.Property(x => x.Type).HasMaxLength(80);
            e.Property(x => x.SubjectType).HasMaxLength(80);
            e.Property(x => x.SubjectId).HasMaxLength(160);
            e.Property(x => x.SemanticKey).HasMaxLength(240);
            e.Property(x => x.Provider).HasMaxLength(40);
            e.Property(x => x.Model).HasMaxLength(120);
            e.Property(x => x.Status).HasMaxLength(32);
            e.Property(x => x.Confidence).HasPrecision(6, 5);
            e.Property(x => x.ProposedPayloadJson).HasColumnType("jsonb");
            e.Property(x => x.EvidenceJson).HasColumnType("jsonb");
        });

        b.Entity<IntelligenceFeedbackEvent>(e =>
        {
            e.HasIndex(x => new { x.FullWorthSpaceId, x.CreatedAt });
            e.HasIndex(x => new { x.CloudEligible, x.CreatedAt });
            e.Property(x => x.EventType).HasMaxLength(80);
            e.Property(x => x.SubjectType).HasMaxLength(80);
            e.Property(x => x.SubjectId).HasMaxLength(160);
            e.Property(x => x.SubjectFingerprint).HasMaxLength(160);
            e.Property(x => x.Source).HasMaxLength(40);
            e.Property(x => x.OldValueJson).HasColumnType("jsonb");
            e.Property(x => x.NewValueJson).HasColumnType("jsonb");
        });

        b.Entity<IntelligenceJob>(e =>
        {
            e.HasIndex(x => x.IdempotencyKey).IsUnique();
            e.HasIndex(x => new { x.Status, x.ScheduledFor });
            e.Property(x => x.Type).HasMaxLength(80);
            e.Property(x => x.ScopeKey).HasMaxLength(160);
            e.Property(x => x.IdempotencyKey).HasMaxLength(240);
            e.Property(x => x.Status).HasMaxLength(32);
            e.Property(x => x.ErrorCode).HasMaxLength(120);
            e.Property(x => x.PayloadJson).HasColumnType("jsonb");
        });
    }
}
