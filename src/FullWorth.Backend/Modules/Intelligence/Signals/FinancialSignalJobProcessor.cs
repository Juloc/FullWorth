using System.Text.Json;
using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Intelligence.Context;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Intelligence.Signals;

public sealed class FinancialSignalJobProcessor(
    IntelligenceDbContext intelligenceDb,
    FullWorthDbContext financeDb,
    FinancialContextSnapshotService contextSnapshots,
    FinancialSignalDetectionService detection,
    AutopilotRolloutSettings rollout,
    ILogger<FinancialSignalJobProcessor> logger)
{
    public async Task ProcessAsync(IntelligenceJob job, CancellationToken ct)
    {
        if (!FinancialSignalJobTypes.IsSupported(job.Type))
            throw new ArgumentException("Unsupported signal job type.", nameof(job));

        if (!rollout.IsShadowOrOn(AutopilotFeatures.Signals))
        {
            await CompleteAsync(job.Id, ct);
            return;
        }

        try
        {
            var targets = await ResolveTargetsAsync(job, ct);
            foreach (var target in targets)
            {
                try
                {
                    var snapshot = await contextSnapshots.BuildAsync(
                        target.UserId,
                        target.FullWorthSpaceId,
                        from: null,
                        to: null,
                        ct: ct);
                    await detection.DetectAndPersistAsync(snapshot, DateTimeOffset.UtcNow, ct);
                }
                catch (KeyNotFoundException)
                {
                    // Membership or space disappeared after the job was queued. Nothing remains to process.
                }
            }

            await CompleteAsync(job.Id, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Financial signal job {JobId} failed.", job.Id);
            await RetryAsync(job.Id, "signal_job_failed", ct);
        }
    }

    private async Task<IReadOnlyList<SignalTarget>> ResolveTargetsAsync(IntelligenceJob job, CancellationToken ct)
    {
        if (job.Type == FinancialSignalJobTypes.DailyFallback)
            return await AllTargetsAsync(ct);

        using var payload = JsonDocument.Parse(job.PayloadJson);
        var root = payload.RootElement;
        var fullWorthSpaceId = RequiredGuid(root, "fullWorthSpaceId");

        if (job.Type == FinancialSignalJobTypes.RefreshSpace)
        {
            return await financeDb.FullWorthSpaceMembers.AsNoTracking()
                .Where(x =>
                    x.FullWorthSpaceId == fullWorthSpaceId &&
                    financeDb.Users.Any(user => user.Id == x.UserId && user.IsActive && !user.IsTombstone))
                .Select(x => new SignalTarget(x.UserId, x.FullWorthSpaceId))
                .Distinct()
                .ToListAsync(ct);
        }

        var userId = RequiredGuid(root, "userId");
        var member = await financeDb.FullWorthSpaceMembers.AsNoTracking().AnyAsync(x =>
            x.UserId == userId &&
            x.FullWorthSpaceId == fullWorthSpaceId &&
            financeDb.Users.Any(user => user.Id == x.UserId && user.IsActive && !user.IsTombstone), ct);
        return member ? [new SignalTarget(userId, fullWorthSpaceId)] : [];
    }

    private Task<List<SignalTarget>> AllTargetsAsync(CancellationToken ct) =>
        financeDb.FullWorthSpaceMembers.AsNoTracking()
            .Where(x => financeDb.Users.Any(user => user.Id == x.UserId && user.IsActive && !user.IsTombstone))
            .Select(x => new SignalTarget(x.UserId, x.FullWorthSpaceId))
            .Distinct()
            .ToListAsync(ct);

    private async Task CompleteAsync(Guid jobId, CancellationToken ct)
    {
        var row = await intelligenceDb.IntelligenceJobs.SingleAsync(x => x.Id == jobId, ct);
        row.Status = IntelligenceJobStatuses.Succeeded;
        row.CompletedAt = DateTimeOffset.UtcNow;
        row.NextRetryAt = null;
        row.ErrorCode = null;
        await intelligenceDb.SaveChangesAsync(ct);
    }

    private async Task RetryAsync(Guid jobId, string errorCode, CancellationToken ct)
    {
        var row = await intelligenceDb.IntelligenceJobs.SingleAsync(x => x.Id == jobId, ct);
        row.RetryCount += 1;
        row.ErrorCode = errorCode;
        if (row.RetryCount >= 5)
        {
            row.Status = IntelligenceJobStatuses.Failed;
            row.CompletedAt = DateTimeOffset.UtcNow;
            row.NextRetryAt = null;
        }
        else
        {
            row.Status = IntelligenceJobStatuses.Deferred;
            var minutes = Math.Min(360, 5 * (1 << Math.Min(6, row.RetryCount - 1)));
            row.NextRetryAt = DateTimeOffset.UtcNow.AddMinutes(minutes);
        }
        await intelligenceDb.SaveChangesAsync(ct);
    }

    private static Guid RequiredGuid(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var value) ||
            value.ValueKind != JsonValueKind.String ||
            !Guid.TryParse(value.GetString(), out var id) ||
            id == Guid.Empty)
            throw new InvalidOperationException($"Signal job payload is missing valid {property}.");
        return id;
    }

    private sealed record SignalTarget(Guid UserId, Guid FullWorthSpaceId);
}
