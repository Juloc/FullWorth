using System.Text.Json;
using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FullWorth.Backend.Modules.Fx;

public sealed class FxRateOptions
{
    public const string SectionName = "Fx";
    /// <summary>Set false (or leave the provider unreachable) to run fully offline — conversions then
    /// report missing and aggregates are flagged incomplete; rates are never assumed 1:1.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>ECB-backed, no API key, historical-by-date. Self-hosters can point this at their own mirror.</summary>
    public string ProviderBaseUrl { get; set; } = "https://api.frankfurter.app";
    public int RefreshIntervalHours { get; set; } = 12;
    /// <summary>Days of history re-checked on every refresh cycle, for recent value-date conversions.</summary>
    public int BackfillDays { get; set; } = 60;
    /// <summary>
    /// Days of history fetched ONCE while the rate table does not reach that far back. The wealth trend
    /// defaults to a 12-month window and converts every historical snapshot at ITS OWN date, so with only
    /// <see cref="BackfillDays"/> of rates ten of twelve months could not convert a foreign account at
    /// all - the curve read flat or plainly too low on every fresh install.
    /// </summary>
    public int HistoryBackfillDays { get; set; } = 400;
}

/// <summary>
/// Fetches ECB daily reference rates from a Frankfurter-compatible endpoint and returns them in the
/// app's ECB-native form (EUR→currency). Parsing is isolated here so it can be unit-tested against a
/// mock HTTP handler without any network.
/// </summary>
public sealed class FxRateProvider(HttpClient http)
{
    public async Task<IReadOnlyList<FxRate>> GetRangeAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        // Frankfurter: GET /{start}..{end}?base=EUR -> { base, start_date, end_date, rates: { "2026-08-28": { "USD": 1.08, ... } } }
        using var response = await http.GetAsync($"/{from:yyyy-MM-dd}..{to:yyyy-MM-dd}?base=EUR", ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return Parse(doc.RootElement);
    }

    public static IReadOnlyList<FxRate> Parse(JsonElement root)
    {
        var result = new List<FxRate>();
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("rates", out var rates) || rates.ValueKind != JsonValueKind.Object)
            return result;
        // A single-day response nests { rates: { USD: 1.08 } }; a range nests { rates: { "date": { USD: ... } } }.
        var firstValue = rates.EnumerateObject().FirstOrDefault().Value;
        if (firstValue.ValueKind == JsonValueKind.Number)
        {
            var date = root.TryGetProperty("date", out var d) && d.ValueKind == JsonValueKind.String && DateOnly.TryParse(d.GetString(), out var parsed)
                ? parsed : DateOnly.FromDateTime(DateTime.UtcNow);
            AddDay(result, date, rates);
        }
        else
        {
            foreach (var day in rates.EnumerateObject())
                if (DateOnly.TryParse(day.Name, out var date) && day.Value.ValueKind == JsonValueKind.Object)
                    AddDay(result, date, day.Value);
        }
        return result;
    }

    private static void AddDay(List<FxRate> into, DateOnly date, JsonElement dayRates)
    {
        foreach (var entry in dayRates.EnumerateObject())
            if (entry.Value.ValueKind == JsonValueKind.Number && entry.Value.TryGetDecimal(out var rate) && rate > 0m)
                into.Add(new FxRate { Date = date, Currency = FxSnapshot.Normalize(entry.Name), Rate = rate });
    }
}

/// <summary>
/// How far back one refresh cycle reaches. Pure so the decision is testable without a database or a
/// provider: the deep window is used only while the table does not already cover it.
/// </summary>
public static class FxRateBackfill
{
    /// <summary>
    /// Slack for the start of the deep range landing on a weekend or holiday, which has no ECB fixing -
    /// without it the earliest stored date is always a little later than the requested one and every
    /// cycle would re-fetch the whole history.
    /// </summary>
    private const int WeekendSlackDays = 7;

    public static DateOnly ResolveFrom(DateOnly today, DateOnly? earliestStored, FxRateOptions options)
    {
        var deepFrom = today.AddDays(-Math.Clamp(options.HistoryBackfillDays, 1, 400));
        if (earliestStored is null || earliestStored > deepFrom.AddDays(WeekendSlackDays))
            return deepFrom;
        return today.AddDays(-Math.Clamp(options.BackfillDays, 1, 400));
    }
}

/// <summary>
/// Periodically refreshes the FX rate table (mirrors the banking BankSyncWorker pattern). Best-effort:
/// any failure (offline, provider down) is logged and retried next cycle — the app keeps working, it
/// just reports conversions as incomplete rather than inventing a rate.
/// </summary>
public sealed class FxRateFetchWorker(IServiceScopeFactory scopeFactory, FxRateProvider provider, IOptions<FxRateOptions> options, ILogger<FxRateFetchWorker> logger)
    : BackgroundService
{
    private readonly FxRateOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            logger.LogInformation("FX rate fetching is disabled; cross-currency totals will be marked incomplete until rates are provided.");
            return;
        }
        var interval = TimeSpan.FromHours(Math.Clamp(_options.RefreshIntervalHours, 1, 168));
        while (!stoppingToken.IsCancellationRequested)
        {
            await RefreshAsync(stoppingToken);
            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        try
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FullWorthDbContext>();

            // Reach back far enough for the history the app actually draws, but only while the table does
            // not already cover it - afterwards the cheap recent window is enough.
            var earliestStored = await db.FxRates.AsNoTracking()
                .MinAsync(rate => (DateOnly?)rate.Date, ct);
            var from = FxRateBackfill.ResolveFrom(today, earliestStored, _options);

            var fetched = await provider.GetRangeAsync(from, today, ct);
            if (fetched.Count == 0) return;

            var existing = await db.FxRates.AsNoTracking()
                .Where(rate => rate.Date >= from)
                .Select(rate => new { rate.Date, rate.Currency })
                .ToListAsync(ct);
            var have = existing.Select(e => (e.Date, e.Currency)).ToHashSet();
            // Also de-duplicate WITHIN the answer: a provider (or a self-hosted mirror) that repeats a
            // day would otherwise make the very first insert violate the unique index.
            var toAdd = fetched
                .Where(rate => !have.Contains((rate.Date, rate.Currency)))
                .GroupBy(rate => (rate.Date, rate.Currency))
                .Select(group => group.First())
                .ToList();
            if (toAdd.Count == 0) return;

            var stored = await InsertMissingAsync(db, toAdd, ct);
            logger.LogInformation(
                "Stored {Count} new FX reference rates from {From} to {To} ({Skipped} already present).",
                stored, from, today, toAdd.Count - stored);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "FX rate refresh failed; will retry next cycle. Conversions stay incomplete rather than assuming 1:1.");
        }
    }

    /// <summary>
    /// Inserts the rates that are not stored yet, and does it idempotently.
    ///
    /// The read-then-insert above is not atomic: two overlapping refreshes - two containers coexisting
    /// for a moment during a rolling restart, a startup pass overtaking the previous one - both see the
    /// same gap and both try to fill it. One then violates IX_FxRates_Date_Currency.
    ///
    /// That mattered far more than the error line suggests. Every rate of the cycle was added to ONE
    /// SaveChanges, so a single duplicate rolled the whole batch back: the run stored NOTHING and the
    /// next attempt was 12 hours later. That is where "a required historical FX rate is missing" came
    /// from - the rates were fetched, then thrown away because one of them already existed.
    ///
    /// So the write is now an ON CONFLICT DO NOTHING upsert, in chunks, and a row that someone else
    /// inserted in the meantime is simply skipped. Postgres only; other providers (the SQLite test
    /// harness) keep the plain insert, which is safe there because nothing runs concurrently.
    /// </summary>
    public static async Task<int> InsertMissingAsync(
        FullWorthDbContext db,
        IReadOnlyList<FxRate> rates,
        CancellationToken ct)
    {
        if (db.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) != true)
        {
            db.FxRates.AddRange(rates);
            await db.SaveChangesAsync(ct);
            return rates.Count;
        }

        var stored = 0;
        // Five parameters per row against Postgres' 65535 limit; 500 keeps a wide margin.
        foreach (var chunk in rates.Chunk(500))
        {
            var values = new List<string>(chunk.Length);
            var parameters = new List<object>(chunk.Length * 5);
            foreach (var rate in chunk)
            {
                var index = parameters.Count;
                values.Add($"({{{index}}}, {{{index + 1}}}, {{{index + 2}}}, {{{index + 3}}}, {{{index + 4}}})");
                parameters.Add(rate.Id);
                parameters.Add(rate.Date);
                parameters.Add(rate.Currency);
                parameters.Add(rate.Rate);
                parameters.Add(rate.FetchedAt);
            }

            stored += await db.Database.ExecuteSqlRawAsync(
                $"""
                INSERT INTO "FxRates" ("Id", "Date", "Currency", "Rate", "FetchedAt")
                VALUES {string.Join(", ", values)}
                ON CONFLICT ("Date", "Currency") DO NOTHING;
                """,
                parameters,
                ct);
        }
        return stored;
    }
}
