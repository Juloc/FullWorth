using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Fx;

/// <summary>
/// A daily FX reference rate (UI_UX_SPEC §18, PRODUCT_DECISIONS "Currency"). Stored in ECB-native form:
/// <see cref="Rate"/> is the value of 1 EUR in <see cref="Currency"/> on <see cref="Date"/> (so EUR→USD
/// of 1.08 means 1 EUR = 1.08 USD). EUR itself is never stored (its rate is 1 by definition). Any
/// cross-rate (e.g. USD→GBP, or converting to a non-EUR base) is derived through EUR.
/// </summary>

/// <summary>Builds <see cref="FxSnapshot"/>s from the stored rate table for a base currency + date window.</summary>
public sealed class CurrencyConverter(FullWorthDbContext db)
{
    public async Task<FxSnapshot> PrepareAsync(string baseCurrency, DateOnly from, DateOnly to, CancellationToken ct)
    {
        // Pull a little before `from` so a date whose exact fixing is a weekend still resolves.
        var start = from.AddDays(-14);
        var rows = await db.FxRates.AsNoTracking()
            .Where(rate => rate.Date >= start && rate.Date <= to)
            .Select(rate => new { rate.Date, rate.Currency, rate.Rate })
            .ToListAsync(ct);
        return new FxSnapshot(baseCurrency, rows.Select(r => (r.Date, r.Currency, r.Rate)));
    }

    /// <summary>Snapshot for converting current values (balances/net worth) at the latest available rate.</summary>
    public Task<FxSnapshot> PrepareLatestAsync(string baseCurrency, DateOnly asOf, CancellationToken ct) =>
        PrepareAsync(baseCurrency, asOf, asOf, ct);
}
