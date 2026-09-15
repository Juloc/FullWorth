using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Portfolio;

public sealed class Asset
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FullWorthSpaceId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Kind { get; set; } = "other";
    public decimal CurrentValue { get; set; }
    public string Currency { get; set; } = "EUR";

    /// <summary>
    /// The date somebody stated this value holds for — the owner, a document, a provider. NULL means
    /// nobody ever said, and nothing invents one: a stamped "today" used to be indistinguishable from
    /// an appraisal and always looked newer than a real one. Use <see cref="ValueRecordedAt"/> for
    /// "since when do we know this figure".
    /// </summary>
    public DateOnly? ValuedAt { get; set; }

    /// <summary>
    /// When FullWorth last learned this value. Maintained by <c>fullworth_prepare_asset</c> and moves
    /// only when value, currency or stated date change — unlike <see cref="UpdatedAt"/>, which a
    /// rename also bumps. It says nothing about when the asset was appraised.
    /// </summary>
    public DateTimeOffset ValueRecordedAt { get; set; } = DateTimeOffset.UtcNow;
    public decimal? AnnualGrowthRate { get; set; }
    public bool IncludeInNetWorth { get; set; } = true;
    public string? Notes { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class Liability
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FullWorthSpaceId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Kind { get; set; } = "loan";
    public decimal CurrentBalance { get; set; }
    public string Currency { get; set; } = "EUR";
    public decimal? InterestRate { get; set; }
    public decimal? RegularPayment { get; set; }
    public string PaymentCycle { get; set; } = "monthly";
    public DateOnly? NextDueDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public bool IncludeInNetWorth { get; set; } = true;
    public string? Notes { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class NetWorthSnapshot
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FullWorthSpaceId { get; set; }
    public Guid? UserId { get; set; }
    public DateOnly Date { get; set; }
    public string Currency { get; set; } = "EUR";
    public decimal Accounts { get; set; }
    public decimal Assets { get; set; }
    public decimal Liabilities { get; set; }
    public decimal NetWorth { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
