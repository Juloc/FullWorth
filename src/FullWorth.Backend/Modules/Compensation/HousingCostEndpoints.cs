using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Compensation;

/// <summary>Heiz-, Wasser- und Nebenkosten aus Abrechnungen, fuer den Vergleich mit den Preisen. Alles hier sieht nur der Benutzer, dem es gehoert.</summary>
public static class HousingCostEndpoints
{
    public static IEndpointRouteBuilder MapCompensationHousingCostEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/compensation/housing-costs").WithTags("Compensation");

        group.MapGet("/", async (Guid fullWorthSpaceId, CurrentUserContext currentUser, SpaceAccess access, HousingCostStore store, CancellationToken ct) =>
        {
            var userId = currentUser.RequireUserId();
            return await access.IsMemberAsync(userId, fullWorthSpaceId, ct) ? Results.Ok(await store.ListAsync(userId, fullWorthSpaceId, ct)) : Results.NotFound();
        });

        group.MapPost("/", async (Guid fullWorthSpaceId, HousingCostWrite request, CurrentUserContext currentUser, SpaceAccess access, HousingCostStore store, CancellationToken ct) =>
        {
            var userId = currentUser.RequireUserId();
            if (!await access.IsMemberAsync(userId, fullWorthSpaceId, ct)) return Results.NotFound();
            try { HousingCostStore.Validate(request); }
            catch (ArgumentException exception) { return Results.BadRequest(new { error = exception.Message }); }
            return Results.Created($"/api/compensation/housing-costs?fullWorthSpaceId={fullWorthSpaceId}", new { id = await store.AddAsync(userId, fullWorthSpaceId, request, ct) });
        });

        group.MapPut("/{id:guid}", async (Guid id, Guid fullWorthSpaceId, HousingCostWrite request, CurrentUserContext currentUser, SpaceAccess access, HousingCostStore store, CancellationToken ct) =>
        {
            var userId = currentUser.RequireUserId();
            if (!await access.IsMemberAsync(userId, fullWorthSpaceId, ct)) return Results.NotFound();
            try { HousingCostStore.Validate(request); }
            catch (ArgumentException exception) { return Results.BadRequest(new { error = exception.Message }); }
            return await store.UpdateAsync(userId, fullWorthSpaceId, id, request, ct) ? Results.NoContent() : Results.NotFound();
        });

        group.MapDelete("/{id:guid}", async (Guid id, Guid fullWorthSpaceId, CurrentUserContext currentUser, SpaceAccess access, HousingCostStore store, CancellationToken ct) =>
        {
            var userId = currentUser.RequireUserId();
            if (!await access.IsMemberAsync(userId, fullWorthSpaceId, ct)) return Results.NotFound();
            return await store.DeleteAsync(userId, fullWorthSpaceId, id, ct) ? Results.NoContent() : Results.NotFound();
        });

        return app;
    }
}
