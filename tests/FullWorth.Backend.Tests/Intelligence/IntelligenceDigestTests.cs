using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Intelligence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Intelligence;

public sealed class IntelligenceDigestTests
{
    [Fact]
    public void Resolve_period_uses_stable_daily_weekly_monthly_keys()
    {
        var now = new DateTimeOffset(2026, 9, 2, 8, 0, 0, TimeSpan.Zero); // Wednesday

        var daily = IntelligenceDigestService.ResolvePeriod(ScheduledIntelligenceJobTypes.DailyIncremental, now);
        var weekly = IntelligenceDigestService.ResolvePeriod(ScheduledIntelligenceJobTypes.WeeklyDeep, now);
        var monthly = IntelligenceDigestService.ResolvePeriod(ScheduledIntelligenceJobTypes.MonthlyReview, now);

        Assert.Equal("2026-09-02", daily.Key);
        Assert.Equal(new DateTimeOffset(2026, 9, 2, 0, 0, 0, TimeSpan.Zero), daily.Start);
        Assert.Equal("2026-08-31", weekly.Key);
        Assert.Equal(new DateTimeOffset(2026, 8, 31, 0, 0, 0, TimeSpan.Zero), weekly.Start);
        Assert.Equal("2026-09", monthly.Key);
        Assert.Equal(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), monthly.Start);
    }

    [Fact]
    public async Task Build_is_idempotent_and_aggregates_runs_and_suggestions()
    {
        await using var intelligenceConnection = new SqliteConnection("Data Source=:memory:");
        await using var financeConnection = new SqliteConnection("Data Source=:memory:");
        await intelligenceConnection.OpenAsync();
        await financeConnection.OpenAsync();

        await using var intelligenceDb = new IntelligenceDbContext(
            new DbContextOptionsBuilder<IntelligenceDbContext>().UseSqlite(intelligenceConnection).Options);
        await using var financeDb = new FullWorthDbContext(
            new DbContextOptionsBuilder<FullWorthDbContext>().UseSqlite(financeConnection).Options);
        await intelligenceDb.Database.EnsureCreatedAsync();
        await financeDb.Database.EnsureCreatedAsync();

        var space = new FullWorthSpace { Name = "Digest", BaseCurrency = "EUR" };
        financeDb.FullWorthSpaces.Add(space);
        await financeDb.SaveChangesAsync();

        var now = new DateTimeOffset(2026, 9, 2, 8, 0, 0, TimeSpan.Zero);
        intelligenceDb.IntelligenceSuggestions.Add(new IntelligenceSuggestion
        {
            FullWorthSpaceId = space.Id,
            Type = "merchant-category",
            SubjectType = "merchant",
            SubjectId = "REWE",
            SemanticKey = "merchant-category:expense",
            Provider = "fake",
            Model = "fake",
            Confidence = .9m,
            CreatedAt = now.AddMinutes(-5)
        });
        intelligenceDb.AiRuns.Add(new AiRun
        {
            FullWorthSpaceId = space.Id,
            Provider = "fake",
            Model = "fake",
            Capability = "text-classification",
            JobType = ScheduledIntelligenceJobTypes.DailyIncremental,
            Status = AiRunStatuses.Succeeded,
            StartedAt = now.AddMinutes(-10),
            CompletedAt = now.AddMinutes(-9),
            InputItemCount = 1,
            OutputItemCount = 1,
            EstimatedCostEur = .01m
        });
        await intelligenceDb.SaveChangesAsync();

        var service = new IntelligenceDigestService(intelligenceDb, financeDb);
        var first = await service.BuildAsync(ScheduledIntelligenceJobTypes.DailyIncremental, space.Id, now, CancellationToken.None);
        var second = await service.BuildAsync(ScheduledIntelligenceJobTypes.DailyIncremental, space.Id, now, CancellationToken.None);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal("2026-09-02", second.PeriodKey);
        Assert.Contains("\"created\":1", second.SummaryJson, StringComparison.Ordinal);
        Assert.Contains("\"runs\":1", second.SummaryJson, StringComparison.Ordinal);
        Assert.Single(await intelligenceDb.IntelligenceDigests.AsNoTracking().ToListAsync());
    }
}
