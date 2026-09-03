using System.Text.Json.Serialization;

namespace FullWorth.Backend.Modules.Coach;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SpendingSentiment
{
    Negative = -1,
    Neutral = 0,
    Positive = 1
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CoachMessageRole
{
    User,
    Assistant
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CoachAnswerMode
{
    Deterministic,
    Ai
}

public sealed class SpendingReview
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FullWorthSpaceId { get; set; }
    public Guid UserId { get; set; }
    public Guid TransactionId { get; set; }
    public Guid? PurchaseId { get; set; }
    public SpendingSentiment Sentiment { get; set; }
    public string ReasonsJson { get; set; } = "[]";
    public string? Note { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class CoachConversation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FullWorthSpaceId { get; set; }
    public Guid UserId { get; set; }
    public string Title { get; set; } = "New conversation";
    public string? MascotId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ArchivedAt { get; set; }
}

public sealed class CoachMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ConversationId { get; set; }
    public CoachMessageRole Role { get; set; }
    public string Text { get; set; } = string.Empty;
    public CoachAnswerMode Mode { get; set; } = CoachAnswerMode.Deterministic;
    public string? FactsJson { get; set; }
    public string? Provider { get; set; }
    public string? Model { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed record UpsertSpendingReviewRequest(
    SpendingSentiment Sentiment,
    IReadOnlyList<string>? Reasons,
    string? Note,
    Guid? PurchaseId = null);

public sealed record SpendingReviewDto(
    Guid Id,
    Guid TransactionId,
    Guid? PurchaseId,
    SpendingSentiment Sentiment,
    IReadOnlyList<string> Reasons,
    string? Note,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record WorthItGroupDto(
    string Key,
    string Label,
    decimal TotalOutgoingAmount,
    decimal ReviewedAmount,
    decimal PositiveAmount,
    decimal NeutralAmount,
    decimal NegativeAmount,
    decimal ReviewCoverage,
    decimal? WorthItScore,
    int ReviewedTransactions);

public sealed record SpendingReasonAmountDto(string Reason, decimal Amount, int Count);

public sealed record SpendingReviewSummaryDto(
    DateOnly From,
    DateOnly To,
    string Currency,
    bool Incomplete,
    decimal TotalOutgoingAmount,
    decimal ReviewedOutgoingAmount,
    decimal ReviewCoverage,
    decimal PositiveAmount,
    decimal NeutralAmount,
    decimal NegativeAmount,
    decimal? WorthItScore,
    int ReviewedTransactions,
    IReadOnlyList<WorthItGroupDto> Categories,
    IReadOnlyList<WorthItGroupDto> Merchants,
    IReadOnlyList<WorthItGroupDto> HighSpendPositive,
    IReadOnlyList<WorthItGroupDto> NegativeOpportunities,
    IReadOnlyList<SpendingReasonAmountDto> PositiveReasons,
    IReadOnlyList<SpendingReasonAmountDto> NegativeReasons);

public sealed record CoachFact(string Id, string Label, string Value);

public sealed record CoachCategoryFact(
    Guid? CategoryId,
    string Name,
    decimal Amount,
    decimal PreviousAmount,
    decimal Delta,
    decimal Share,
    decimal ReviewCoverage,
    decimal? WorthItScore,
    decimal NegativeReviewedAmount,
    decimal PositiveReviewedAmount)
{
    public decimal AvoidableNegativeReviewedAmount { get; init; }
    public decimal BudgetOverage { get; init; }
}

public sealed record CoachMerchantFact(
    string Name,
    decimal Amount,
    decimal PreviousAmount,
    decimal Delta,
    decimal ReviewCoverage,
    decimal? WorthItScore,
    decimal NegativeReviewedAmount,
    decimal PositiveReviewedAmount);

public sealed record CoachBudgetFact(
    Guid BudgetId,
    string Name,
    Guid? CategoryId,
    string Currency,
    decimal Target,
    decimal Spent,
    decimal Remaining,
    decimal PercentUsed,
    decimal ProjectedEndSpend,
    decimal ProjectedOverUnder,
    bool PartialAccess)
{
    public decimal Overage => Math.Max(0m, -Remaining);
}

public sealed record CoachReviewExample(
    Guid TransactionId,
    SpendingSentiment Sentiment,
    string Label,
    decimal Amount,
    IReadOnlyList<string> Reasons);

public sealed record CoachTargetScenario(
    decimal Target,
    DateOnly? EstimatedDate,
    int? Months,
    decimal CurrentNetWorth,
    decimal AssumedMonthlySavings,
    decimal? AssumedAnnualReturn);

public sealed record CoachContext(
    DateOnly From,
    DateOnly To,
    DateOnly ComparisonFrom,
    DateOnly ComparisonTo,
    string Currency,
    bool Incomplete,
    decimal Income,
    decimal Outgoing,
    decimal NetCashFlow,
    decimal PreviousIncome,
    decimal PreviousOutgoing,
    decimal PreviousNetCashFlow,
    decimal? CurrentNetWorth,
    decimal? AverageMonthlySavings,
    IReadOnlyList<CoachCategoryFact> Categories,
    IReadOnlyList<CoachMerchantFact> Merchants,
    SpendingReviewSummaryDto Reviews,
    IReadOnlyList<CoachFact> Facts)
{
    public decimal? LiquidAccountBalance { get; init; }
    public decimal? TotalDebt { get; init; }
    public IReadOnlyList<CoachBudgetFact> Budgets { get; init; } = [];
    public IReadOnlyList<CoachReviewExample> PositiveExamples { get; init; } = [];
    public IReadOnlyList<CoachReviewExample> NegativeExamples { get; init; } = [];
}

public sealed record CreateCoachConversationRequest(string? Title, string? MascotId);
public sealed record AskCoachRequest(string Text, DateOnly? From = null, DateOnly? To = null);

public sealed record CoachConversationDto(
    Guid Id,
    string Title,
    string? MascotId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record CoachMessageDto(
    Guid Id,
    CoachMessageRole Role,
    string Text,
    CoachAnswerMode Mode,
    IReadOnlyList<CoachFact> Facts,
    string? Provider,
    string? Model,
    DateTimeOffset CreatedAt);

public sealed record CoachConversationDetailDto(
    CoachConversationDto Conversation,
    IReadOnlyList<CoachMessageDto> Messages);

public sealed record CoachAnswer(
    string Text,
    CoachAnswerMode Mode,
    IReadOnlyList<CoachFact> Facts,
    IReadOnlyList<string> FollowUps,
    string? Provider = null,
    string? Model = null);

public sealed record CoachProviderRequest(
    string Question,
    CoachContext Context,
    IReadOnlyList<CoachMessageDto> ConversationTail,
    string? MascotId);

public sealed record CoachProviderResult(
    string Text,
    IReadOnlyList<string> FactIds,
    IReadOnlyList<string> FollowUps,
    string? Model = null);

public interface ICoachTextProvider
{
    string ProviderId { get; }
    Task<CoachProviderResult> CompleteAsync(CoachProviderRequest request, CancellationToken cancellationToken);
}

public interface ICoachProviderResolver
{
    Task<ICoachTextProvider?> ResolveAsync(Guid userId, Guid fullWorthSpaceId, CancellationToken cancellationToken);
}

public sealed class NullCoachProviderResolver : ICoachProviderResolver
{
    public Task<ICoachTextProvider?> ResolveAsync(Guid userId, Guid fullWorthSpaceId, CancellationToken cancellationToken) =>
        Task.FromResult<ICoachTextProvider?>(null);
}
