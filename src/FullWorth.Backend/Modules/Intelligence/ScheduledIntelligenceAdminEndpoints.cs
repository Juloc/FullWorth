using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Intelligence;

public static class ScheduledIntelligenceAdminEndpoints
{
    public static IEndpointRouteBuilder MapScheduledIntelligenceAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/intelligence/admin/jobs").WithTags("Intelligence Admin");

        group.MapPost("/{type}/enqueue", async (
            string type,
            RunIntelligenceJobRequest? request,
            CurrentUserContext currentUser,
            IntelligenceAdminAuthorizer authorizer,
            IntelligenceDbContext db,
            IntelligenceStore store,
            CancellationToken ct) =>
        {
            var userId = currentUser.RequireUserId();
            if (!await authorizer.IsAdminAsync(userId, ct)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (!IsScheduledType(type)) return Results.BadRequest(new { error = "unsupported_scheduled_job" });

            var suffix = string.IsNullOrWhiteSpace(request?.IdempotencyKey)
                ? Guid.NewGuid().ToString("N")
                : request!.IdempotencyKey!.Trim();
            if (suffix.Length > 160) return Results.BadRequest(new { error = "idempotency_key_too_long" });

            var job = await store.EnqueueJobAsync(
                type,
                "instance",
                DateTimeOffset.UtcNow,
                $"manual-scheduled:{type}:{suffix}",
                "{}",
                ct);
            IntelligenceAuditWriter.Record(db, userId, "job.enqueued", "IntelligenceJob", job.Id, job.Status);
            await db.SaveChangesAsync(ct);
            return Results.Accepted($"/api/intelligence/admin/jobs", job);
        });

        return app;
    }

    private static bool IsScheduledType(string type) =>
        type is ScheduledIntelligenceJobTypes.DailyIncremental
            or ScheduledIntelligenceJobTypes.WeeklyDeep
            or ScheduledIntelligenceJobTypes.MonthlyReview;
}
