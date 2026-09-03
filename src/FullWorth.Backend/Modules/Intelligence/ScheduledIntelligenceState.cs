using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Intelligence;

public sealed class IntelligenceJobLease
{
    public Guid JobId { get; set; }
    public string LeaseOwner { get; set; } = string.Empty;
    public DateTimeOffset LeaseUntil { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class IntelligenceWatermark
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Key { get; set; } = string.Empty;
    public DateTimeOffset Value { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public static class ScheduledIntelligenceModelConfiguration
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<IntelligenceJobLease>(entity =>
        {
            entity.HasKey(x => x.JobId);
            entity.HasIndex(x => x.LeaseUntil);
            entity.Property(x => x.LeaseOwner).HasMaxLength(120);
            entity.HasOne<IntelligenceJob>().WithOne().HasForeignKey<IntelligenceJobLease>(x => x.JobId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<IntelligenceWatermark>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.Key).IsUnique();
            entity.Property(x => x.Key).HasMaxLength(200);
        });
    }
}

public sealed class IntelligenceWatermarkStore(IntelligenceDbContext db)
{
    public Task<DateTimeOffset?> GetAsync(string key, CancellationToken ct) => db.IntelligenceWatermarks.AsNoTracking()
        .Where(x => x.Key == key)
        .Select(x => (DateTimeOffset?)x.Value)
        .SingleOrDefaultAsync(ct);

    public async Task SetAsync(string key, DateTimeOffset value, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("Watermark key is required.", nameof(key));
        var normalized = key.Trim();
        var row = await db.IntelligenceWatermarks.SingleOrDefaultAsync(x => x.Key == normalized, ct);
        if (row is null)
        {
            row = new IntelligenceWatermark { Key = normalized, Value = value, UpdatedAt = DateTimeOffset.UtcNow };
            db.IntelligenceWatermarks.Add(row);
        }
        else if (value > row.Value)
        {
            row.Value = value;
            row.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync(ct);
    }
}

public sealed class IntelligenceJobLeaseService(IntelligenceDbContext db)
{
    public static readonly TimeSpan DefaultLeaseDuration = TimeSpan.FromMinutes(10);

    public async Task<IntelligenceJob?> TryClaimNextAsync(string owner, DateTimeOffset now, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(owner)) throw new ArgumentException("Lease owner is required.", nameof(owner));
        owner = owner.Trim();

        var candidateIds = await db.IntelligenceJobs.AsNoTracking()
            .Where(job =>
                job.ScheduledFor <= now &&
                (!job.NextRetryAt.HasValue || job.NextRetryAt <= now) &&
                (job.Status == IntelligenceJobStatuses.Queued ||
                 job.Status == IntelligenceJobStatuses.Deferred ||
                 job.Status == IntelligenceJobStatuses.Running))
            .OrderBy(job => job.ScheduledFor)
            .ThenBy(job => job.CreatedAt)
            .Select(job => job.Id)
            .Take(20)
            .ToListAsync(ct);

        foreach (var jobId in candidateIds)
        {
            var lease = await db.IntelligenceJobLeases.SingleOrDefaultAsync(x => x.JobId == jobId, ct);
            if (lease is not null && lease.LeaseUntil > now) continue;

            var job = await db.IntelligenceJobs.SingleOrDefaultAsync(x => x.Id == jobId, ct);
            if (job is null) continue;

            // Queued/deferred -> running is the primary claim CAS. Reclaiming a stale running job is
            // guarded by its expired lease. The lease row has a unique PK, so concurrent insert races
            // cannot result in two durable owners.
            if (job.Status is IntelligenceJobStatuses.Queued or IntelligenceJobStatuses.Deferred)
            {
                var claimed = await db.IntelligenceJobs
                    .Where(x => x.Id == jobId && (x.Status == IntelligenceJobStatuses.Queued || x.Status == IntelligenceJobStatuses.Deferred))
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(x => x.Status, IntelligenceJobStatuses.Running)
                        .SetProperty(x => x.StartedAt, now)
                        .SetProperty(x => x.ErrorCode, (string?)null), ct);
                if (claimed != 1) continue;
                job = await db.IntelligenceJobs.SingleAsync(x => x.Id == jobId, ct);
            }

            if (lease is null)
            {
                db.IntelligenceJobLeases.Add(new IntelligenceJobLease
                {
                    JobId = jobId,
                    LeaseOwner = owner,
                    LeaseUntil = now + DefaultLeaseDuration,
                    UpdatedAt = now
                });
                try
                {
                    await db.SaveChangesAsync(ct);
                    return job;
                }
                catch (DbUpdateException)
                {
                    db.ChangeTracker.Clear();
                    continue;
                }
            }

            var renewed = await db.IntelligenceJobLeases
                .Where(x => x.JobId == jobId && x.LeaseUntil <= now)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.LeaseOwner, owner)
                    .SetProperty(x => x.LeaseUntil, now + DefaultLeaseDuration)
                    .SetProperty(x => x.UpdatedAt, now), ct);
            if (renewed == 1) return job;
        }

        return null;
    }

    public async Task<bool> RenewAsync(Guid jobId, string owner, DateTimeOffset now, CancellationToken ct) =>
        await db.IntelligenceJobLeases
            .Where(x => x.JobId == jobId && x.LeaseOwner == owner && x.LeaseUntil > now)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.LeaseUntil, now + DefaultLeaseDuration)
                .SetProperty(x => x.UpdatedAt, now), ct) == 1;

    public async Task ReleaseAsync(Guid jobId, string owner, CancellationToken ct)
    {
        await db.IntelligenceJobLeases.Where(x => x.JobId == jobId && x.LeaseOwner == owner).ExecuteDeleteAsync(ct);
    }
}
