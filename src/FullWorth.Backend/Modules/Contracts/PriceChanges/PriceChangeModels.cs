using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FullWorth.Backend.Modules.Contracts.PriceChanges;

public sealed class PriceChangeSuggestion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ContractId { get; set; }
    public decimal OldAmount { get; set; }
    public decimal NewAmount { get; set; }
    public decimal PercentChange { get; set; }
    public DateOnly DetectedOn { get; set; }
    public Guid EvidenceTransactionId { get; set; }
    public string Status { get; set; } = PriceChangeSuggestionStatuses.Pending;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public RecurringContract Contract { get; set; } = null!;
    public FullWorth.Backend.Modules.Transactions.FinanceTransaction EvidenceTransaction { get; set; } = null!;
}

public static class PriceChangeSuggestionStatuses
{
    public const string Pending = "pending";
    public const string Confirmed = "confirmed";
    public const string Ignored = "ignored";
}

public enum PriceChangeAutoRefreshPolicy
{
    Disabled,
    AutoDetectedContracts
}

public sealed class PriceChangeDetectionOptions
{
    public const string SectionName = "PriceChanges";

    public decimal MinimumPercentChange { get; set; } = 5m;
    public decimal? MinimumAbsoluteChange { get; set; }
    public PriceChangeAutoRefreshPolicy AutoRefreshPolicy { get; set; } = PriceChangeAutoRefreshPolicy.AutoDetectedContracts;
}
