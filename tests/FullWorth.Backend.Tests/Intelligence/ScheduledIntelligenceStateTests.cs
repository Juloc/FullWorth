using FullWorth.Backend.Modules.Intelligence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Intelligence;

public sealed class ScheduledIntelligenceStateTests
{
    [Fact]
    public async Task Watermark_only_moves_forward()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<IntelligenceDbContext>().UseSqlite(connection).Options;
        await using var db = new IntelligenceDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var store = new IntelligenceWatermarkStore(db);
        var first = new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero);
        var later = first.AddHours(2);

        await store.SetAsync("merchant:daily", later, CancellationToken.None);
        await store.SetAsync("merchant:daily", first, CancellationToken.None);

        Assert.Equal(later, await store.GetAsync("merchant:daily", CancellationToken.None));
    }

    [Fact]
    public async Task Lease_claim_marks_job_running_and_blocks_second_owner_until_expired()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<IntelligenceDbContext>().UseSqlite(connection).Options;
        await using var db = new IntelligenceDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var now = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        var job = new IntelligenceJob
        {
            Type = ScheduledIntelligenceJobTypes.DailyIncremental,
            ScopeKey = "instance",
            ScheduledFor = now,
            IdempotencyKey = "scheduled:daily:2026-09-01"
        };
        db.IntelligenceJobs.Add(job);
        await db.SaveChangesAsync();
        var leases = new IntelligenceJobLeaseService(db);

        var firstClaim = await leases.TryClaimNextAsync("worker-a", now, CancellationToken.None);
        var secondClaim = await leases.TryClaimNextAsync("worker-b", now.AddMinutes(1), CancellationToken.None);

        Assert.NotNull(firstClaim);
        Assert.Equal(job.Id, firstClaim!.Id);
        Assert.Null(secondClaim);
        Assert.Equal(IntelligenceJobStatuses.Running,
            (await db.IntelligenceJobs.AsNoTracking().SingleAsync(x => x.Id == job.Id)).Status);
        Assert.Equal("worker-a",
            (await db.IntelligenceJobLeases.AsNoTracking().SingleAsync(x => x.JobId == job.Id)).LeaseOwner);
    }

    [Fact]
    public async Task Lease_heartbeat_extends_only_current_owners_active_lease()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<IntelligenceDbContext>().UseSqlite(connection).Options;
        await using var db = new IntelligenceDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var now = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        var job = new IntelligenceJob
        {
            Type = ScheduledIntelligenceJobTypes.DailyIncremental,
            ScopeKey = "instance",
            ScheduledFor = now,
            IdempotencyKey = "scheduled:daily:heartbeat"
        };
        db.IntelligenceJobs.Add(job);
        await db.SaveChangesAsync();
        var leases = new IntelligenceJobLeaseService(db);
        Assert.NotNull(await leases.TryClaimNextAsync("worker-a", now, CancellationToken.None));
        var originalUntil = (await db.IntelligenceJobLeases.AsNoTracking().SingleAsync()).LeaseUntil;

        var wrongOwnerRenewed = await leases.RenewAsync(job.Id, "worker-b", now.AddMinutes(2), CancellationToken.None);
        var rightOwnerRenewed = await leases.RenewAsync(job.Id, "worker-a", now.AddMinutes(2), CancellationToken.None);
        var updated = await db.IntelligenceJobLeases.AsNoTracking().SingleAsync();

        Assert.False(wrongOwnerRenewed);
        Assert.True(rightOwnerRenewed);
        Assert.True(updated.LeaseUntil > originalUntil);
        Assert.Equal("worker-a", updated.LeaseOwner);
    }

    [Fact]
    public async Task Expired_running_job_can_be_reclaimed_by_another_owner()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<IntelligenceDbContext>().UseSqlite(connection).Options;
        await using var db = new IntelligenceDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var now = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        var job = new IntelligenceJob
        {
            Type = ScheduledIntelligenceJobTypes.DailyIncremental,
            ScopeKey = "instance",
            ScheduledFor = now.AddMinutes(-30),
            IdempotencyKey = "scheduled:daily:stale",
            Status = IntelligenceJobStatuses.Running,
            StartedAt = now.AddMinutes(-30)
        };
        db.IntelligenceJobs.Add(job);
        db.IntelligenceJobLeases.Add(new IntelligenceJobLease
        {
            JobId = job.Id,
            LeaseOwner = "worker-a",
            LeaseUntil = now.AddMinutes(-1),
            UpdatedAt = now.AddMinutes(-11)
        });
        await db.SaveChangesAsync();
        var leases = new IntelligenceJobLeaseService(db);

        var claim = await leases.TryClaimNextAsync("worker-b", now, CancellationToken.None);

        Assert.NotNull(claim);
        Assert.Equal("worker-b",
            (await db.IntelligenceJobLeases.AsNoTracking().SingleAsync(x => x.JobId == job.Id)).LeaseOwner);
    }
}
