using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Compensation;

public static class EmploymentContractEndpoints
{
    public static IEndpointRouteBuilder MapCompensationEmploymentContractEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/compensation/contracts").WithTags("Compensation");

        group.MapGet("/", async (Guid fullWorthSpaceId, CurrentUserContext currentUser, SpaceAccess access, EmploymentContractService service, CancellationToken ct) =>
        {
            var userId = currentUser.RequireUserId();
            return await access.IsMemberAsync(userId, fullWorthSpaceId, ct)
                ? Results.Ok(await service.OverviewAsync(userId, fullWorthSpaceId, ct))
                : Results.NotFound();
        });

        group.MapPost("/", async (Guid fullWorthSpaceId, Guid? importId, ContractStateWrite request, CurrentUserContext currentUser, SpaceAccess access, EmploymentContractStore store, CancellationToken ct) =>
        {
            var userId = currentUser.RequireUserId();
            if (!await access.IsMemberAsync(userId, fullWorthSpaceId, ct)) return Results.NotFound();
            try { EmploymentContractStore.Validate(request); }
            catch (ArgumentException exception) { return Results.BadRequest(new { error = exception.Message }); }
            var id = await store.AddAsync(userId, fullWorthSpaceId, request, importId, ct);
            return id is null ? Results.NotFound() : Results.Created($"/api/compensation/contracts?fullWorthSpaceId={fullWorthSpaceId}", new { id });
        });

        group.MapDelete("/{id:guid}", async (Guid id, Guid fullWorthSpaceId, CurrentUserContext currentUser, SpaceAccess access, EmploymentContractService service, CancellationToken ct) =>
        {
            var userId = currentUser.RequireUserId();
            if (!await access.IsMemberAsync(userId, fullWorthSpaceId, ct)) return Results.NotFound();
            return await service.DeleteAsync(userId, fullWorthSpaceId, id, ct) ? Results.NoContent() : Results.NotFound();
        });

        return app;
    }
}
