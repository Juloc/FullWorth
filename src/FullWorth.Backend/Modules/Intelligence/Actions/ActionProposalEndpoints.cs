using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Intelligence.Actions;

public static class ActionProposalEndpoints
{
    public static IEndpointRouteBuilder MapActionProposalEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/action-proposals").WithTags("Action proposals");

        group.MapGet("/", async (
            Guid fullWorthSpaceId,
            string? state,
            int? limit,
            CurrentUserContext currentUser,
            ActionProposalService service,
            CancellationToken ct) =>
        {
            try
            {
                var rows = await service.ListAsync(
                    currentUser.RequireUserId(),
                    fullWorthSpaceId,
                    state,
                    limit ?? 50,
                    ct);
                return rows is null ? Results.NotFound() : Results.Ok(rows);
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
            ActionProposalService service,
            CancellationToken ct) =>
        {
            var proposal = await service.GetAsync(
                currentUser.RequireUserId(),
                fullWorthSpaceId,
                id,
                ct);
            return proposal is null ? Results.NotFound() : Results.Ok(proposal);
        });

        group.MapPost("/", async (
            Guid fullWorthSpaceId,
            CreateActionProposalRequest request,
            CurrentUserContext currentUser,
            ActionProposalService service,
            CancellationToken ct) =>
            ToResult(await service.CreateAsync(
                currentUser.RequireUserId(),
                fullWorthSpaceId,
                request,
                ct)));

        group.MapPost("/{id:guid}/preview", async (
            Guid id,
            Guid fullWorthSpaceId,
            CurrentUserContext currentUser,
            ActionProposalService service,
            CancellationToken ct) =>
            ToResult(await service.RefreshPreviewAsync(
                currentUser.RequireUserId(),
                fullWorthSpaceId,
                id,
                ct)));

        group.MapPost("/{id:guid}/execute", async (
            Guid id,
            Guid fullWorthSpaceId,
            ExecuteActionProposalRequest request,
            CurrentUserContext currentUser,
            ActionProposalService service,
            CancellationToken ct) =>
            ToResult(await service.ExecuteAsync(
                currentUser.RequireUserId(),
                fullWorthSpaceId,
                id,
                request,
                ct)));

        group.MapPost("/{id:guid}/reject", async (
            Guid id,
            Guid fullWorthSpaceId,
            CurrentUserContext currentUser,
            ActionProposalService service,
            CancellationToken ct) =>
            ToResult(await service.RejectAsync(
                currentUser.RequireUserId(),
                fullWorthSpaceId,
                id,
                ct)));

        return app;
    }

    private static IResult ToResult(ActionProposalOperationOutcome outcome) => outcome.Result switch
    {
        ActionProposalOperationResult.Success => Results.Ok(new
        {
            proposal = outcome.Proposal,
            alreadyApplied = outcome.AlreadyApplied
        }),
        ActionProposalOperationResult.NotFound or ActionProposalOperationResult.Disabled => Results.NotFound(),
        ActionProposalOperationResult.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
        ActionProposalOperationResult.Invalid => Results.BadRequest(new
        {
            error = outcome.Error ?? "Invalid action proposal.",
            proposal = outcome.Proposal
        }),
        ActionProposalOperationResult.Conflict => Results.Conflict(new
        {
            error = outcome.Error ?? "Action proposal state changed.",
            proposal = outcome.Proposal
        }),
        _ => Results.StatusCode(StatusCodes.Status409Conflict)
    };
}
