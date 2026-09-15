using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FullWorth.Backend.Modules.Contracts.PriceChanges;

public sealed record PriceChangePreviewView(
    Guid ContractId,
    string ContractName,
    decimal OldAmount,
    decimal NewAmount,
    decimal PercentChange,
    string Currency,
    Guid EvidenceTransactionId,
    DateOnly EvidenceTransactionDate,
    bool AutoDetected);

public sealed record PriceChangeSuggestionView(
    Guid Id,
    Guid ContractId,
    decimal OldAmount,
    decimal NewAmount,
    decimal PercentChange,
    DateOnly DetectedOn,
    Guid EvidenceTransactionId,
    DateOnly EvidenceTransactionDate,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record PriceChangeDetectionOutcome(
    PriceChangeMutationResult Result,
    IReadOnlyList<PriceChangeSuggestionView>? Suggestions = null,
    int AutoRefreshedContracts = 0);

public enum PriceChangeMutationResult
{
    Success,
    NotFound,
    Forbidden
}

public sealed record PriceChangeMutationOutcome(PriceChangeMutationResult Result, PriceChangeSuggestionView? Suggestion = null);

public sealed record PriceChangeDetectionRequest(DateOnly DetectedOn);
