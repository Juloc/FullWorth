using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Fx;

/// <summary>
/// A daily FX reference rate (UI_UX_SPEC §18, PRODUCT_DECISIONS "Currency"). Stored in ECB-native form:
/// <see cref="Rate"/> is the value of 1 EUR in <see cref="Currency"/> on <see cref="Date"/> (so EUR→USD
/// of 1.08 means 1 EUR = 1.08 USD). EUR itself is never stored (its rate is 1 by definition). Any
/// cross-rate (e.g. USD→GBP, or converting to a non-EUR base) is derived through EUR.
/// </summary>

/// <summary>
/// Accumulates base-currency conversions for one analytics request and remembers whether any amount
/// could not be converted (no rate in the lookback window). Analytics surface that single flag as
/// "incomplete" (spec §18) and skip the unconvertible line — a missing rate is never assumed 1:1.
/// </summary>
public sealed class FxAccumulator(FxSnapshot snapshot)
{
    public bool Incomplete { get; private set; }

    /// <summary>Converts to the base currency at the given date, or returns null (and flags Incomplete) when no rate is available.</summary>
    public decimal? Convert(decimal amount, string currency, DateOnly date)
    {
        var converted = snapshot.ToBaseOn(amount, currency, date);
        if (converted is null) Incomplete = true;
        return converted;
    }
}
