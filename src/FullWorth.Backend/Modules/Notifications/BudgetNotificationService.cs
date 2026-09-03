using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Budgets;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Notifications;

/// <summary>
/// After sync, evaluates each active budget for each recipient with the same advanced scope,
/// split/refund/transfer and FX semantics used by the user-facing budget status. Notifications are
/// suppressed for partial account visibility or incomplete FX so a push can neither leak hidden
/// household activity nor claim an inaccurate threshold.
/// </summary>
public sealed class BudgetNotificationService(
    FullWorthDbContext db,
    BudgetStore budgets,
    NotificationDispatcher dispatcher,
    ILogger<BudgetNotificationService> logger)
{
    private readonly BudgetNotificationReconciliationService reconciliation = new(db);

    public async Task EvaluateAndDispatchAsync(Guid fullWorthSpaceId, DateOnly asOf, CancellationToken ct)
    {
        try
        {
            await EvaluateCoreAsync(fullWorthSpaceId, asOf, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Budget notification evaluation failed for space {SpaceId}", fullWorthSpaceId);
        }
    }

    private async Task EvaluateCoreAsync(Guid fullWorthSpaceId, DateOnly asOf, CancellationToken ct)
    {
        // Keep BudgetStore in the constructor for backwards-compatible DI/tests; active IDs are read
        // directly because threshold evaluation itself now comes from the canonical reconciliation engine.
        _ = budgets;
        var budgetIds = await db.Budgets.AsNoTracking()
            .Where(budget => budget.FullWorthSpaceId == fullWorthSpaceId && budget.IsActive)
            .Select(budget => budget.Id)
            .ToListAsync(ct);
        if (budgetIds.Count == 0) return;

        var members = await db.FullWorthSpaceMembers.AsNoTracking()
            .Where(member => member.FullWorthSpaceId == fullWorthSpaceId)
            .Select(member => member.UserId)
            .ToListAsync(ct);
        if (members.Count == 0) return;

        foreach (var budgetId in budgetIds)
        {
            foreach (var userId in members)
            {
                var signal = await reconciliation.EvaluateForUserAsync(userId, fullWorthSpaceId, budgetId, asOf, ct);
                if (signal is null || signal.PartialAccess || signal.IncompleteFx || signal.PercentUsed < signal.NearThreshold)
                    continue;

                var percent = (int)Math.Floor(signal.PercentUsed);
                var over = signal.PercentUsed >= signal.CriticalThreshold;
                var cycle = signal.PeriodStart.ToString("yyyy-MM-dd");
                var nearKey = $"budget:{signal.BudgetId}:{cycle}:near";
                var overKey = $"budget:{signal.BudgetId}:{cycle}:over";

                if (over)
                {
                    var overEnabled = await dispatcher.DispatchAsync(userId, fullWorthSpaceId, NotificationTypes.BudgetOver,
                        NotificationMessages.BudgetOver(signal.Name, percent), overKey, ct);
                    if (overEnabled)
                    {
                        // "Over" is the alert this cycle — suppress a redundant "near" buzz.
                        await dispatcher.MarkDedupAsync(userId, fullWorthSpaceId, NotificationTypes.BudgetNear, nearKey, ct);
                    }
                    else
                    {
                        // The user disabled "over" but may still want "near" — fall through to it.
                        await dispatcher.DispatchAsync(userId, fullWorthSpaceId, NotificationTypes.BudgetNear,
                            NotificationMessages.BudgetNear(signal.Name, percent), nearKey, ct);
                    }
                }
                else
                {
                    await dispatcher.DispatchAsync(userId, fullWorthSpaceId, NotificationTypes.BudgetNear,
                        NotificationMessages.BudgetNear(signal.Name, percent), nearKey, ct);
                }
            }
        }
    }
}
