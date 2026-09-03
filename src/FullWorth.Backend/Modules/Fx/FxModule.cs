using FullWorth.Backend.Data;
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
/// An in-memory snapshot of the rate table over a date window, so an aggregation can convert many
/// amounts without a query per amount. Conversions that lack a rate return null — callers mark the
/// aggregate incomplete and must NEVER assume 1:1 (spec §18).
/// </summary>
public sealed class FxSnapshot
{
    // How far back to accept an older rate for a date with no exact entry (weekends/holidays have no
    // ECB fixing). Kept small so a stale rate can't masquerade as a current one indefinitely.
    private const int LookbackDays = 14;
    private readonly string _base;
    // currency -> rates sorted by date ascending (Date, EUR->currency rate).
    private readonly Dictionary<string, List<(DateOnly Date, decimal Rate)>> _byCurrency;

    public FxSnapshot(string baseCurrency, IEnumerable<(DateOnly Date, string Currency, decimal Rate)> rows)
    {
        _base = Normalize(baseCurrency);
        _byCurrency = rows
            .GroupBy(r => Normalize(r.Currency))
            .ToDictionary(g => g.Key, g => g.OrderBy(r => r.Date).Select(r => (r.Date, r.Rate)).ToList());
    }

    /// <summary>EUR→currency rate effective on <paramref name="date"/> (the latest fixing at or before it, within the lookback); null if none.</summary>
    private decimal? EurRate(string currency, DateOnly date)
    {
        if (currency == "EUR") return 1m;
        if (!_byCurrency.TryGetValue(currency, out var list)) return null;
        decimal? found = null;
        var earliest = date.AddDays(-LookbackDays);
        foreach (var (d, rate) in list)
        {
            if (d > date) break;
            if (d >= earliest) found = rate;
        }
        return found;
    }

    /// <summary>
    /// Converts <paramref name="amount"/> from <paramref name="from"/> to the base currency using the
    /// rate effective on <paramref name="date"/>. Returns null when a required rate is missing.
    /// </summary>
    public decimal? ToBaseOn(decimal amount, string from, DateOnly date)
    {
        from = Normalize(from);
        if (from == _base) return amount;
        var eurToFrom = EurRate(from, date);
        if (eurToFrom is null or 0m) return null;
        var amountInEur = amount / eurToFrom.Value;
        if (_base == "EUR") return amountInEur;
        var eurToBase = EurRate(_base, date);
        if (eurToBase is null) return null;
        return amountInEur * eurToBase.Value;
    }

    public static string Normalize(string? currency) =>
        string.IsNullOrWhiteSpace(currency) ? "EUR" : currency.Trim().ToUpperInvariant();
}

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
