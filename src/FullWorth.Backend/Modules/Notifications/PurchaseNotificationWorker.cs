using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Notifications;

/// <summary>
/// Purchase-specific push notifications. Every alert is idempotent through NotificationDispatcher's
/// dedup table. New purchases are sent only to their creator. Legacy rows without a creator are skipped
/// rather than broadening visibility to all FullWorth-Space members.
/// </summary>
public sealed class PurchaseNotificationService(FullWorthDbContext db, NotificationDispatcher dispatcher)
{
    public async Task ScanAndDispatchAsync(DateTimeOffset now, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        await DispatchFailedScansAsync(now, ct);
        await DispatchReviewAndUnmatchedAsync(now, ct);
        await DispatchDeadlinesAsync(today, ct);
    }

    private async Task DispatchFailedScansAsync(DateTimeOffset now, CancellationToken ct)
    {
        var cutoff = now.AddDays(-7);
        var failed = await db.Database.SqlQuery<FailedScanRow>($"""
            SELECT "Id", "FullWorthSpaceId", "UserId", "PurchaseId", "Attempts", "UpdatedAt"
            FROM "ReceiptScanJobs"
            WHERE "Status" = 'error' AND "UpdatedAt" >= {cutoff}
            ORDER BY "UpdatedAt"
            """).ToListAsync(ct);

        foreach (var job in failed)
        {
            var key = $"scan:{job.Id}:attempt:{job.Attempts}";
            await dispatcher.DispatchAsync(
                job.UserId,
                job.FullWorthSpaceId,
                NotificationTypes.PurchaseScanFailed,
                NotificationMessages.PurchaseScanFailed(),
                key,
                ct);
        }
    }

    private async Task DispatchReviewAndUnmatchedAsync(DateTimeOffset now, CancellationToken ct)
    {
        // Avoid buzzing users for old migration/backfill rows immediately after this feature ships.
        var recent = now.AddDays(-14);
        var unmatchedBefore = now.AddHours(-24);
        var rows = await db.Purchases.AsNoTracking()
            .Where(x => x.CreatedByUserId.HasValue)
            .Where(x => x.CreatedAt >= recent && (x.Source == "receipt" || x.Source == "amazon"))
            .Where(x => x.ReviewState != "confirmed" ||
                (x.CreatedAt <= unmatchedBefore && x.TransactionId == null && !x.PaymentLinks.Any()))
            .Select(x => new PurchaseAttentionRow(
                x.Id,
                x.FullWorthSpaceId,
                x.CreatedByUserId,
                x.Merchant,
                x.ReviewState,
                x.CreatedAt,
                x.TransactionId != null || x.PaymentLinks.Any()))
            .ToListAsync(ct);

        foreach (var purchase in rows)
        {
            var recipients = await RecipientsAsync(purchase.FullWorthSpaceId, purchase.CreatedByUserId, ct);
            if (purchase.ReviewState != "confirmed")
            {
                foreach (var userId in recipients)
                    await dispatcher.DispatchAsync(
                        userId,
                        purchase.FullWorthSpaceId,
                        NotificationTypes.PurchaseReview,
                        NotificationMessages.PurchaseReview(purchase.Merchant),
                        $"purchase-review:{purchase.Id}",
                        ct);
            }

            if (!purchase.Linked && purchase.CreatedAt <= unmatchedBefore)
            {
                foreach (var userId in recipients)
                    await dispatcher.DispatchAsync(
                        userId,
                        purchase.FullWorthSpaceId,
                        NotificationTypes.PurchaseUnmatched,
                        NotificationMessages.PurchaseUnmatched(purchase.Merchant),
                        $"purchase-unmatched:{purchase.Id}",
                        ct);
            }
        }
    }

    private async Task DispatchDeadlinesAsync(DateOnly today, CancellationToken ct)
    {
        var returnThrough = today.AddDays(3);
        var warrantyThrough = today.AddDays(30);
        var rows = await db.PurchaseItems.AsNoTracking()
            .Where(x => x.Purchase.CreatedByUserId.HasValue)
            .Where(x =>
                (x.ReturnDeadline.HasValue && x.ReturnDeadline.Value >= today && x.ReturnDeadline.Value <= returnThrough) ||
                (x.WarrantyEnd.HasValue && x.WarrantyEnd.Value >= today && x.WarrantyEnd.Value <= warrantyThrough))
            .Select(x => new DeadlineRow(
                x.Id,
                x.PurchaseId,
                x.Purchase.FullWorthSpaceId,
                x.Purchase.CreatedByUserId,
                x.Name,
                x.ReturnDeadline,
                x.WarrantyEnd))
            .ToListAsync(ct);

        foreach (var row in rows)
        {
            var recipients = await RecipientsAsync(row.FullWorthSpaceId, row.CreatedByUserId, ct);
            if (row.ReturnDeadline is { } returnDeadline && returnDeadline <= returnThrough)
            {
                foreach (var userId in recipients)
                    await dispatcher.DispatchAsync(
                        userId,
                        row.FullWorthSpaceId,
                        NotificationTypes.PurchaseReturnDeadline,
                        NotificationMessages.PurchaseReturnDeadline(row.Name, returnDeadline),
                        $"return:{row.ItemId}:{returnDeadline:yyyy-MM-dd}",
                        ct);
            }
            if (row.WarrantyEnd is { } warrantyEnd && warrantyEnd <= warrantyThrough)
            {
                foreach (var userId in recipients)
                    await dispatcher.DispatchAsync(
                        userId,
                        row.FullWorthSpaceId,
                        NotificationTypes.PurchaseWarrantyDeadline,
                        NotificationMessages.PurchaseWarrantyDeadline(row.Name, warrantyEnd),
                        $"warranty:{row.ItemId}:{warrantyEnd:yyyy-MM-dd}",
                        ct);
            }
        }
    }

    private Task<List<Guid>> RecipientsAsync(Guid fullWorthSpaceId, Guid? createdByUserId, CancellationToken ct)
    {
        if (!createdByUserId.HasValue) return Task.FromResult(new List<Guid>());
        return db.FullWorthSpaceMembers.AsNoTracking()
            .Where(x => x.FullWorthSpaceId == fullWorthSpaceId && x.UserId == createdByUserId.Value)
            .Select(x => x.UserId)
            .ToListAsync(ct);
    }

    public sealed record FailedScanRow(Guid Id, Guid FullWorthSpaceId, Guid UserId, Guid PurchaseId, int Attempts, DateTimeOffset UpdatedAt);
    private sealed record PurchaseAttentionRow(Guid Id, Guid FullWorthSpaceId, Guid? CreatedByUserId, string Merchant, string ReviewState, DateTimeOffset CreatedAt, bool Linked);
    private sealed record DeadlineRow(Guid ItemId, Guid PurchaseId, Guid FullWorthSpaceId, Guid? CreatedByUserId, string Name, DateOnly? ReturnDeadline, DateOnly? WarrantyEnd);
}

public sealed class PurchaseNotificationWorker(IServiceScopeFactory scopes, ILogger<PurchaseNotificationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<PurchaseNotificationService>()
                    .ScanAndDispatchAsync(DateTimeOffset.UtcNow, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex) { logger.LogError(ex, "Purchase notification scan failed."); }

            await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
        }
    }
}
