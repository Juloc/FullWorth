using FullWorth.Backend.Modules.Intelligence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Intelligence;

public sealed class AiBudgetGuardTests
{
    [Fact]
    public async Task Disabled_ai_blocks_provider_calls_even_without_budget_limits()
    {
        await using var fixture = await Fixture.CreateAsync(enabled: false);
        var decision = await new AiBudgetGuard(fixture.Db).CheckAsync(0.01m, CancellationToken.None);
        Assert.False(decision.Allowed);
        Assert.Equal("ai_disabled", decision.Reason);
    }

    [Fact]
    public async Task Estimated_call_must_fit_daily_budget()
    {
        await using var fixture = await Fixture.CreateAsync(enabled: true, dailyBudget: 1.00m, monthlyBudget: 10m);
        fixture.Db.AiRuns.Add(new AiRun
        {
            Provider = "openai",
            Model = "test",
            Capability = "text-classification",
            JobType = "daily",
            Status = AiRunStatuses.Succeeded,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            CompletedAt = DateTimeOffset.UtcNow.AddMinutes(-9),
            ActualCostEur = 0.80m
        });
        await fixture.Db.SaveChangesAsync();

        var guard = new AiBudgetGuard(fixture.Db);
        var blocked = await guard.CheckAsync(0.25m, CancellationToken.None);
        var allowed = await guard.CheckAsync(0.20m, CancellationToken.None);

        Assert.False(blocked.Allowed);
        Assert.Equal("daily_budget_exceeded", blocked.Reason);
        Assert.True(allowed.Allowed);
        Assert.Equal(0.20m, allowed.DailyRemainingEur);
    }

    [Fact]
    public async Task Estimated_call_must_fit_monthly_budget()
    {
        await using var fixture = await Fixture.CreateAsync(enabled: true, dailyBudget: null, monthlyBudget: 2.00m);
        fixture.Db.AiRuns.Add(new AiRun
        {
            Provider = "openai",
            Model = "test",
            Capability = "text-classification",
            JobType = "weekly",
            Status = AiRunStatuses.Succeeded,
            StartedAt = DateTimeOffset.UtcNow.AddDays(-1),
            CompletedAt = DateTimeOffset.UtcNow.AddDays(-1),
            EstimatedCostEur = 1.80m
        });
        await fixture.Db.SaveChangesAsync();

        var decision = await new AiBudgetGuard(fixture.Db).CheckAsync(0.25m, CancellationToken.None);
        Assert.False(decision.Allowed);
        Assert.Equal("monthly_budget_exceeded", decision.Reason);
        Assert.Equal(0.20m, decision.MonthlyRemainingEur);
    }

    [Fact]
    public async Task Actual_cost_wins_over_estimate_and_new_estimate_is_persisted()
    {
        await using var fixture = await Fixture.CreateAsync(enabled: true, dailyBudget: 2m, monthlyBudget: 20m);
        var run = new AiRun
        {
            Provider = "openai",
            Model = "test",
            Capability = "text-classification",
            JobType = "daily",
            StartedAt = DateTimeOffset.UtcNow,
            EstimatedCostEur = 1.50m,
            ActualCostEur = 0.40m
        };
        fixture.Db.AiRuns.Add(run);
        await fixture.Db.SaveChangesAsync();

        var guard = new AiBudgetGuard(fixture.Db);
        var decision = await guard.CheckAsync(1.00m, CancellationToken.None);
        Assert.True(decision.Allowed);
        Assert.Equal(0.40m, decision.TodaySpentEur);

        await guard.RecordEstimateAsync(run.Id, 0.60m, CancellationToken.None);
        Assert.Equal(0.60m, (await fixture.Db.AiRuns.SingleAsync()).EstimatedCostEur);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        public IntelligenceDbContext Db { get; }

        private Fixture(SqliteConnection connection, IntelligenceDbContext db)
        {
            this.connection = connection;
            Db = db;
        }

        public static async Task<Fixture> CreateAsync(bool enabled, decimal? dailyBudget = null, decimal? monthlyBudget = null)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<IntelligenceDbContext>().UseSqlite(connection).Options;
            var db = new IntelligenceDbContext(options);
            await db.Database.EnsureCreatedAsync();
            db.AiInstanceSettings.Add(new AiInstanceSettings
            {
                Enabled = enabled,
                Provider = "openai",
                DefaultTextModel = "test",
                DefaultVisionModel = "test",
                DailyBudgetEur = dailyBudget,
                MonthlyBudgetEur = monthlyBudget,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
            return new Fixture(connection, db);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
