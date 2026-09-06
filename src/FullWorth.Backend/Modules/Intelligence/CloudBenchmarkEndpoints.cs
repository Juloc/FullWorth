using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Contracts;
using FullWorth.Backend.Security;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Intelligence;

public static class CloudBenchmarkEndpoints
{
    public static IEndpointRouteBuilder MapCloudBenchmarkEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/intelligence/benchmarks").WithTags("Intelligence Benchmarks");

        group.MapGet("/", async (
            string metricKey,
            string? currency,
            string? country,
            string? regionBucket,
            string? householdSizeBand,
            string? incomeBand,
            string? ageBand,
            string? observedMonth,
            CurrentUserContext currentUser,
            CloudIntelligenceStateService cloudState,
            CloudInstanceCredentialStore credentials,
            IFullWorthCloudClient cloud,
            CancellationToken ct) =>
        {
            _ = currentUser.RequireUserId();

            if (!await cloudState.HasCurrentActiveConsentAsync(ct))
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

            var state = await cloudState.GetEnabledStateAsync(ct);
            if (state is null)
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

            var secret = await credentials.GetSecretAsync(state.InstanceId, ct);
            if (string.IsNullOrWhiteSpace(secret))
            {
                try
                {
                    var registration = await cloud.RegisterAsync(
                        state.InstanceId,
                        CloudIntelligencePolicy.CurrentVersion,
                        typeof(CloudBenchmarkEndpoints).Assembly.GetName().Version?.ToString() ?? "unknown",
                        ct);
                    await credentials.SaveAsync(registration, ct);
                    secret = registration.Credential;
                    await cloudState.SetTransportStatusAsync(
                        state.InstanceId, null, registration.EntitlementStatus,
                        DateTimeOffset.UtcNow, null, ct);
                }
                catch (FullWorthCloudException ex)
                {
                    await cloudState.SetTransportStatusAsync(state.InstanceId, ex.ErrorCode, null, null, null, ct);
                    return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
                }
            }

            try
            {
                var result = await cloud.GetBenchmarkAsync(
                    secret,
                    metricKey,
                    currency,
                    country,
                    regionBucket,
                    householdSizeBand,
                    incomeBand,
                    ageBand,
                    observedMonth,
                    ct);
                return result is null ? Results.NoContent() : Results.Ok(result);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = "invalid_benchmark_query", message = ex.Message });
            }
            catch (FullWorthCloudException ex)
            {
                await cloudState.SetTransportStatusAsync(state.InstanceId, ex.ErrorCode, null, null, null, ct);
                return Results.StatusCode(ex.StatusCode is { } status ? (int)status : StatusCodes.Status503ServiceUnavailable);
            }
        });

        group.MapGet("/contracts", async (
            Guid fullWorthSpaceId,
            CurrentUserContext currentUser,
            FullWorthDbContext financeDb,
            CloudIntelligenceStateService cloudState,
            CloudInstanceCredentialStore credentials,
            IFullWorthCloudClient cloud,
            CancellationToken ct) =>
        {
            var userId = currentUser.RequireUserId();
            var isMember = await financeDb.FullWorthSpaceMembers.AsNoTracking().AnyAsync(x =>
                x.FullWorthSpaceId == fullWorthSpaceId && x.UserId == userId, ct);
            if (!isMember) return Results.NotFound();

            if (!await cloudState.HasCurrentActiveConsentAsync(ct))
                return Results.Ok(new { available = false, items = Array.Empty<object>() });

            var state = await cloudState.GetEnabledStateAsync(ct);
            if (state is null)
                return Results.Ok(new { available = false, items = Array.Empty<object>() });

            var secret = await credentials.GetSecretAsync(state.InstanceId, ct);
            if (string.IsNullOrWhiteSpace(secret))
            {
                try
                {
                    var registration = await cloud.RegisterAsync(
                        state.InstanceId,
                        CloudIntelligencePolicy.CurrentVersion,
                        typeof(CloudBenchmarkEndpoints).Assembly.GetName().Version?.ToString() ?? "unknown",
                        ct);
                    await credentials.SaveAsync(registration, ct);
                    secret = registration.Credential;
                    await cloudState.SetTransportStatusAsync(
                        state.InstanceId, null, registration.EntitlementStatus,
                        DateTimeOffset.UtcNow, null, ct);
                }
                catch (FullWorthCloudException)
                {
                    return Results.Ok(new { available = false, items = Array.Empty<object>() });
                }
            }

            var rows = await financeDb.Contracts.AsNoTracking()
                .Where(x => x.FullWorthSpaceId == fullWorthSpaceId &&
                            x.IsActive &&
                            x.CategoryId != null &&
                            x.Amount != 0m &&
                            (x.AccountId == null || financeDb.AccountOwners.Any(owner =>
                                owner.AccountId == x.AccountId && owner.UserId == userId)))
                .Select(x => new
                {
                    x.Amount,
                    x.Currency,
                    x.BillingCycle,
                    x.Interval,
                    CategoryKey = financeDb.Categories
                        .Where(category => category.Id == x.CategoryId &&
                                           category.FullWorthSpaceId == fullWorthSpaceId &&
                                           !category.IsArchived)
                        .Select(category => category.Key)
                        .FirstOrDefault()
                })
                .ToListAsync(ct);

            var local = rows.Select(x =>
                {
                    var metricKey = CloudContractBenchmarkContributionService.MetricForCategory(x.CategoryKey);
                    var currency = NormalizeCurrency(x.Currency);
                    if (metricKey is null || currency is null) return null;
                    var monthly = Math.Abs(x.Amount) *
                                  ContractCycle.PeriodsPerYear(x.BillingCycle, x.Interval) / 12m;
                    return monthly is > 0m and <= 1_000_000m
                        ? new LocalContractBenchmark(metricKey, currency, monthly)
                        : null;
                })
                .Where(x => x is not null)
                .Cast<LocalContractBenchmark>()
                .GroupBy(x => new { x.MetricKey, x.Currency })
                .Select(g => new
                {
                    g.Key.MetricKey,
                    g.Key.Currency,
                    LocalMedian = Math.Round(Median(g.Select(x => x.MonthlyValue)), 2)
                })
                .ToList();

            var observedMonth = DateTimeOffset.UtcNow.ToString("yyyy-MM");
            var items = new List<object>();
            foreach (var item in local)
            {
                try
                {
                    var aggregate = await cloud.GetBenchmarkAsync(
                        secret,
                        item.MetricKey,
                        item.Currency,
                        null,
                        null,
                        null,
                        null,
                        null,
                        observedMonth,
                        ct);
                    if (aggregate is null) continue;

                    items.Add(new
                    {
                        item.MetricKey,
                        item.Currency,
                        item.LocalMedian,
                        aggregate.ObservationCount,
                        aggregate.DistinctInstanceCount,
                        aggregate.Median,
                        aggregate.Mean,
                        aggregate.P25,
                        aggregate.P75
                    });
                }
                catch (FullWorthCloudException)
                {
                    // Comparison UI is optional. One unavailable metric must not break the contracts page.
                }
            }

            return Results.Ok(new
            {
                available = items.Count > 0,
                observedMonth,
                items
            });
        });

        group.MapGet("/savings", async (
            Guid fullWorthSpaceId,
            CurrentUserContext currentUser,
            FullWorthDbContext financeDb,
            CloudSavingsBenchmarkContributionService localSavings,
            CloudIntelligenceStateService cloudState,
            CloudInstanceCredentialStore credentials,
            IFullWorthCloudClient cloud,
            CancellationToken ct) =>
        {
            var userId = currentUser.RequireUserId();
            var isMember = await financeDb.FullWorthSpaceMembers.AsNoTracking().AnyAsync(x =>
                x.FullWorthSpaceId == fullWorthSpaceId && x.UserId == userId, ct);
            if (!isMember) return Results.NotFound();

            var local = await localSavings.ComputeSpaceAsync(
                fullWorthSpaceId,
                DateTimeOffset.UtcNow,
                ct);
            if (local is null)
                return Results.Ok(new { available = false });

            if (!await cloudState.HasCurrentActiveConsentAsync(ct))
                return Results.Ok(new
                {
                    available = false,
                    localSavingsRate = local.SavingsRate,
                    local.ObservedMonth
                });

            var state = await cloudState.GetEnabledStateAsync(ct);
            if (state is null)
                return Results.Ok(new
                {
                    available = false,
                    localSavingsRate = local.SavingsRate,
                    local.ObservedMonth
                });

            var secret = await credentials.GetSecretAsync(state.InstanceId, ct);
            if (string.IsNullOrWhiteSpace(secret))
            {
                try
                {
                    var registration = await cloud.RegisterAsync(
                        state.InstanceId,
                        CloudIntelligencePolicy.CurrentVersion,
                        typeof(CloudBenchmarkEndpoints).Assembly.GetName().Version?.ToString() ?? "unknown",
                        ct);
                    await credentials.SaveAsync(registration, ct);
                    secret = registration.Credential;
                }
                catch (FullWorthCloudException)
                {
                    return Results.Ok(new
                    {
                        available = false,
                        localSavingsRate = local.SavingsRate,
                        local.ObservedMonth
                    });
                }
            }

            FullWorthCloudBenchmark? aggregate = null;
            var peerFilter = "all";
            try
            {
                var incomeBand = local.IncomeBand == "unknown" ? null : local.IncomeBand;
                aggregate = await cloud.GetBenchmarkAsync(
                    secret,
                    "savings.rate",
                    null,
                    local.Country,
                    null,
                    null,
                    incomeBand,
                    null,
                    local.ObservedMonth,
                    ct);
                if (aggregate is not null)
                    peerFilter = SavingsPeerFilter(aggregate);
                else if (local.Country is not null)
                {
                    aggregate = await cloud.GetBenchmarkAsync(
                        secret,
                        "savings.rate",
                        null,
                        local.Country,
                        null,
                        null,
                        null,
                        null,
                        local.ObservedMonth,
                        ct);
                    if (aggregate is not null) peerFilter = SavingsPeerFilter(aggregate);
                }
                if (aggregate is null)
                {
                    aggregate = await cloud.GetBenchmarkAsync(
                        secret,
                        "savings.rate",
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        local.ObservedMonth,
                        ct);
                    if (aggregate is not null) peerFilter = SavingsPeerFilter(aggregate);
                }
            }
            catch (FullWorthCloudException)
            {
                aggregate = null;
            }

            if (aggregate is null)
                return Results.Ok(new
                {
                    available = false,
                    localSavingsRate = local.SavingsRate,
                    local.ObservedMonth
                });

            return Results.Ok(new
            {
                available = true,
                localSavingsRate = local.SavingsRate,
                local.ObservedMonth,
                peerFilter,
                aggregate.ObservationCount,
                aggregate.DistinctInstanceCount,
                aggregate.Median,
                aggregate.Mean,
                aggregate.P25,
                aggregate.P75
            });
        });

        return app;
    }

    private static string SavingsPeerFilter(FullWorthCloudBenchmark aggregate)
    {
        if (!string.IsNullOrWhiteSpace(aggregate.Country) &&
            !string.IsNullOrWhiteSpace(aggregate.IncomeBand))
            return "country_income";
        if (!string.IsNullOrWhiteSpace(aggregate.IncomeBand))
            return "income";
        if (!string.IsNullOrWhiteSpace(aggregate.Country))
            return "country";
        return "all";
    }

    private static string? NormalizeCurrency(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim().ToUpperInvariant();
        return normalized.Length == 3 && normalized.All(char.IsAsciiLetter)
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

    private sealed record LocalContractBenchmark(
        string MetricKey,
        string Currency,
        decimal MonthlyValue);
}
