using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Purchases;

public static class PurchaseLifecycleEndpoints
{
    public static IEndpointRouteBuilder MapPurchaseLifecycleEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/purchases").WithTags("Purchases");
        group.MapPost("/manual", async (Guid fullWorthSpaceId, PurchaseDraftWrite request, FullWorth.Backend.Security.CurrentUserContext user, PurchaseLifecycleService service, CancellationToken ct) => Outcome(await service.CreateAsync(user.RequireUserId(), fullWorthSpaceId, request, ct), true));
        group.MapPatch("/{id:guid}/summary", async (Guid id, Guid fullWorthSpaceId, PurchaseSummaryPatch request, FullWorth.Backend.Security.CurrentUserContext user, PurchaseLifecycleService service, CancellationToken ct) => Outcome(await service.UpdateSummaryAsync(user.RequireUserId(), fullWorthSpaceId, id, request, ct)));
        group.MapPost("/{id:guid}/confirm", async (Guid id, Guid fullWorthSpaceId, ConfirmPurchaseRequest request, FullWorth.Backend.Security.CurrentUserContext user, PurchaseLifecycleService service, CancellationToken ct) => Outcome(await service.ConfirmAsync(user.RequireUserId(), fullWorthSpaceId, id, request, ct)));
        group.MapPut("/{id:guid}/bookmark", async (Guid id, Guid fullWorthSpaceId, BookmarkPurchaseRequest request, FullWorth.Backend.Security.CurrentUserContext user, PurchaseLifecycleService service, CancellationToken ct) => Mutation(await service.SetBookmarkAsync(user.RequireUserId(), fullWorthSpaceId, id, request.Bookmarked, ct)));
        return app;
    }
    private static IResult Mutation(PurchaseMutationResult result) => result switch
    { PurchaseMutationResult.Success => Results.NoContent(), PurchaseMutationResult.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden), PurchaseMutationResult.Invalid => Results.BadRequest(), _ => Results.NotFound() };
    private static IResult Outcome((PurchaseMutationResult Result, object? Value, string? Error) outcome, bool created = false) => outcome.Result switch
    {
        PurchaseMutationResult.Success when created => Results.Created(string.Empty, outcome.Value), PurchaseMutationResult.Success => Results.Ok(outcome.Value),
        PurchaseMutationResult.Invalid when outcome.Value is not null => Results.Conflict(new { error = outcome.Error, detail = outcome.Value }),
        PurchaseMutationResult.Invalid => Results.BadRequest(new { error = outcome.Error }), PurchaseMutationResult.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden), _ => Results.NotFound()
    };
}
