using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Audit;
using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Coach;

public static class CoachEndpoints
{
    public static IEndpointRouteBuilder MapSpendingReviewEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/spending-reviews").WithTags("Spending Reviews");

        group.MapGet("/reasons", () => Results.Ok(SpendingReviewService.ReasonCatalog));

        group.MapGet("/transactions/{transactionId:guid}", async (
            Guid transactionId,
            Guid fullWorthSpaceId,
            CurrentUserContext currentUser,
            SpendingReviewService service,
            CancellationToken ct) =>
        {
            var review = await service.GetAsync(currentUser.RequireUserId(), fullWorthSpaceId, transactionId, ct);
            return review is null ? Results.NotFound() : Results.Ok(review);
        });

        group.MapPut("/transactions/{transactionId:guid}", async (
            Guid transactionId,
            Guid fullWorthSpaceId,
            UpsertSpendingReviewRequest request,
            CurrentUserContext currentUser,
            SpendingReviewService service,
            AuditService audit,
            FullWorthDbContext db,
            CancellationToken ct) =>
        {
            try
            {
                var userId = currentUser.RequireUserId();
                var result = await service.UpsertAsync(userId, fullWorthSpaceId, transactionId, request, ct);
                if (result.Result == SpendingReviewWriteResult.Saved && result.Review is { } saved)
                {
                    audit.Record(fullWorthSpaceId, userId, "spending_review.upsert", "SpendingReview", saved.Id);
                    await db.SaveChangesAsync(ct);
                }
                return result.Result switch
                {
                    SpendingReviewWriteResult.Saved => Results.Ok(result.Review),
                    SpendingReviewWriteResult.Invalid => Results.BadRequest(new { error = result.Error ?? "invalid_review" }),
                    _ => Results.NotFound()
                };
            }
            catch (SpendingReviewValidationException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        });

        group.MapDelete("/transactions/{transactionId:guid}", async (
            Guid transactionId,
            Guid fullWorthSpaceId,
            CurrentUserContext currentUser,
            SpendingReviewService service,
            AuditService audit,
            FullWorthDbContext db,
            CancellationToken ct) =>
        {
            var userId = currentUser.RequireUserId();
            var existing = await service.GetAsync(userId, fullWorthSpaceId, transactionId, ct);
            if (!await service.DeleteAsync(userId, fullWorthSpaceId, transactionId, ct)) return Results.NotFound();
            audit.Record(fullWorthSpaceId, userId, "spending_review.delete", "SpendingReview", existing?.Id);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        group.MapGet("/summary", async (
            Guid fullWorthSpaceId,
            DateOnly? from,
            DateOnly? to,
            CurrentUserContext currentUser,
            SpendingReviewService service,
            CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await service.GetSummaryAsync(currentUser.RequireUserId(), fullWorthSpaceId, from, to, ct));
            }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (SpendingReviewValidationException exception) { return Results.BadRequest(new { error = exception.Message }); }
        });

        group.MapGet("/recent", async (
            Guid fullWorthSpaceId,
            int? limit,
            SpendingSentiment? sentiment,
            CurrentUserContext currentUser,
            SpendingReviewService service,
            CancellationToken ct) =>
            Results.Ok(await service.RecentAsync(currentUser.RequireUserId(), fullWorthSpaceId, limit ?? 20, sentiment, ct)));

        return app;
    }

    public static IEndpointRouteBuilder MapCoachEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/coach").WithTags("Coach");

        group.MapPost("/conversations", async (
            Guid fullWorthSpaceId,
            CreateCoachConversationRequest request,
            CurrentUserContext currentUser,
            CoachService service,
            AuditService audit,
            FullWorthDbContext db,
            CancellationToken ct) =>
        {
            try
            {
                var userId = currentUser.RequireUserId();
                var conversation = await service.CreateConversationAsync(userId, fullWorthSpaceId, request, ct);
                audit.Record(fullWorthSpaceId, userId, "coach_conversation.create", "CoachConversation", conversation.Id);
                await db.SaveChangesAsync(ct);
                return Results.Created($"/api/coach/conversations/{conversation.Id}?fullWorthSpaceId={fullWorthSpaceId}", conversation);
            }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (ArgumentException exception) { return Results.BadRequest(new { error = exception.Message }); }
        });

        group.MapGet("/conversations", async (
            Guid fullWorthSpaceId,
            int? limit,
            CurrentUserContext currentUser,
            CoachService service,
            CancellationToken ct) =>
        {
            try { return Results.Ok(await service.ListConversationsAsync(currentUser.RequireUserId(), fullWorthSpaceId, limit ?? 20, ct)); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
        });

        group.MapGet("/conversations/{id:guid}", async (
            Guid id,
            Guid fullWorthSpaceId,
            CurrentUserContext currentUser,
            CoachService service,
            CancellationToken ct) =>
        {
            var conversation = await service.GetConversationAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, ct);
            return conversation is null ? Results.NotFound() : Results.Ok(conversation);
        });

        group.MapDelete("/conversations/{id:guid}", async (
            Guid id,
            Guid fullWorthSpaceId,
            CurrentUserContext currentUser,
            CoachService service,
            AuditService audit,
            FullWorthDbContext db,
            CancellationToken ct) =>
        {
            var userId = currentUser.RequireUserId();
            if (!await service.ArchiveConversationAsync(userId, fullWorthSpaceId, id, ct)) return Results.NotFound();
            audit.Record(fullWorthSpaceId, userId, "coach_conversation.archive", "CoachConversation", id);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        group.MapPost("/conversations/{id:guid}/messages", async (
            Guid id,
            Guid fullWorthSpaceId,
            AskCoachRequest request,
            CurrentUserContext currentUser,
            CoachService service,
            CancellationToken ct) =>
        {
            try
            {
                var userId = currentUser.RequireUserId();
                if (!CoachRequestLimiter.TryAcquire(userId, out var retryAfter)) return RateLimited(retryAfter);
                var answer = await service.AskAsync(userId, fullWorthSpaceId, id, request, ct);
                return answer is null ? Results.NotFound() : Results.Ok(answer);
            }
            catch (ArgumentException exception) { return Results.BadRequest(new { error = exception.Message }); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
        });

        group.MapPost("/ask", async (
            Guid fullWorthSpaceId,
            AskCoachRequest request,
            CurrentUserContext currentUser,
            CoachService service,
            CancellationToken ct) =>
        {
            try
            {
                var userId = currentUser.RequireUserId();
                if (!CoachRequestLimiter.TryAcquire(userId, out var retryAfter)) return RateLimited(retryAfter);
                return Results.Ok(await service.AskEphemeralAsync(userId, fullWorthSpaceId, request, ct));
            }
            catch (ArgumentException exception) { return Results.BadRequest(new { error = exception.Message }); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
        });

        return app;
    }

    private static IResult RateLimited(TimeSpan retryAfter) => Results.Json(
        new { error = "coach_rate_limited", retryAfterSeconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)) },
        statusCode: StatusCodes.Status429TooManyRequests);
}
