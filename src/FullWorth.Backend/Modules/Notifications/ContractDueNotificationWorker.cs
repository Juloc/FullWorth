using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Notifications;

/// <summary>
/// Scans for recurring contracts due within a short horizon and dispatches a once-only "contract due"
/// notification to each recipient (the space members, or just the owning account's owners when the
/// contract is bound to an account). Idempotent per due-occurrence via the dedup key.
/// </summary>
public sealed class ContractDueNotificationService(FullWorthDbContext db, NotificationDispatcher dispatcher)
{
    public async Task ScanAndDispatchAsync(DateOnly today, int horizonDays, CancellationToken ct)
    {
        var horizon = today.AddDays(horizonDays);
        var due = await db.Contracts.AsNoTracking()
            .Where(c => c.IsActive
                && c.NextDueDate != null
                && c.NextDueDate >= today
                && c.NextDueDate <= horizon
                && (c.EndDate == null || c.EndDate >= today))
            .Select(c => new { c.Id, c.FullWorthSpaceId, c.Name, c.AccountId, c.NextDueDate })
            .ToListAsync(ct);

        foreach (var contract in due)
        {
            var dueDate = contract.NextDueDate!.Value;
            // Recipients: an unbound contract is visible to every member; an account-bound one only to that
            // account's owners/viewers. Split the branches to keep the query trivially translatable.
            var recipients = contract.AccountId is null
                ? await db.FullWorthSpaceMembers.AsNoTracking()
                    .Where(m => m.FullWorthSpaceId == contract.FullWorthSpaceId).Select(m => m.UserId).ToListAsync(ct)
                : await db.AccountOwners.AsNoTracking()
                    .Where(o => o.AccountId == contract.AccountId.Value).Select(o => o.UserId).ToListAsync(ct);

            var message = NotificationMessages.ContractDue(contract.Name, dueDate);
            var dedupKey = $"contract:{contract.Id}:{dueDate:yyyy-MM-dd}";
            foreach (var userId in recipients)
                await dispatcher.DispatchAsync(userId, contract.FullWorthSpaceId, NotificationTypes.ContractDue, message, dedupKey, ct);
        }
    }
}

public sealed class ContractDueNotificationWorker(IServiceScopeFactory scopes, ILogger<ContractDueNotificationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var today = DateOnly.FromDateTime(DateTime.UtcNow);
                await scope.ServiceProvider.GetRequiredService<ContractDueNotificationService>().ScanAndDispatchAsync(today, 3, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex) { logger.LogError(ex, "Contract-due notification scan failed."); }

            await Task.Delay(TimeSpan.FromHours(24), stoppingToken);
        }
    }
}
