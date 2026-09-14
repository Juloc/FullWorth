using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Portfolio;

public static class WealthEndpoints
{
    public static IEndpointRouteBuilder MapWealthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/wealth").WithTags("Wealth");

        group.MapGet("/overview", async (
            Guid fullWorthSpaceId,
            string? currency,
            CurrentUserContext currentUser,
            WealthOverviewService service,
            CancellationToken ct) =>
            ToResult(await service.GetOverviewForUserAsync(
                currentUser.RequireUserId(), fullWorthSpaceId, currency, ct)));

        group.MapGet("/history", async (
            Guid fullWorthSpaceId,
            DateOnly? from,
            DateOnly? to,
            string? currency,
            CurrentUserContext currentUser,
            WealthOverviewService service,
            CancellationToken ct) =>
            ToResult(await service.GetHistoryForUserAsync(
                currentUser.RequireUserId(), fullWorthSpaceId, from, to, currency, ct)));

        group.MapGet("/booking-activity", async (
            Guid fullWorthSpaceId,
            DateOnly? from,
            DateOnly? to,
            CurrentUserContext currentUser,
            WealthOverviewService service,
            CancellationToken ct) =>
            ToResult(await service.GetBookingActivityForUserAsync(
                currentUser.RequireUserId(), fullWorthSpaceId, from, to, ct)));

        return app;
    }

    private static IResult ToResult(WealthOverviewOutcome outcome) => outcome.Status switch
    {
        WealthRequestStatus.Success => Results.Ok(outcome.Overview),
        WealthRequestStatus.NotFound => Results.NotFound(),
        WealthRequestStatus.Invalid => Results.BadRequest(new { error = outcome.Error ?? "Invalid wealth request." }),
        _ => Results.StatusCode(StatusCodes.Status409Conflict)
    };

    private static IResult ToResult(WealthHistoryOutcome outcome) => outcome.Status switch
    {
        WealthRequestStatus.Success => Results.Ok(outcome.History),
        WealthRequestStatus.NotFound => Results.NotFound(),
        WealthRequestStatus.Invalid => Results.BadRequest(new { error = outcome.Error ?? "Invalid wealth history request." }),
        _ => Results.StatusCode(StatusCodes.Status409Conflict)
    };

    private static IResult ToResult(WealthBookingActivityOutcome outcome) => outcome.Status switch
    {
        WealthRequestStatus.Success => Results.Ok(outcome.Activity),
        WealthRequestStatus.NotFound => Results.NotFound(),
        WealthRequestStatus.Invalid => Results.BadRequest(new { error = outcome.Error ?? "Invalid wealth booking-activity request." }),
        _ => Results.StatusCode(StatusCodes.Status409Conflict)
    };
}
