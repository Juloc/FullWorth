using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Intelligence;

/// <summary>
/// Queues minimized product-price observations for confirmed purchase items created/updated after the
/// current Cloud Intelligence consent. No automatic historical backfill is performed.
/// </summary>
public sealed class CloudProductPriceContributionService(
    FullWorthDbContext financeDb,
    IntelligenceDbContext intelligenceDb,
    CloudIntelligenceStateService cloudState)
{
    private const int MaximumItemsPerPass = 2_000;

    public async Task<int> QueueCurrentAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (!await cloudState.HasCurrentActiveConsentAsync(ct))
            return 0;

        var state = await cloudState.GetEnabledStateAsync(ct);
        if (state is null)
            return 0;

        var view = await cloudState.GetAsync(ct);
        if (view.AcceptedAt is null ||
            !string.Equals(view.AcceptedPolicyVersion, CloudIntelligencePolicy.CurrentVersion, StringComparison.Ordinal))
            return 0;

        var acceptedAt = view.AcceptedAt.Value;
        var rows = await financeDb.PurchaseItems.AsNoTracking()
            .Where(x =>
                x.Purchase.Status == "confirmed" &&
                x.LineType == "product" &&
                x.Barcode != null &&
                x.Barcode != "" &&
                (x.UpdatedAt >= acceptedAt || x.Purchase.UpdatedAt >= acceptedAt))
            .OrderBy(x => x.UpdatedAt)
            .Take(MaximumItemsPerPass)
            .Select(x => new
            {
                x.Id,
                x.Barcode,
                x.UnitPrice,
                x.BaseUnitPrice,
                x.Quantity,
                x.TotalPrice,
                x.Currency,
                x.UpdatedAt,
                x.Purchase.FullWorthSpaceId,
                x.Purchase.PurchaseDate,
                x.Purchase.CreatedAt
            })
            .ToListAsync(ct);

        if (rows.Count == 0)
            return 0;

        var countries = await LoadSpaceCountriesAsync(
            rows.Select(x => x.FullWorthSpaceId).Distinct().ToArray(),
            ct);

        var queued = 0;
        foreach (var row in rows)
        {
            if (!GtinKey.TryCreateGtinSubjectKey(row.Barcode, out var productKey) ||
                string.IsNullOrWhiteSpace(productKey))
                continue;

            var price = EffectiveUnitPrice(
                row.UnitPrice,
                row.BaseUnitPrice,
                row.Quantity,
                row.TotalPrice);
            if (price is null or <= 0m or > 1_000_000m)
                continue;

            var currency = NormalizeCurrency(row.Currency);
            if (currency is null)
                continue;

            var observedDate = row.PurchaseDate ??
                               DateOnly.FromDateTime(row.CreatedAt.UtcDateTime);
            var observedMonth = observedDate.ToString("yyyy-MM");
            countries.TryGetValue(row.FullWorthSpaceId, out var country);

            var idempotencyKey = StableObservationKey(row.FullWorthSpaceId, row.Id);
            var payload = JsonSerializer.Serialize(new
            {
                productKey,
                unitPrice = Math.Round(price.Value, 4),
                currency,
                country,
                observedMonth,
                source = "purchase"
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
                    EventType = "price_observation",
                    PayloadJson = payload,
                    Status = CloudSubmissionStatuses.Queued,
                    CreatedAt = now
                });
                queued++;
            }
            else if (existing.Status is CloudSubmissionStatuses.Queued or CloudSubmissionStatuses.Failed)
            {
                // Corrections made before transmission replace the queued value. Once sent, this
                // purchase item is never uploaded a second time, preventing duplicate historical weight.
                existing.PayloadJson = payload;
                existing.Status = CloudSubmissionStatuses.Queued;
                existing.NextAttemptAt = null;
                existing.ErrorCode = null;
            }
        }

        await intelligenceDb.SaveChangesAsync(ct);
        return queued;
    }

    private async Task<Dictionary<Guid, string?>> LoadSpaceCountriesAsync(
        Guid[] spaceIds,
        CancellationToken ct)
    {
        var rows = await financeDb.BankConnections.AsNoTracking()
            .Where(x => spaceIds.Contains(x.FullWorthSpaceId) &&
                        x.Country != null &&
                        x.Country != "")
            .Select(x => new { x.FullWorthSpaceId, x.Country })
            .Distinct()
            .ToListAsync(ct);

        return spaceIds.ToDictionary(
            id => id,
            id =>
            {
                var values = rows.Where(x => x.FullWorthSpaceId == id)
                    .Select(x => NormalizeCountry(x.Country))
                    .Where(x => x is not null)
                    .Cast<string>()
                    .Distinct(StringComparer.Ordinal)
                    .Take(2)
                    .ToList();
                return values.Count == 1 ? values[0] : null;
            });
    }

    private static decimal? EffectiveUnitPrice(
        decimal? unitPrice,
        decimal? baseUnitPrice,
        decimal quantity,
        decimal totalPrice)
    {
        if (unitPrice is > 0m)
            return unitPrice;
        if (baseUnitPrice is > 0m)
            return baseUnitPrice;
        if (quantity > 0m && totalPrice > 0m)
            return totalPrice / quantity;
        return null;
    }

    private static string StableObservationKey(Guid fullWorthSpaceId, Guid purchaseItemId)
    {
        var bytes = Encoding.UTF8.GetBytes(
            $"fullworth:price-observation:v1:{fullWorthSpaceId:N}:{purchaseItemId:N}");
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return $"price:{hash[..48]}";
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
}

public sealed class CloudProductPriceContributionWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<CloudProductPriceContributionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var queued = await scope.ServiceProvider
                    .GetRequiredService<CloudProductPriceContributionService>()
                    .QueueCurrentAsync(DateTimeOffset.UtcNow, stoppingToken);
                if (queued > 0)
                    logger.LogInformation(
                        "Queued {Count} privacy-safe product price observation(s).",
                        queued);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "FullWorth Cloud product price contribution cycle failed.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromHours(6), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
