using System.Text.Json;
using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Intelligence;

public sealed record IntelligenceSuggestionReviewResult(bool Success, string? ErrorCode, IntelligenceSuggestion? Suggestion);

public sealed class IntelligenceSuggestionReviewService(
    IntelligenceDbContext intelligenceDb,
    FullWorthDbContext financeDb)
{
    public Task<List<IntelligenceSuggestion>> ListPendingAsync(int limit, CancellationToken ct) =>
        intelligenceDb.IntelligenceSuggestions.AsNoTracking()
            .Where(x => x.Status == IntelligenceSuggestionStatuses.Pending)
            .OrderByDescending(x => x.Confidence)
            .ThenBy(x => x.CreatedAt)
            .Take(Math.Clamp(limit, 1, 500))
            .ToListAsync(ct);

    public async Task<IntelligenceSuggestionReviewResult> AcceptAsync(Guid suggestionId, Guid actorUserId, CancellationToken ct)
    {
        var suggestion = await intelligenceDb.IntelligenceSuggestions.SingleOrDefaultAsync(x => x.Id == suggestionId, ct);
        if (suggestion is null) return new(false, "suggestion_not_found", null);
        if (suggestion.Status != IntelligenceSuggestionStatuses.Pending) return new(false, "suggestion_not_pending", suggestion);

        if (string.Equals(suggestion.Type, "merchant-category", StringComparison.Ordinal) &&
            string.Equals(suggestion.SubjectType, "merchant", StringComparison.Ordinal))
            return await AcceptMerchantCategoryAsync(suggestion, actorUserId, ct);

        if (suggestion.Type is "product-normalization" or "receipt-follow-up" or "contract-enrichment")
            return await AcceptReviewedProposalAsync(suggestion, actorUserId, ct);

        return new(false, "unsupported_suggestion_type", suggestion);
    }

    private async Task<IntelligenceSuggestionReviewResult> AcceptMerchantCategoryAsync(
        IntelligenceSuggestion suggestion,
        Guid actorUserId,
        CancellationToken ct)
    {
        if (suggestion.FullWorthSpaceId is null)
            return new(false, "invalid_suggestion_payload", suggestion);

        MerchantCategorySuggestionPayload payload;
        try
        {
            payload = JsonSerializer.Deserialize<MerchantCategorySuggestionPayload>(suggestion.ProposedPayloadJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new JsonException("Missing payload.");
        }
        catch (JsonException)
        {
            return new(false, "invalid_suggestion_payload", suggestion);
        }

        var normalizedCounterparty = suggestion.SubjectId.Trim();
        var direction = payload.Direction?.Trim().ToLowerInvariant();
        var categoryKey = payload.CategoryKey?.Trim();
        if (normalizedCounterparty.Length is < 1 or > 320 || direction is not ("income" or "expense") || string.IsNullOrWhiteSpace(categoryKey))
            return new(false, "invalid_suggestion_payload", suggestion);

        var category = await financeDb.Categories.AsNoTracking().SingleOrDefaultAsync(x =>
            x.FullWorthSpaceId == suggestion.FullWorthSpaceId.Value &&
            x.Key == categoryKey &&
            !x.IsArchived, ct);
        if (category is null) return new(false, "category_not_found", suggestion);

        var mapping = await intelligenceDb.LearnedMerchantMappings.SingleOrDefaultAsync(x =>
            x.FullWorthSpaceId == suggestion.FullWorthSpaceId.Value &&
            x.NormalizedCounterparty == normalizedCounterparty &&
            x.Direction == direction, ct);
        var now = DateTimeOffset.UtcNow;
        if (mapping is null)
        {
            mapping = new LearnedMerchantMapping
            {
                FullWorthSpaceId = suggestion.FullWorthSpaceId.Value,
                CreatedByUserId = actorUserId,
                NormalizedCounterparty = normalizedCounterparty,
                Direction = direction,
                CategoryId = category.Id,
                Source = "ai-confirmed",
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now
            };
            intelligenceDb.LearnedMerchantMappings.Add(mapping);
        }
        else
        {
            mapping.CategoryId = category.Id;
            mapping.CreatedByUserId = actorUserId;
            mapping.Source = "ai-confirmed";
            mapping.IsActive = true;
            mapping.UpdatedAt = now;
        }

        MarkAccepted(suggestion, actorUserId, now);
        intelligenceDb.IntelligenceFeedbackEvents.Add(new IntelligenceFeedbackEvent
        {
            FullWorthSpaceId = suggestion.FullWorthSpaceId.Value,
            UserId = actorUserId,
            EventType = "ai_suggestion_accepted",
            SubjectType = "merchant",
            SubjectId = normalizedCounterparty,
            SubjectFingerprint = string.Empty,
            OldValueJson = "{}",
            NewValueJson = JsonSerializer.Serialize(new { categoryKey, direction }),
            Source = "ai-review",
            CloudEligible = false,
            CreatedAt = now
        });
        await intelligenceDb.SaveChangesAsync(ct);
        return new(true, null, suggestion);
    }

    /// <summary>
    /// Phase-3 product/receipt/contract proposals are reviewable, but accepting them does not mutate
    /// FullWorthDbContext. Their domain-specific application belongs to the existing purchase/receipt/
    /// contract workflows where stale/manual-protection rules can be enforced explicitly.
    /// </summary>
    private async Task<IntelligenceSuggestionReviewResult> AcceptReviewedProposalAsync(
        IntelligenceSuggestion suggestion,
        Guid actorUserId,
        CancellationToken ct)
    {
        if (!suggestion.FullWorthSpaceId.HasValue)
            return new(false, "invalid_suggestion_scope", suggestion);

        // Reject stale references for persisted product/receipt subjects. Contract-candidate subjects are
        // intentionally derived fingerprints rather than FinanceDb rows, so their scope is validated only.
        if (suggestion.Type == "product-normalization")
        {
            if (!Guid.TryParseExact(suggestion.SubjectId, "N", out var itemId))
                return new(false, "invalid_suggestion_payload", suggestion);
            var exists = await financeDb.PurchaseItems.AsNoTracking().AnyAsync(x =>
                x.Id == itemId && x.Purchase.FullWorthSpaceId == suggestion.FullWorthSpaceId.Value, ct);
            if (!exists) return new(false, "subject_not_found", suggestion);
        }
        else if (suggestion.Type == "receipt-follow-up")
        {
            if (!Guid.TryParseExact(suggestion.SubjectId, "N", out var documentId))
                return new(false, "invalid_suggestion_payload", suggestion);
            var exists = await financeDb.PurchaseDocuments.AsNoTracking().AnyAsync(x =>
                x.Id == documentId && x.Purchase.FullWorthSpaceId == suggestion.FullWorthSpaceId.Value, ct);
            if (!exists) return new(false, "subject_not_found", suggestion);
        }
        else
        {
            var scopeExists = await financeDb.FullWorthSpaces.AsNoTracking()
                .AnyAsync(x => x.Id == suggestion.FullWorthSpaceId.Value, ct);
            if (!scopeExists) return new(false, "subject_not_found", suggestion);
        }

        // Ensure the provider payload is at least syntactically valid JSON before recording approval.
        try
        {
            using var _ = JsonDocument.Parse(suggestion.ProposedPayloadJson);
        }
        catch (JsonException)
        {
            return new(false, "invalid_suggestion_payload", suggestion);
        }

        var now = DateTimeOffset.UtcNow;
        MarkAccepted(suggestion, actorUserId, now);
        intelligenceDb.IntelligenceFeedbackEvents.Add(new IntelligenceFeedbackEvent
        {
            FullWorthSpaceId = suggestion.FullWorthSpaceId.Value,
            UserId = actorUserId,
            EventType = "ai_suggestion_accepted",
            SubjectType = suggestion.SubjectType,
            SubjectId = suggestion.SubjectId,
            SubjectFingerprint = string.Empty,
            OldValueJson = "{}",
            NewValueJson = suggestion.ProposedPayloadJson,
            Source = "ai-review",
            CloudEligible = false,
            CreatedAt = now
        });
        await intelligenceDb.SaveChangesAsync(ct);
        return new(true, null, suggestion);
    }

    public async Task<IntelligenceSuggestionReviewResult> RejectAsync(Guid suggestionId, Guid actorUserId, CancellationToken ct)
    {
        var suggestion = await intelligenceDb.IntelligenceSuggestions.SingleOrDefaultAsync(x => x.Id == suggestionId, ct);
        if (suggestion is null) return new(false, "suggestion_not_found", null);
        if (suggestion.Status != IntelligenceSuggestionStatuses.Pending) return new(false, "suggestion_not_pending", suggestion);

        suggestion.Status = IntelligenceSuggestionStatuses.Rejected;
        suggestion.ReviewedAt = DateTimeOffset.UtcNow;
        suggestion.ReviewedByUserId = actorUserId;
        if (suggestion.FullWorthSpaceId.HasValue)
        {
            intelligenceDb.IntelligenceFeedbackEvents.Add(new IntelligenceFeedbackEvent
            {
                FullWorthSpaceId = suggestion.FullWorthSpaceId.Value,
                UserId = actorUserId,
                EventType = "ai_suggestion_rejected",
                SubjectType = suggestion.SubjectType,
                SubjectId = suggestion.SubjectId,
                SubjectFingerprint = string.Empty,
                OldValueJson = suggestion.ProposedPayloadJson,
                NewValueJson = "{}",
                Source = "ai-review",
                CloudEligible = false,
                CreatedAt = DateTimeOffset.UtcNow
            });
        }
        await intelligenceDb.SaveChangesAsync(ct);
        return new(true, null, suggestion);
    }

    private static void MarkAccepted(IntelligenceSuggestion suggestion, Guid actorUserId, DateTimeOffset now)
    {
        suggestion.Status = IntelligenceSuggestionStatuses.Accepted;
        suggestion.ReviewedAt = now;
        suggestion.ReviewedByUserId = actorUserId;
    }

    private sealed record MerchantCategorySuggestionPayload(string? CategoryKey, string? Direction, string? EvidenceSummary);
}
