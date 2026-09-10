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

    /// <summary>
    /// Beyond this the fixing is reported as stale. It is a LABEL, not a refusal: shortening the
    /// lookback instead would make conversions that work today start failing, which trades a stated
    /// uncertainty for a missing number. Four days covers a weekend plus a public holiday.
    /// </summary>
    public const int StaleAfterDays = 4;
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
    private decimal? EurRate(string currency, DateOnly date) => EurFixing(currency, date)?.Rate;

    /// <summary>The fixing used, with its own date - which is what makes a converted total explainable.</summary>
    private (DateOnly Date, decimal Rate)? EurFixing(string currency, DateOnly date)
    {
        // EUR needs no fixing, and pretending it has one dated today would make every EUR-based total
        // look freshly rated. It carries the asked-for date, which is exactly as old as the question.
        if (currency == "EUR") return (date, 1m);
        if (!_byCurrency.TryGetValue(currency, out var list)) return null;
        (DateOnly Date, decimal Rate)? found = null;
        var earliest = date.AddDays(-LookbackDays);
        foreach (var (d, rate) in list)
        {
            if (d > date) break;
            if (d >= earliest) found = (d, rate);
        }
        return found;
    }

    /// <summary>
    /// Converts <paramref name="amount"/> from <paramref name="from"/> to the base currency using the
    /// rate effective on <paramref name="date"/>. Returns null when a required rate is missing.
    /// </summary>
    public decimal? ToBaseOn(decimal amount, string from, DateOnly date) =>
        // An amount already in the base currency needs no conversion and reports none, but it is still a
        // perfectly good base-currency amount - treating it as unconvertible would mark every total that
        // contains base-currency money incomplete.
        Normalize(from) == _base ? amount : ConvertToBase(amount, from, date)?.Amount;

    /// <summary>
    /// The same conversion, plus the rate and the fixing date behind it. A cross-rate through EUR uses
    /// two fixings and reports the older one: a total is only as current as its stalest input.
    /// </summary>
    public FxConversion? ConvertToBase(decimal amount, string from, DateOnly date)
    {
        from = Normalize(from);
        // Not a conversion at all. Reporting a rate of 1 "as of" some fixing would invent provenance
        // for a number nobody converted.
        if (from == _base) return null;

        var fromFixing = EurFixing(from, date);
        if (fromFixing is null || fromFixing.Value.Rate == 0m) return null;
        var amountInEur = amount / fromFixing.Value.Rate;
        if (_base == "EUR")
            return new FxConversion(amountInEur, from, 1m / fromFixing.Value.Rate, fromFixing.Value.Date);

        var baseFixing = EurFixing(_base, date);
        if (baseFixing is null) return null;
        var converted = amountInEur * baseFixing.Value.Rate;
        var effective = baseFixing.Value.Rate / fromFixing.Value.Rate;
        var oldest = fromFixing.Value.Date <= baseFixing.Value.Date ? fromFixing.Value.Date : baseFixing.Value.Date;
        return new FxConversion(converted, from, effective, oldest);
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
