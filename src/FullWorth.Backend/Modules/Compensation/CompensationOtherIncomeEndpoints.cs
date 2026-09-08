using FullWorth.Backend.Data;
using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Compensation;

public static class CompensationOtherIncomeEndpoints
{
    public static IEndpointRouteBuilder MapCompensationOtherIncomeEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/compensation").WithTags("Compensation");

        // Suggestions for the type picker. The stored type is an open set, so the frontend must accept
        // free text as well and only use this list to pre-fill.
        group.MapGet("/other-income/types", () => Results.Ok(CompensationOtherIncome.SuggestedTypes));

        group.MapGet("/other-income", async (
            Guid fullWorthSpaceId,
            CurrentUserContext currentUser,
            FullWorthDbContext db,
            CancellationToken ct) =>
        {
            var store = new CompensationOtherIncomeStore(db);
            var entries = await store.ListAsync(currentUser.RequireUserId(), fullWorthSpaceId, ct);
            return entries is null ? Results.NotFound() : Results.Ok(entries);
        });

        group.MapPost("/other-income", async (
            Guid fullWorthSpaceId,
            CompensationOtherIncomeWrite request,
            CurrentUserContext currentUser,
            FullWorthDbContext db,
            CancellationToken ct) =>
        {
            try
            {
                var store = new CompensationOtherIncomeStore(db);
                var entry = await store.CreateAsync(
                    currentUser.RequireUserId(), fullWorthSpaceId, request, ct);
                return entry is null
                    ? Results.NotFound()
                    : Results.Created(
                        $"/api/compensation/other-income/{entry.Id}?fullWorthSpaceId={fullWorthSpaceId}", entry);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        });

        group.MapPut("/other-income/{id:guid}", async (
            Guid id,
            Guid fullWorthSpaceId,
            CompensationOtherIncomeWrite request,
            CurrentUserContext currentUser,
            FullWorthDbContext db,
            CancellationToken ct) =>
        {
            try
            {
                var store = new CompensationOtherIncomeStore(db);
                var entry = await store.UpdateAsync(
                    currentUser.RequireUserId(), fullWorthSpaceId, id, request, ct);
                return entry is null ? Results.NotFound() : Results.Ok(entry);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        });

        group.MapDelete("/other-income/{id:guid}", async (
            Guid id,
            Guid fullWorthSpaceId,
            CurrentUserContext currentUser,
            FullWorthDbContext db,
            CancellationToken ct) =>
        {
            var store = new CompensationOtherIncomeStore(db);
            var deleted = await store.DeleteAsync(
                currentUser.RequireUserId(), fullWorthSpaceId, id, ct);
            return deleted switch
            {
                null => Results.NotFound(),
                true => Results.NoContent(),
                false => Results.NotFound()
            };
        });

        return app;
    }
}
