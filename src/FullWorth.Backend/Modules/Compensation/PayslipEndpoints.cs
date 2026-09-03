using FullWorth.Backend.Data;
using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Compensation;

public static class PayslipEndpoints
{
    public static IEndpointRouteBuilder MapCompensationPayslipEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/compensation/payslips").WithTags("Compensation");

        group.MapPost("/extract", async (IFormFile file, CurrentUserContext currentUser, CancellationToken ct) =>
        {
            _ = currentUser.RequireUserId();
            try
            {
                return Results.Ok(await PayslipExtractor.ExtractAsync(file, ct));
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        }).DisableAntiforgery();

        group.MapGet("/", async (Guid fullWorthSpaceId, CurrentUserContext currentUser, FullWorthDbContext db, CancellationToken ct) =>
        {
            var store = new PayslipStore(db);
            var payslips = await store.ListAsync(currentUser.RequireUserId(), fullWorthSpaceId, ct);
            return payslips is null ? Results.NotFound() : Results.Ok(payslips);
        });

        group.MapGet("/latest-delta", async (Guid fullWorthSpaceId, CurrentUserContext currentUser, FullWorthDbContext db, CancellationToken ct) =>
        {
            var store = new PayslipStore(db);
            var delta = await store.GetLatestDeltaAsync(currentUser.RequireUserId(), fullWorthSpaceId, ct);
            return delta is null ? Results.NoContent() : Results.Ok(delta);
        });

        group.MapPost("/", async (Guid fullWorthSpaceId, PayslipRecordWrite request, CurrentUserContext currentUser, FullWorthDbContext db, CancellationToken ct) =>
        {
            try
            {
                var store = new PayslipStore(db);
                var saved = await store.SaveAsync(currentUser.RequireUserId(), fullWorthSpaceId, request, ct);
                return saved is null ? Results.NotFound() : Results.Created($"/api/compensation/payslips/{saved.Id}?fullWorthSpaceId={fullWorthSpaceId}", saved);
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        });

        group.MapDelete("/{id:guid}", async (Guid id, Guid fullWorthSpaceId, CurrentUserContext currentUser, FullWorthDbContext db, CancellationToken ct) =>
        {
            var store = new PayslipStore(db);
            var deleted = await store.DeleteAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, ct);
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
