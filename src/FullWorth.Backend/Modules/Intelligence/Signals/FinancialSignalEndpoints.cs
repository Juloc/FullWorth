using FullWorth.Backend.Data;
using FullWorth.Backend.Security;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Intelligence.Signals;

public sealed record SnoozeFinancialSignalRequest(DateTimeOffset Until);
public sealed record FinancialSignalFeedbackRequest(string Feedback);

public static class FinancialSignalEndpoints
{
    public static IEndpointRouteBuilder MapFinancialSignalEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/insights").WithTags("Insights");

        group.MapGet("/", async (
            Guid fullWorthSpaceId,
            string? view,
            int? limit,
            CurrentUserContext currentUser,
            FullWorthDbContext financeDb,
            FinancialSignalStore store,
            AutopilotRolloutSettings rollout,
            CancellationToken ct) =>
        {
            if (!rollout.IsOn(AutopilotFeatures.Insights))
                return Results.NotFound();

            var userId = currentUser.RequireUserId();
            if (!await IsMemberAsync(financeDb, userId, fullWorthSpaceId, ct))
                return Results.NotFound();

            try
            {
                var result = await store.ListAsync(
                    userId,
                    fullWorthSpaceId,
                    view ?? "active",
                    limit ?? 50,
                    DateTimeOffset.UtcNow,
                    ct);
                return Results.Ok(result);
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        });

        group.MapGet("/{id:guid}", async (
            Guid id,
            Guid fullWorthSpaceId,
            CurrentUserContext currentUser,
            FullWorthDbContext financeDb,
            FinancialSignalStore store,
            AutopilotRolloutSettings rollout,
            CancellationToken ct) =>
        {
            if (!rollout.IsOn(AutopilotFeatures.Insights))
                return Results.NotFound();

            var userId = currentUser.RequireUserId();
            if (!await IsMemberAsync(financeDb, userId, fullWorthSpaceId, ct))
                return Results.NotFound();

            var result = await store.GetAsync(userId, fullWorthSpaceId, id, DateTimeOffset.UtcNow, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });

        group.MapPost("/{id:guid}/read", async (
            Guid id,
            Guid fullWorthSpaceId,
            CurrentUserContext currentUser,
            FullWorthDbContext financeDb,
            FinancialSignalStore store,
            AutopilotRolloutSettings rollout,
            CancellationToken ct) =>
            await StateMutationAsync(
                currentUser.RequireUserId(),
                fullWorthSpaceId,
                financeDb,
                ct,
                () => store.MarkReadAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, ct)));

        group.MapPost("/{id:guid}/dismiss", async (
            Guid id,
            Guid fullWorthSpaceId,
            CurrentUserContext currentUser,
            FullWorthDbContext financeDb,
            FinancialSignalStore store,
            AutopilotRolloutSettings rollout,
            CancellationToken ct) =>
            await StateMutationAsync(
                currentUser.RequireUserId(),
                fullWorthSpaceId,
                financeDb,
                ct,
                () => store.DismissAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, ct)));

        group.MapPost("/{id:guid}/snooze", async (
            Guid id,
            Guid fullWorthSpaceId,
            SnoozeFinancialSignalRequest request,
            CurrentUserContext currentUser,
            FullWorthDbContext financeDb,
            FinancialSignalStore store,
            AutopilotRolloutSettings rollout,
            CancellationToken ct) =>
        {
            if (!rollout.IsOn(AutopilotFeatures.Insights))
                return Results.NotFound();

            var userId = currentUser.RequireUserId();
            if (!await IsMemberAsync(financeDb, userId, fullWorthSpaceId, ct))
                return Results.NotFound();

            try
            {
                return await store.SnoozeAsync(userId, fullWorthSpaceId, id, request.Until, ct)
                    ? Results.NoContent()
                    : Results.NotFound();
            }
            catch (ArgumentOutOfRangeException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        });

        group.MapPost("/{id:guid}/feedback", async (
            Guid id,
            Guid fullWorthSpaceId,
            FinancialSignalFeedbackRequest request,
            CurrentUserContext currentUser,
            FullWorthDbContext financeDb,
            FinancialSignalStore store,
            AutopilotRolloutSettings rollout,
            CancellationToken ct) =>
        {
            if (!rollout.IsOn(AutopilotFeatures.Insights))
                return Results.NotFound();

            var userId = currentUser.RequireUserId();
            if (!await IsMemberAsync(financeDb, userId, fullWorthSpaceId, ct))
                return Results.NotFound();

            try
            {
                return await store.SetFeedbackAsync(userId, fullWorthSpaceId, id, request.Feedback, ct)
                    ? Results.NoContent()
                    : Results.NotFound();
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        });

        return app;
    }

    private static async Task<IResult> StateMutationAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        FullWorthDbContext financeDb,
        CancellationToken ct,
        Func<Task<bool>> mutate)
    {
        if (!await IsMemberAsync(financeDb, userId, fullWorthSpaceId, ct))
            return Results.NotFound();
        return await mutate() ? Results.NoContent() : Results.NotFound();
    }

    private static Task<bool> IsMemberAsync(
        FullWorthDbContext db,
        Guid userId,
        Guid fullWorthSpaceId,
        CancellationToken ct) =>
        db.FullWorthSpaceMembers.AsNoTracking().AnyAsync(x =>
            x.UserId == userId && x.FullWorthSpaceId == fullWorthSpaceId, ct);
}
