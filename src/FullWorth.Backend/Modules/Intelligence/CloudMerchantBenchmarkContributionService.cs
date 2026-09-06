using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Intelligence;

/// <summary>
/// Contributes one privacy-safe previous-month net-spend observation per instance/canonical merchant/currency.
/// Only canonical merchant keys from the verified signed knowledge pack leave the instance. Individual
/// transactions, local merchant ids, raw counterparties and per-transaction amounts are never submitted.
/// </summary>
public sealed class CloudMerchantBenchmarkContributionService(
    FullWorthDbContext financeDb,
    IntelligenceDbContext intelligenceDb,
    CloudIntelligenceStateService cloudState,
    CloudOperationalRegistryResolver registryResolver)
{
    public const string MetricKey = "spending.merchant.monthly";

    public async Task<int> QueuePreviousMonthAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (!await cloudState.HasCurrentActiveConsentAsync(ct))
            return 0;

        var state = await cloudState.GetEnabledStateAsync(ct);
        if (state is null)
            return 0;

        var currentMonth = new DateOnly(now.Year, now.Month, 1);
        var monthStart = currentMonth.AddMonths(-1);
        var monthEnd = currentMonth;

        var rows = await financeDb.Transactions.AsNoTracking()
            .Join(
                financeDb.Accounts.AsNoTracking(),
                transaction => transaction.AccountId,
                account => account.Id,
                (transaction, account) => new
                {
                    transaction.Id,
                    transaction.RefundOfTransactionId,
                    transaction.Amount,
                    transaction.Currency,
                    transaction.NormalizedCounterparty,
                    transaction.IsIgnored,
                    transaction.IsTransfer,
                    Date = transaction.BookingDate ?? transaction.ValueDate,
                    account.FullWorthSpaceId
                })
            .Where(x =>
                x.Date != null &&
                x.Date >= monthStart &&
                x.Date < monthEnd &&
                !x.IsIgnored &&
                !x.IsTransfer &&
                (x.Amount < 0m || (x.Amount > 0m && x.RefundOfTransactionId != null)))
            .ToListAsync(ct);

        if (rows.Count == 0)
            return 0;

        var refundOriginalIds = rows
            .Where(x => x.Amount > 0m && x.RefundOfTransactionId.HasValue)
            .Select(x => x.RefundOfTransactionId!.Value)
            .Distinct()
            .ToArray();

        var originals = refundOriginalIds.Length == 0
            ? new Dictionary<Guid, string?>()
            : await financeDb.Transactions.AsNoTracking()
                .Where(x => refundOriginalIds.Contains(x.Id))
                .Select(x => new { x.Id, x.NormalizedCounterparty })
                .ToDictionaryAsync(x => x.Id, x => x.NormalizedCounterparty, ct);

        var countryBySpace = await LoadSpaceCountriesAsync(ct);
        var identityCache = new Dictionary<(string Alias, string Country), CloudMerchantIdentity?>();
        var resolvedRows = new List<MerchantSpendSource>();

        foreach (var row in rows)
        {
            var alias = row.Amount > 0m && row.RefundOfTransactionId.HasValue
                ? originals.GetValueOrDefault(row.RefundOfTransactionId.Value)
                : row.NormalizedCounterparty;
            if (string.IsNullOrWhiteSpace(alias))
                continue;

            var country = countryBySpace.GetValueOrDefault(row.FullWorthSpaceId);
            var normalizedCountry = country ?? "GLOBAL";
            var cacheKey = (alias, normalizedCountry);
            if (!identityCache.TryGetValue(cacheKey, out var merchant))
            {
                merchant = await registryResolver.ResolveMerchantAsync(
                    alias,
                    country,
                    "expense",
                    ct);
                identityCache[cacheKey] = merchant;
            }
            if (merchant is null)
                continue;

            var currency = NormalizeCurrency(row.Currency);
            if (currency is null)
                continue;

            // Ledger convention: expenses are negative, refunds positive. Negating yields net spend.
            resolvedRows.Add(new MerchantSpendSource(
                CloudBenchmarkEntityKeys.ForMerchant(merchant.MerchantKey),
                currency,
                -row.Amount,
                country));
        }

        var observedMonth = monthStart.ToString("yyyy-MM");
        var queued = 0;

        foreach (var group in resolvedRows.GroupBy(x => new { x.EntityKey, x.Currency }))
        {
            var value = Math.Round(group.Sum(x => x.NetSpend), 2);
            if (value is <= 0m or > 1_000_000m)
                continue;

            var countries = group
                .Select(x => x.Country)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var country = countries.Count == 1 ? countries[0] : null;

            var payload = JsonSerializer.Serialize(new
            {
                metricKey = MetricKey,
                entityKey = group.Key.EntityKey,
                value,
                currency = group.Key.Currency,
                country,
                observedMonth
            });
            var idempotencyKey = IdempotencyKey(
                group.Key.EntityKey,
                group.Key.Currency,
                observedMonth,
                value,
                country);

            if (await intelligenceDb.CloudSubmissionOutbox.AsNoTracking()
                    .AnyAsync(x => x.IdempotencyKey == idempotencyKey, ct))
                continue;

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

        if (queued > 0)
            await intelligenceDb.SaveChangesAsync(ct);
        return queued;
    }

    private async Task<Dictionary<Guid, string?>> LoadSpaceCountriesAsync(CancellationToken ct)
    {
        var rows = await financeDb.BankConnections.AsNoTracking()
            .Where(x => x.Country != null && x.Country != "")
            .Select(x => new { x.FullWorthSpaceId, x.Country })
            .ToListAsync(ct);

        return rows
            .GroupBy(x => x.FullWorthSpaceId)
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    var countries = group
                        .Select(x => NormalizeCountry(x.Country))
                        .Where(x => x is not null)
                        .Distinct(StringComparer.Ordinal)
                        .ToList();
                    return countries.Count == 1 ? countries[0] : null;
                });
    }

    private static string IdempotencyKey(
        string merchantKey,
        string currency,
        string observedMonth,
        decimal value,
        string? country)
    {
        var material = Encoding.UTF8.GetBytes(
            $"fullworth:merchant-benchmark:v1:{merchantKey}:{currency}:{observedMonth}:{value:0.00}:{country ?? "GLOBAL"}");
        var hash = Convert.ToHexString(SHA256.HashData(material)).ToLowerInvariant();
        return $"benchmark-merchant:{hash[..48]}";
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

    private sealed record MerchantSpendSource(
        string EntityKey,
        string Currency,
        decimal NetSpend,
        string? Country);
}

public sealed class CloudMerchantBenchmarkContributionWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<CloudMerchantBenchmarkContributionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var queued = await scope.ServiceProvider
                    .GetRequiredService<CloudMerchantBenchmarkContributionService>()
                    .QueuePreviousMonthAsync(DateTimeOffset.UtcNow, stoppingToken);
                if (queued > 0)
                    logger.LogInformation(
                        "Queued {Count} privacy-safe merchant benchmark observation(s).",
                        queued);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "FullWorth Cloud merchant benchmark contribution cycle failed.");
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
