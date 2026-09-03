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
    /// <summary>Days of history to backfill on startup so recent value-date conversions resolve.</summary>
    public int BackfillDays { get; set; } = 60;
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
            var from = today.AddDays(-Math.Clamp(_options.BackfillDays, 1, 400));
            var fetched = await provider.GetRangeAsync(from, today, ct);
            if (fetched.Count == 0) return;

            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FullWorthDbContext>();
            var existing = await db.FxRates.AsNoTracking()
                .Where(rate => rate.Date >= from)
                .Select(rate => new { rate.Date, rate.Currency })
                .ToListAsync(ct);
            var have = existing.Select(e => (e.Date, e.Currency)).ToHashSet();
            var toAdd = fetched.Where(rate => !have.Contains((rate.Date, rate.Currency))).ToList();
            if (toAdd.Count == 0) return;
            db.FxRates.AddRange(toAdd);
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Stored {Count} new FX reference rates from {From} to {To}.", toAdd.Count, from, today);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "FX rate refresh failed; will retry next cycle. Conversions stay incomplete rather than assuming 1:1.");
        }
    }
}
