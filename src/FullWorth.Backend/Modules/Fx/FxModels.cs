using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Fx;

/// <summary>
/// A daily FX reference rate (UI_UX_SPEC §18, PRODUCT_DECISIONS "Currency"). Stored in ECB-native form:
/// <see cref="Rate"/> is the value of 1 EUR in <see cref="Currency"/> on <see cref="Date"/> (so EUR→USD
/// of 1.08 means 1 EUR = 1.08 USD). EUR itself is never stored (its rate is 1 by definition). Any
/// cross-rate (e.g. USD→GBP, or converting to a non-EUR base) is derived through EUR.
/// </summary>

public sealed class FxRate
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateOnly Date { get; set; }
    public string Currency { get; set; } = string.Empty;
    public decimal Rate { get; set; }
    public DateTimeOffset FetchedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// One conversion, with what it was done WITH. A converted total used to be a bare number: the user
/// could not tell which rate produced it or how old that rate was, and a fixing up to two weeks old
/// looked exactly like this morning's.
///
/// <see cref="Rate"/> is the effective rate actually applied (base per unit of the original
/// currency), and <see cref="RateDate"/> is the OLDEST fixing the conversion relied on — a cross-rate
/// through EUR uses two, and a total is only as current as its stalest input.
/// </summary>
public sealed record FxConversion(decimal Amount, string From, decimal Rate, DateOnly RateDate)
{
    /// <summary>How old the fixing was, relative to the date the conversion was asked for.</summary>
    public int AgeInDays(DateOnly asOf) => Math.Max(0, asOf.DayNumber - RateDate.DayNumber);
}
