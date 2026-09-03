using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Intelligence;

public sealed record AiBudgetDecision(bool Allowed, string? Reason, decimal TodaySpentEur, decimal MonthSpentEur, decimal? DailyRemainingEur, decimal? MonthlyRemainingEur, decimal? EstimatedCallCostEur);

public sealed class AiBudgetGuard(IntelligenceDbContext db)
{
    public async Task<AiBudgetDecision> CheckAsync(decimal? estimatedCallCostEur, CancellationToken ct)
    {
        if (estimatedCallCostEur < 0m)
            throw new ArgumentOutOfRangeException(nameof(estimatedCallCostEur));

        var settings = await db.AiInstanceSettings.AsNoTracking().SingleOrDefaultAsync(
            x => x.ScopeKey == AiInstanceSettings.InstanceScopeKey, ct);
        if (settings is null || !settings.Enabled)
            return new(false, "ai_disabled", 0m, 0m, settings?.DailyBudgetEur, settings?.MonthlyBudgetEur, estimatedCallCostEur);

        var now = DateTimeOffset.UtcNow;
        var dayStart = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        var monthStart = new DateTimeOffset(new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc));
        var todayCosts = await db.AiRuns.AsNoTracking().Where(x => x.StartedAt >= dayStart)
            .Select(x => x.ActualCostEur ?? x.EstimatedCostEur ?? 0m).ToListAsync(ct);
        var monthCosts = await db.AiRuns.AsNoTracking().Where(x => x.StartedAt >= monthStart)
            .Select(x => x.ActualCostEur ?? x.EstimatedCostEur ?? 0m).ToListAsync(ct);
        var todaySpent = todayCosts.Sum();
        var monthSpent = monthCosts.Sum();
        decimal? dailyRemaining = settings.DailyBudgetEur.HasValue ? Math.Max(0m, settings.DailyBudgetEur.Value - todaySpent) : null;
        decimal? monthlyRemaining = settings.MonthlyBudgetEur.HasValue ? Math.Max(0m, settings.MonthlyBudgetEur.Value - monthSpent) : null;

        if ((settings.DailyBudgetEur.HasValue || settings.MonthlyBudgetEur.HasValue) && !estimatedCallCostEur.HasValue)
            return new(false, "cost_estimate_required", todaySpent, monthSpent, dailyRemaining, monthlyRemaining, null);

        var estimate = estimatedCallCostEur ?? 0m;
        if (settings.DailyBudgetEur.HasValue && todaySpent + estimate > settings.DailyBudgetEur.Value)
            return new(false, "daily_budget_exceeded", todaySpent, monthSpent, dailyRemaining, monthlyRemaining, estimate);
        if (settings.MonthlyBudgetEur.HasValue && monthSpent + estimate > settings.MonthlyBudgetEur.Value)
            return new(false, "monthly_budget_exceeded", todaySpent, monthSpent, dailyRemaining, monthlyRemaining, estimate);
        return new(true, null, todaySpent, monthSpent, dailyRemaining, monthlyRemaining, estimate);
    }

    public async Task RecordEstimateAsync(Guid runId, decimal estimatedCallCostEur, CancellationToken ct)
    {
        if (estimatedCallCostEur < 0m) throw new ArgumentOutOfRangeException(nameof(estimatedCallCostEur));
        var run = await db.AiRuns.SingleAsync(x => x.Id == runId, ct);
        run.EstimatedCostEur = estimatedCallCostEur;
        await db.SaveChangesAsync(ct);
    }
}
