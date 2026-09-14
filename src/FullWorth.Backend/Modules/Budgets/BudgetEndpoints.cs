using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Budgets;

public static class BudgetEndpoints
{
    public static IEndpointRouteBuilder MapBudgetEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/budgets").WithTags("Budgets");

        group.MapGet("/", async (Guid fullWorthSpaceId, CurrentUserContext currentUser, BudgetStore store, CancellationToken ct) =>
            Results.Ok(await store.ListForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, ct)));

        group.MapGet("/{id:guid}", async (Guid id, Guid fullWorthSpaceId, CurrentUserContext currentUser, BudgetStore store, CancellationToken ct) =>
        {
            var budget = await store.GetForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, ct);
            return budget is null ? Results.NotFound() : Results.Ok(budget);
        });

        group.MapGet("/{id:guid}/status", async (Guid id, Guid fullWorthSpaceId, DateOnly? asOf, CurrentUserContext currentUser, BudgetStore store, CancellationToken ct) =>
        {
            var asOfDate = asOf ?? DateOnly.FromDateTime(DateTime.UtcNow);
            var status = await store.GetStatusForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, asOfDate, ct);
            return status is null ? Results.NotFound() : Results.Ok(status);
        });

        group.MapPost("/", async (Guid fullWorthSpaceId, BudgetWrite request, CurrentUserContext currentUser, BudgetStore store, CancellationToken ct) =>
            ToResult(await store.CreateForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, request, ct)));

        group.MapPut("/{id:guid}", async (Guid id, Guid fullWorthSpaceId, BudgetWrite request, CurrentUserContext currentUser, BudgetStore store, CancellationToken ct) =>
            ToResult(await store.UpdateForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, request, ct)));

        group.MapDelete("/{id:guid}", async (Guid id, Guid fullWorthSpaceId, CurrentUserContext currentUser, BudgetStore store, CancellationToken ct) =>
            ToResult(await store.ArchiveForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, ct)));

        return app;
    }

    private static IResult ToResult(BudgetMutationOutcome outcome) => outcome.Result switch
    {
        BudgetMutationResult.Success => Results.Ok(outcome.Budget),
        BudgetMutationResult.NotFound => Results.NotFound(),
        BudgetMutationResult.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
        BudgetMutationResult.Invalid => Results.BadRequest(new { error = outcome.Error ?? "Invalid budget." }),
        _ => Results.StatusCode(StatusCodes.Status409Conflict)
    };

    private static IResult ToResult(BudgetMutationResult result) => result switch
    {
        BudgetMutationResult.Success => Results.NoContent(),
        BudgetMutationResult.NotFound => Results.NotFound(),
        BudgetMutationResult.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
        BudgetMutationResult.Invalid => Results.BadRequest(),
        _ => Results.StatusCode(StatusCodes.Status409Conflict)
    };
}
