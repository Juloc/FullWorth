using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Loans;

public static class LoanEndpoints
{
    public static IEndpointRouteBuilder MapLoanEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/loans").WithTags("Loans");

        group.MapGet("/", async (Guid fullWorthSpaceId, CurrentUserContext currentUser, LoanStore store, CancellationToken ct) =>
        {
            var loans = await store.ListForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, ct);
            return loans is null ? Results.NotFound() : Results.Ok(loans);
        });

        group.MapGet("/{id:guid}", async (Guid id, Guid fullWorthSpaceId, CurrentUserContext currentUser, LoanStore store, CancellationToken ct) =>
        {
            var loan = await store.GetForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, ct);
            return loan is null ? Results.NotFound() : Results.Ok(loan);
        });

        group.MapGet("/{id:guid}/amortization", async (Guid id, Guid fullWorthSpaceId, CurrentUserContext currentUser, LoanStore store, CancellationToken ct) =>
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            var outcome = await store.GetAmortizationForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, today, ct);
            return outcome.Status switch
            {
                AmortizationStatus.Ok => Results.Ok(outcome.Result),
                AmortizationStatus.Insufficient => Results.BadRequest(new { error = "not_enough_history" }),
                _ => Results.NotFound()
            };
        });

        group.MapPost("/", async (Guid fullWorthSpaceId, LoanWrite request, CurrentUserContext currentUser, LoanStore store, CancellationToken ct) =>
            ToResult(await store.CreateForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, request, ct)));

        group.MapPut("/{id:guid}", async (Guid id, Guid fullWorthSpaceId, LoanWrite request, CurrentUserContext currentUser, LoanStore store, CancellationToken ct) =>
            ToResult(await store.UpdateForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, request, ct)));

        group.MapDelete("/{id:guid}", async (Guid id, Guid fullWorthSpaceId, CurrentUserContext currentUser, LoanStore store, CancellationToken ct) =>
            ToResult(await store.ArchiveForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, ct)));

        return app;
    }

    private static IResult ToResult(LoanMutationOutcome outcome) => outcome.Result switch
    {
        LoanMutationResult.Success => Results.Ok(outcome.Loan),
        LoanMutationResult.NotFound => Results.NotFound(),
        LoanMutationResult.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
        LoanMutationResult.Invalid => Results.BadRequest(new { error = outcome.Error ?? "Invalid loan." }),
        _ => Results.StatusCode(StatusCodes.Status409Conflict)
    };

    private static IResult ToResult(LoanMutationResult result) => result switch
    {
        LoanMutationResult.Success => Results.NoContent(),
        LoanMutationResult.NotFound => Results.NotFound(),
        LoanMutationResult.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
        LoanMutationResult.Invalid => Results.BadRequest(),
        _ => Results.StatusCode(StatusCodes.Status409Conflict)
    };
}
