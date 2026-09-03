using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Intelligence;

public static class IntelligenceSuggestionEndpoints
{
    public static IEndpointRouteBuilder MapIntelligenceSuggestionEndpoints(this IEndpointRouteBuilder app)
    {
        // Keep all Phase-3 review/scheduler/user surfaces behind the same Program.cs registration.
        // This avoids a second top-level mapping hook while the Intelligence module remains isolated.
        app.MapScheduledIntelligenceAdminEndpoints();
        app.MapIntelligenceDigestEndpoints();

        var group = app.MapGroup("/api/intelligence/admin/suggestions").WithTags("Intelligence Admin");

        group.MapGet("/pending", async (
            int? limit,
            CurrentUserContext currentUser,
            IntelligenceAdminAuthorizer authorizer,
            IntelligenceSuggestionReviewService review,
            CancellationToken ct) =>
        {
            var userId = currentUser.RequireUserId();
            if (!await authorizer.IsAdminAsync(userId, ct)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            return Results.Ok(await review.ListPendingAsync(limit ?? 100, ct));
        });

        group.MapPost("/{id:guid}/accept", async (
            Guid id,
            CurrentUserContext currentUser,
            IntelligenceAdminAuthorizer authorizer,
            IntelligenceDbContext db,
            IntelligenceSuggestionReviewService review,
            CancellationToken ct) =>
        {
            var userId = currentUser.RequireUserId();
            if (!await authorizer.IsAdminAsync(userId, ct)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var result = await review.AcceptAsync(id, userId, ct);
            if (!result.Success)
                return result.ErrorCode == "suggestion_not_found"
                    ? Results.NotFound(new { error = result.ErrorCode })
                    : Results.Conflict(new { error = result.ErrorCode });

            IntelligenceAuditWriter.Record(db, userId, "suggestion.accepted", "IntelligenceSuggestion", id, "accepted");
            await db.SaveChangesAsync(ct);
            return Results.Ok(result.Suggestion);
        });

        group.MapPost("/{id:guid}/reject", async (
            Guid id,
            CurrentUserContext currentUser,
            IntelligenceAdminAuthorizer authorizer,
            IntelligenceDbContext db,
            IntelligenceSuggestionReviewService review,
            CancellationToken ct) =>
        {
            var userId = currentUser.RequireUserId();
            if (!await authorizer.IsAdminAsync(userId, ct)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var result = await review.RejectAsync(id, userId, ct);
            if (!result.Success)
                return result.ErrorCode == "suggestion_not_found"
                    ? Results.NotFound(new { error = result.ErrorCode })
                    : Results.Conflict(new { error = result.ErrorCode });

            IntelligenceAuditWriter.Record(db, userId, "suggestion.rejected", "IntelligenceSuggestion", id, "rejected");
            await db.SaveChangesAsync(ct);
            return Results.Ok(result.Suggestion);
        });

        return app;
    }
}
