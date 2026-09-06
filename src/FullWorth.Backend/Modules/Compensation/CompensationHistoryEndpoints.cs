using FullWorth.Backend.Data;
using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Compensation;

public static class CompensationHistoryEndpoints
{
    public static IEndpointRouteBuilder MapCompensationHistoryEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/compensation").WithTags("Compensation");

        group.MapGet("/history", async (
            Guid fullWorthSpaceId,
            CurrentUserContext currentUser,
            FullWorthDbContext db,
            CancellationToken ct) =>
        {
            var store = new CompensationHistoryStore(db);
            var entries = await store.ListAsync(currentUser.RequireUserId(), fullWorthSpaceId, ct);
            return entries is null ? Results.NotFound() : Results.Ok(entries);
        });

        group.MapPost("/history", async (
            Guid fullWorthSpaceId,
            CompensationHistoryWrite request,
            CurrentUserContext currentUser,
            FullWorthDbContext db,
            CancellationToken ct) =>
        {
            try
            {
                var store = new CompensationHistoryStore(db);
                var entry = await store.CreateAsync(
                    currentUser.RequireUserId(), fullWorthSpaceId, request, ct);
                return entry is null
                    ? Results.NotFound()
                    : Results.Created($"/api/compensation/history/{entry.Id}?fullWorthSpaceId={fullWorthSpaceId}", entry);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        });

        group.MapPut("/history/{id:guid}", async (
            Guid id,
            Guid fullWorthSpaceId,
            CompensationHistoryWrite request,
            CurrentUserContext currentUser,
            FullWorthDbContext db,
            CancellationToken ct) =>
        {
            try
            {
                var store = new CompensationHistoryStore(db);
                var entry = await store.UpdateAsync(
                    currentUser.RequireUserId(), fullWorthSpaceId, id, request, ct);
                return entry is null ? Results.NotFound() : Results.Ok(entry);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        });

        group.MapDelete("/history/{id:guid}", async (
            Guid id,
            Guid fullWorthSpaceId,
            CurrentUserContext currentUser,
            FullWorthDbContext db,
            CancellationToken ct) =>
        {
            var store = new CompensationHistoryStore(db);
            var deleted = await store.DeleteAsync(
                currentUser.RequireUserId(), fullWorthSpaceId, id, ct);
            return deleted switch
            {
                null => Results.NotFound(),
                true => Results.NoContent(),
                false => Results.NotFound()
            };
        });

        group.MapGet("/timeline", async (
            Guid fullWorthSpaceId,
            DateOnly? from,
            DateOnly? to,
            CurrentUserContext currentUser,
            FullWorthDbContext db,
            CancellationToken ct) =>
        {
            try
            {
                var store = new CompensationHistoryStore(db);
                var timeline = await store.TimelineAsync(
                    currentUser.RequireUserId(), fullWorthSpaceId, from, to, ct);
                return timeline is null ? Results.NotFound() : Results.Ok(timeline);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        });

        return app;
    }
}
