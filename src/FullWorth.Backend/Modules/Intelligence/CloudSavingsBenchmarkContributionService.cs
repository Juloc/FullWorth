using System.Text.Json;
using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Fx;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Intelligence;

public sealed record SavingsBenchmarkSnapshot(
    Guid FullWorthSpaceId,
    decimal SavingsRate,
    decimal MonthlyIncome,
    string BaseCurrency,
    string ObservedMonth,
    string? Country,
    string IncomeBand);

/// <summary>
/// Derives a privacy-safe savings-rate benchmark from the last completed calendar month.
/// Cloud payloads contain only the ratio plus coarse comparison dimensions; never transactions or income amounts.
/// </summary>
public sealed class CloudSavingsBenchmarkContributionService(
    FullWorthDbContext financeDb,
    IntelligenceDbContext intelligenceDb,
    CloudIntelligenceStateService cloudState,
    CurrencyConverter currencyConverter)
{
    public async Task<SavingsBenchmarkSnapshot?> ComputeSpaceAsync(
        Guid fullWorthSpaceId,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var space = await financeDb.FullWorthSpaces.AsNoTracking()
            .Where(x => x.Id == fullWorthSpaceId)
            .Select(x => new { x.Id, x.BaseCurrency })
            .SingleOrDefaultAsync(ct);
        if (space is null) return null;

        var currentMonth = new DateOnly(now.Year, now.Month, 1);
        var monthStart = currentMonth.AddMonths(-1);
        var monthEnd = currentMonth.AddDays(-1);

        var accountIds = await financeDb.Accounts.AsNoTracking()
            .Where(x => x.FullWorthSpaceId == fullWorthSpaceId)
            .Select(x => x.Id)
            .ToListAsync(ct);
        if (accountIds.Count == 0) return null;

        var transactions = await financeDb.Transactions.AsNoTracking()
            .Where(x => accountIds.Contains(x.AccountId) &&
                        !x.IsIgnored &&
                        !x.IsTransfer &&
                        x.Status != "PDNG" &&
                        ((x.BookingDate >= monthStart && x.BookingDate <= monthEnd) ||
                         (x.BookingDate == null && x.ValueDate >= monthStart && x.ValueDate <= monthEnd)))
            .Select(x => new
            {
                x.Amount,
                x.Currency,
                x.BookingDate,
                x.ValueDate,
                x.RefundOfTransactionId
            })
            .ToListAsync(ct);
        if (transactions.Count == 0) return null;

        var fx = await currencyConverter.PrepareAsync(
            space.BaseCurrency,
            monthStart,
            monthEnd,
            ct);

        decimal income = 0m;
        decimal netCashflow = 0m;
        foreach (var transaction in transactions)
        {
            var date = transaction.BookingDate ?? transaction.ValueDate ?? monthEnd;
            var converted = fx.ToBaseOn(transaction.Amount, transaction.Currency, date);
            if (!converted.HasValue)
            {
                // Never derive a benchmark from an incomplete FX month.
                return null;
            }

            netCashflow += converted.Value;
            if (converted.Value > 0m && transaction.RefundOfTransactionId is null)
                income += converted.Value;
        }

        if (income <= 0m) return null;

        var rate = netCashflow / income;
        // Ignore pathological/incomplete finance periods rather than poisoning shared statistics.
        if (rate is < -5m or > 2m) return null;

        var country = await SpaceCountryAsync(fullWorthSpaceId, ct);
        var annualizedIncome = income * 12m;
        var incomeBand = string.Equals(space.BaseCurrency, "EUR", StringComparison.OrdinalIgnoreCase)
            ? IncomeBand(annualizedIncome)
            : "unknown";

        return new SavingsBenchmarkSnapshot(
            fullWorthSpaceId,
            Math.Round(rate, 4),
            Math.Round(income, 2),
            space.BaseCurrency.Trim().ToUpperInvariant(),
            monthStart.ToString("yyyy-MM"),
            country,
            incomeBand);
    }

    public async Task<int> QueueCurrentAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (!await cloudState.HasCurrentActiveConsentAsync(ct))
            return 0;

        var state = await cloudState.GetEnabledStateAsync(ct);
        if (state is null) return 0;

        var spaceIds = await financeDb.FullWorthSpaces.AsNoTracking()
            .Select(x => x.Id)
            .ToListAsync(ct);

        var snapshots = new List<SavingsBenchmarkSnapshot>();
        foreach (var spaceId in spaceIds)
        {
            var snapshot = await ComputeSpaceAsync(spaceId, now, ct);
            if (snapshot is not null)
                snapshots.Add(snapshot);
        }
        if (snapshots.Count == 0) return 0;

        // Cloud aggregates one latest value per instance/month. Reduce multiple local spaces to one
        // instance observation first so an instance with many spaces cannot carry more weight.
        var rate = Math.Round(Median(snapshots.Select(x => x.SavingsRate)), 4);
        var observedMonth = snapshots.Select(x => x.ObservedMonth).Distinct().Single();
        var country = snapshots.Select(x => x.Country)
            .Where(x => x is not null)
            .Distinct(StringComparer.Ordinal)
            .Take(2)
            .ToList() is { Count: 1 } countries ? countries[0] : null;
        var incomeBand = CombinedIncomeBand(snapshots);
        var revisionDate = now.ToString("yyyy-MM-dd");
        var idempotencyKey = $"benchmark:savings.rate:{observedMonth}:{revisionDate}";

        var payload = JsonSerializer.Serialize(new
        {
            metricKey = "savings.rate",
            value = rate,
            country,
            incomeBand,
            observedMonth
        });

        var existing = await intelligenceDb.CloudSubmissionOutbox
            .SingleOrDefaultAsync(x => x.IdempotencyKey == idempotencyKey, ct);
        if (existing is null)
        {
            intelligenceDb.CloudSubmissionOutbox.Add(new CloudSubmissionOutbox
            {
                InstanceId = state.InstanceId,
                IdempotencyKey = idempotencyKey,
                SchemaVersion = CloudIntelligencePolicy.SubmissionSchemaVersion,
                EventType = "benchmark_observation",
                PayloadJson = payload,
                Status = CloudSubmissionStatuses.Queued,
                CreatedAt = now
            });
            await intelligenceDb.SaveChangesAsync(ct);
            return 1;
        }

        if (existing.Status is CloudSubmissionStatuses.Queued or CloudSubmissionStatuses.Failed)
        {
            existing.PayloadJson = payload;
            existing.Status = CloudSubmissionStatuses.Queued;
            existing.NextAttemptAt = null;
            existing.ErrorCode = null;
            await intelligenceDb.SaveChangesAsync(ct);
        }

        return 0;
    }

    private async Task<string?> SpaceCountryAsync(Guid fullWorthSpaceId, CancellationToken ct)
    {
        var countries = (await financeDb.BankConnections.AsNoTracking()
                .Where(x => x.FullWorthSpaceId == fullWorthSpaceId)
                .Select(x => x.Country)
                .Distinct()
                .Take(3)
                .ToListAsync(ct))
            .Select(NormalizeCountry)
            .Where(x => x is not null)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return countries.Count == 1 ? countries[0] : null;
    }

    private static string CombinedIncomeBand(IReadOnlyList<SavingsBenchmarkSnapshot> snapshots)
    {
        if (snapshots.Count == 0 ||
            snapshots.Any(x => !string.Equals(x.BaseCurrency, "EUR", StringComparison.OrdinalIgnoreCase)))
            return "unknown";

        var annualized = snapshots.Select(x => x.MonthlyIncome * 12m);
        return IncomeBand(Median(annualized));
    }

    public static string IncomeBand(decimal annualIncome) => annualIncome switch
    {
        < 25_000m => "lt_25k",
        < 50_000m => "25k_50k",
        < 75_000m => "50k_75k",
        < 100_000m => "75k_100k",
        < 150_000m => "100k_150k",
        _ => "150k_plus"
    };

    private static string? NormalizeCountry(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim().ToUpperInvariant();
        return normalized.Length == 2 && normalized.All(char.IsAsciiLetter)
            ? normalized
            : null;
    }

    private static decimal Median(IEnumerable<decimal> values)
    {
        var sorted = values.OrderBy(x => x).ToArray();
        if (sorted.Length == 0) return 0m;
        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 1
            ? sorted[middle]
            : (sorted[middle - 1] + sorted[middle]) / 2m;
    }
}

public sealed class CloudSavingsBenchmarkContributionWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<CloudSavingsBenchmarkContributionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var queued = await scope.ServiceProvider
                    .GetRequiredService<CloudSavingsBenchmarkContributionService>()
                    .QueueCurrentAsync(DateTimeOffset.UtcNow, stoppingToken);
                if (queued > 0)
                    logger.LogInformation("Queued privacy-safe savings benchmark observation.");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "FullWorth Cloud savings benchmark contribution cycle failed.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromHours(24), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
