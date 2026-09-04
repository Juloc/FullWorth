using FullWorth.Backend.Security;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Intelligence;

public static class CloudSyncAdminEndpoints
{
    public static IEndpointRouteBuilder MapCloudSyncAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/intelligence/admin/cloud").WithTags("Intelligence Admin");

        group.MapPost("/local-only", async (
            CurrentUserContext currentUser,
            IntelligenceAdminAuthorizer authorizer,
            IntelligenceDbContext db,
            CloudIntelligenceStateService cloudState,
            CancellationToken ct) =>
        {
            if (!currentUser.IsAuthenticated)
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var userId = currentUser.UserId;
            if (!await authorizer.IsAdminAsync(userId, ct))
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            var result = await cloudState.DisableAsync(userId, ct);
            IntelligenceAuditWriter.Record(
                db,
                userId,
                "cloud.local_only",
                "CloudConnectionState",
                outcome: "disabled");
            await db.SaveChangesAsync(ct);
            return Results.Ok(result);
        });

        group.MapPost("/sync", async (
            CurrentUserContext currentUser,
            IntelligenceAdminAuthorizer authorizer,
            IntelligenceDbContext db,
            CloudOutboxUploader uploader,
            CancellationToken ct) =>
        {
            if (!currentUser.IsAuthenticated)
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var userId = currentUser.UserId;
            if (!await authorizer.IsAdminAsync(userId, ct))
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            var result = await uploader.SyncOnceAsync(ct);
            IntelligenceAuditWriter.Record(
                db,
                userId,
                "cloud.sync",
                "CloudSubmissionOutbox",
                outcome: result.ErrorCode ?? $"sent:{result.Sent};retry:{result.Retried};dead:{result.DeadLettered}");
            await db.SaveChangesAsync(ct);
            return Results.Ok(result);
        });

        group.MapGet("/outbox", async (
            CurrentUserContext currentUser,
            IntelligenceAdminAuthorizer authorizer,
            IntelligenceDbContext db,
            CancellationToken ct) =>
        {
            if (!currentUser.IsAuthenticated)
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var userId = currentUser.UserId;
            if (!await authorizer.IsAdminAsync(userId, ct))
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            var counts = await db.CloudSubmissionOutbox.AsNoTracking()
                .GroupBy(x => x.Status)
                .Select(grouping => new { status = grouping.Key, count = grouping.Count() })
                .ToListAsync(ct);
            return Results.Ok(new
            {
                counts,
                nextAttemptAt = await db.CloudSubmissionOutbox.AsNoTracking()
                    .Where(x => x.Status == CloudSubmissionStatuses.Failed && x.NextAttemptAt != null)
                    .MinAsync(x => x.NextAttemptAt, ct)
            });
        });

        return app;
    }
}
