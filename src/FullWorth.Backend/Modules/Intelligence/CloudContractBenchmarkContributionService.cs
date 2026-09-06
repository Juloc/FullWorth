using System.Text.Json;
using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Contracts;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Intelligence;

/// <summary>
/// Creates privacy-safe, instance-level contract benchmark observations from structured local contracts.
/// No contract/provider/account/user identifier or free text is included in the cloud payload.
/// </summary>
public sealed class CloudContractBenchmarkContributionService(
    FullWorthDbContext financeDb,
    IntelligenceDbContext intelligenceDb,
    CloudIntelligenceStateService cloudState)
{
    public async Task<int> QueueCurrentAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (!await cloudState.HasCurrentActiveConsentAsync(ct))
            return 0;

        var state = await cloudState.GetEnabledStateAsync(ct);
        if (state is null)
            return 0;

        var contracts = await financeDb.Contracts.AsNoTracking()
            .Where(x => x.IsActive && x.CategoryId != null && x.Amount != 0m)
            .Select(x => new
            {
                x.Amount,
                x.Currency,
                x.BillingCycle,
                x.Interval,
                CategoryKey = financeDb.Categories
                    .Where(c => c.Id == x.CategoryId &&
                                c.FullWorthSpaceId == x.FullWorthSpaceId &&
                                !c.IsArchived)
                    .Select(c => c.Key)
                    .FirstOrDefault()
            })
            .ToListAsync(ct);

        var observations = contracts
            .Select(x =>
            {
                var metricKey = MetricForCategory(x.CategoryKey);
                var currency = NormalizeCurrency(x.Currency);
                if (metricKey is null || currency is null) return null;

                var monthly = Math.Abs(x.Amount) *
                              ContractCycle.PeriodsPerYear(x.BillingCycle, x.Interval) / 12m;
                if (monthly is <= 0m or > 1_000_000m) return null;

                return new ContractBenchmarkSource(metricKey, currency, monthly);
            })
            .Where(x => x is not null)
            .Cast<ContractBenchmarkSource>()
            .ToList();

        if (observations.Count == 0)
            return 0;

        var country = await InstanceCountryAsync(ct);
        var observedMonth = now.ToString("yyyy-MM");
        var revisionDate = now.ToString("yyyy-MM-dd");
        var queued = 0;

        foreach (var group in observations.GroupBy(x => new { x.MetricKey, x.Currency }))
        {
            // One observation per instance/metric/currency/month. If several matching contracts exist
            // locally, use their median so a household with several lines/policies does not dominate.
            var value = Math.Round(Median(group.Select(x => x.MonthlyValue)), 2);
            var idempotencyKey =
                $"benchmark:{group.Key.MetricKey}:{group.Key.Currency}:{observedMonth}:{revisionDate}";
            var payload = JsonSerializer.Serialize(new
            {
                metricKey = group.Key.MetricKey,
                value,
                currency = group.Key.Currency,
                country,
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
                queued++;
            }
            else if (existing.Status is CloudSubmissionStatuses.Queued or CloudSubmissionStatuses.Failed)
            {
                // Same-day recomputation may reflect a corrected contract. Update only unsent data; once
                // transmitted, the next day's revision becomes a new idempotent event.
                existing.PayloadJson = payload;
                existing.Status = CloudSubmissionStatuses.Queued;
                existing.NextAttemptAt = null;
                existing.ErrorCode = null;
            }
        }

        await intelligenceDb.SaveChangesAsync(ct);
        return queued;
    }

    public static string? MetricForCategory(string? categoryKey)
    {
        var key = categoryKey?.Trim().ToLowerInvariant();
        return key switch
        {
            "housing.electricity" => "contract.energy.monthly_cost",
            "housing.internet" => "contract.internet.monthly_cost",
            "insurance.health" => "contract.insurance.health.monthly_cost",
            "insurance" => "contract.insurance.monthly_cost",
            _ when key?.StartsWith("insurance.", StringComparison.Ordinal) == true
                => "contract.insurance.monthly_cost",
            _ => null
        };
    }

    private async Task<string?> InstanceCountryAsync(CancellationToken ct)
    {
        var countries = (await financeDb.BankConnections.AsNoTracking()
                .Where(x => x.Country != null && x.Country != "")
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

    private static string? NormalizeCurrency(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim().ToUpperInvariant();
        return normalized.Length == 3 && normalized.All(char.IsAsciiLetter)
            ? normalized
            : null;
    }

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

    private sealed record ContractBenchmarkSource(
        string MetricKey,
        string Currency,
        decimal MonthlyValue);
}

public sealed class CloudContractBenchmarkContributionWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<CloudContractBenchmarkContributionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var queued = await scope.ServiceProvider
                    .GetRequiredService<CloudContractBenchmarkContributionService>()
                    .QueueCurrentAsync(DateTimeOffset.UtcNow, stoppingToken);
                if (queued > 0)
                    logger.LogInformation("Queued {Count} privacy-safe contract benchmark observation(s).", queued);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "FullWorth Cloud contract benchmark contribution cycle failed.");
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
