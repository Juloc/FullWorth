using System.Text.Json;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Intelligence;
using FullWorth.Backend.Modules.Intelligence.Signals;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Backend.Tests.Intelligence;

public sealed class FinancialSignalJobProcessorTests
{
    [Fact]
    public async Task Signal_job_succeeds_without_ai_settings_or_credentials()
    {
        using var factory = new BackendWebApplicationFactory();
        var userId = Guid.NewGuid();
        await factory.SeedFullWorthUserAsync(userId);
        await factory.SeedAsync(async db =>
        {
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                UserId = userId,
                Role = FullWorthSpaceRoles.Member
            });
            await db.SaveChangesAsync();
        });

        await using var scope = factory.Services.CreateAsyncScope();
        var intelligenceDb = scope.ServiceProvider.GetRequiredService<IntelligenceDbContext>();
        var job = new IntelligenceJob
        {
            Type = FinancialSignalJobTypes.RefreshUser,
            ScopeKey = $"user:{userId:N}",
            IdempotencyKey = $"test:signals:{Guid.NewGuid():N}",
            ScheduledFor = DateTimeOffset.UtcNow,
            Status = IntelligenceJobStatuses.Running,
            StartedAt = DateTimeOffset.UtcNow,
            PayloadJson = JsonSerializer.Serialize(new
            {
                userId,
                fullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                reason = "test"
            })
        };
        intelligenceDb.IntelligenceJobs.Add(job);
        await intelligenceDb.SaveChangesAsync();

        var aiSettings = await intelligenceDb.AiInstanceSettings.SingleOrDefaultAsync(x =>
            x.ScopeKey == AiInstanceSettings.InstanceScopeKey);
        if (aiSettings is null)
        {
            aiSettings = new AiInstanceSettings
            {
                ScopeKey = AiInstanceSettings.InstanceScopeKey,
                Enabled = false,
                CredentialId = null
            };
            intelligenceDb.AiInstanceSettings.Add(aiSettings);
        }
        else
        {
            aiSettings.Enabled = false;
            aiSettings.CredentialId = null;
        }
        await intelligenceDb.SaveChangesAsync();

        var processor = scope.ServiceProvider.GetRequiredService<ScheduledIntelligenceJobProcessor>();
        await processor.ProcessAsync(job, CancellationToken.None);

        var storedJob = await intelligenceDb.IntelligenceJobs.AsNoTracking().SingleAsync(x => x.Id == job.Id);
        Assert.Equal(IntelligenceJobStatuses.Succeeded, storedJob.Status);
        Assert.Empty(await intelligenceDb.AiRuns.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Inactive_user_is_skipped_by_user_refresh()
    {
        using var factory = new BackendWebApplicationFactory();
        var userId = Guid.NewGuid();
        await factory.SeedFullWorthUserAsync(userId, isActive: false);
        await factory.SeedAsync(async db =>
        {
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                UserId = userId,
                Role = FullWorthSpaceRoles.Member
            });
            await db.SaveChangesAsync();
        });

        await using var scope = factory.Services.CreateAsyncScope();
        var intelligenceDb = scope.ServiceProvider.GetRequiredService<IntelligenceDbContext>();
        var job = new IntelligenceJob
        {
            Type = FinancialSignalJobTypes.RefreshUser,
            ScopeKey = $"user:{userId:N}",
            IdempotencyKey = $"test:signals-inactive:{Guid.NewGuid():N}",
            ScheduledFor = DateTimeOffset.UtcNow,
            Status = IntelligenceJobStatuses.Running,
            StartedAt = DateTimeOffset.UtcNow,
            PayloadJson = JsonSerializer.Serialize(new
            {
                userId,
                fullWorthSpaceId = FullWorthSpaceDefaults.LegacyId
            })
        };
        intelligenceDb.IntelligenceJobs.Add(job);
        await intelligenceDb.SaveChangesAsync();

        var processor = scope.ServiceProvider.GetRequiredService<ScheduledIntelligenceJobProcessor>();
        await processor.ProcessAsync(job, CancellationToken.None);

        Assert.Equal(
            IntelligenceJobStatuses.Succeeded,
            (await intelligenceDb.IntelligenceJobs.AsNoTracking().SingleAsync(x => x.Id == job.Id)).Status);
        Assert.Empty(await intelligenceDb.FinancialSignals.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Signal_job_is_a_noop_when_signals_are_explicitly_off()
    {
        var overrides = new Dictionary<string, string?>
        {
            [$"{AutopilotRolloutSettings.SectionName}:{AutopilotFeatures.Signals}"] = "off"
        };
        using var factory = new BackendWebApplicationFactory(overrides);
        var userId = Guid.NewGuid();
        await factory.SeedFullWorthUserAsync(userId);
        await factory.SeedAsync(async db =>
        {
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                UserId = userId,
                Role = FullWorthSpaceRoles.Member
            });
            await db.SaveChangesAsync();
        });

        await using var scope = factory.Services.CreateAsyncScope();
        var intelligenceDb = scope.ServiceProvider.GetRequiredService<IntelligenceDbContext>();
        var job = new IntelligenceJob
        {
            Type = FinancialSignalJobTypes.RefreshUser,
            ScopeKey = $"user:{userId:N}",
            IdempotencyKey = $"test:signals-off:{Guid.NewGuid():N}",
            ScheduledFor = DateTimeOffset.UtcNow,
            Status = IntelligenceJobStatuses.Running,
            StartedAt = DateTimeOffset.UtcNow,
            PayloadJson = JsonSerializer.Serialize(new
            {
                userId,
                fullWorthSpaceId = FullWorthSpaceDefaults.LegacyId
            })
        };
        intelligenceDb.IntelligenceJobs.Add(job);
        await intelligenceDb.SaveChangesAsync();

        var processor = scope.ServiceProvider.GetRequiredService<ScheduledIntelligenceJobProcessor>();
        await processor.ProcessAsync(job, CancellationToken.None);

        Assert.Equal(
            IntelligenceJobStatuses.Succeeded,
            (await intelligenceDb.IntelligenceJobs.AsNoTracking().SingleAsync(x => x.Id == job.Id)).Status);
        Assert.Empty(await intelligenceDb.FinancialSignals.AsNoTracking().ToListAsync());
        Assert.Empty(await intelligenceDb.AiRuns.AsNoTracking().ToListAsync());
    }
}
