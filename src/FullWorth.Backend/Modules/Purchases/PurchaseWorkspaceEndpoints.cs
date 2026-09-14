using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Purchases;

public static class PurchaseWorkspaceEndpoints
{
    public static IEndpointRouteBuilder MapPurchaseWorkspaceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/purchases").WithTags("Purchases");
        group.MapGet("/paged", async (Guid fullWorthSpaceId, string? query, DateOnly? from, DateOnly? to, Guid? categoryId, Guid? productId, string? reviewState, bool? linked, bool? bookmarked, decimal? minAmount, decimal? maxAmount, string? source, int? offset, int? limit, FullWorth.Backend.Security.CurrentUserContext user, PurchaseWorkspaceService service, CancellationToken ct) =>
        { var value = await service.ListPagedAsync(user.RequireUserId(), fullWorthSpaceId, query, from, to, categoryId, productId, reviewState, linked, bookmarked, minAmount, maxAmount, source, offset ?? 0, limit ?? 100, ct); return value is null ? Results.NotFound() : Results.Ok(value); });
        group.MapGet("/{id:guid}/workspace", async (Guid id, Guid fullWorthSpaceId, FullWorth.Backend.Security.CurrentUserContext user, PurchaseWorkspaceService service, CancellationToken ct) => { var value = await service.GetWorkspaceAsync(user.RequireUserId(), fullWorthSpaceId, id, ct); return value is null ? Results.NotFound() : Results.Ok(value); });
        group.MapDelete("/{id:guid}", async (Guid id, Guid fullWorthSpaceId, FullWorth.Backend.Security.CurrentUserContext user, PurchaseWorkspaceService service, CancellationToken ct) => Mutation(await service.DeletePurchaseAsync(user.RequireUserId(), fullWorthSpaceId, id, ct)));
        group.MapPost("/{id:guid}/items", async (Guid id, Guid fullWorthSpaceId, PurchaseItemPatch request, FullWorth.Backend.Security.CurrentUserContext user, PurchaseWorkspaceService service, CancellationToken ct) => Outcome(await service.AddItemAsync(user.RequireUserId(), fullWorthSpaceId, id, request, ct), true));
        group.MapPatch("/{id:guid}/items/{itemId:guid}", async (Guid id, Guid itemId, Guid fullWorthSpaceId, PurchaseItemPatch request, FullWorth.Backend.Security.CurrentUserContext user, PurchaseWorkspaceService service, CancellationToken ct) => Outcome(await service.UpdateItemAsync(user.RequireUserId(), fullWorthSpaceId, id, itemId, request, ct)));
        group.MapDelete("/{id:guid}/items/{itemId:guid}", async (Guid id, Guid itemId, Guid fullWorthSpaceId, FullWorth.Backend.Security.CurrentUserContext user, PurchaseWorkspaceService service, CancellationToken ct) => Mutation(await service.DeleteItemAsync(user.RequireUserId(), fullWorthSpaceId, id, itemId, ct)));
        group.MapPut("/{id:guid}/items/reorder", async (Guid id, Guid fullWorthSpaceId, List<Guid> itemIds, FullWorth.Backend.Security.CurrentUserContext user, PurchaseWorkspaceService service, CancellationToken ct) => Mutation(await service.ReorderItemsAsync(user.RequireUserId(), fullWorthSpaceId, id, itemIds, ct)));
        group.MapPost("/{id:guid}/items/{itemId:guid}/match-product", async (Guid id, Guid itemId, Guid productId, Guid fullWorthSpaceId, FullWorth.Backend.Security.CurrentUserContext user, PurchaseWorkspaceService service, CancellationToken ct) => Mutation(await service.MatchItemProductAsync(user.RequireUserId(), fullWorthSpaceId, id, itemId, productId, ct)));
        group.MapPost("/{id:guid}/items/{itemId:guid}/unlink-product", async (Guid id, Guid itemId, Guid fullWorthSpaceId, FullWorth.Backend.Security.CurrentUserContext user, PurchaseWorkspaceService service, CancellationToken ct) => Mutation(await service.UnlinkItemProductAsync(user.RequireUserId(), fullWorthSpaceId, id, itemId, ct)));
        group.MapGet("/{id:guid}/payments", async (Guid id, Guid fullWorthSpaceId, FullWorth.Backend.Security.CurrentUserContext user, PurchaseWorkspaceService service, CancellationToken ct) => { var value = await service.PaymentsAsync(user.RequireUserId(), fullWorthSpaceId, id, ct); return value is null ? Results.NotFound() : Results.Ok(value); });
        group.MapGet("/{id:guid}/payment-candidates", async (Guid id, Guid fullWorthSpaceId, FullWorth.Backend.Security.CurrentUserContext user, PurchaseWorkspaceService service, CancellationToken ct) => { var value = await service.PaymentCandidatesAsync(user.RequireUserId(), fullWorthSpaceId, id, ct); return value is null ? Results.NotFound() : Results.Ok(value); });
        group.MapPost("/{id:guid}/payments", async (Guid id, Guid fullWorthSpaceId, PurchasePaymentWrite request, FullWorth.Backend.Security.CurrentUserContext user, PurchaseWorkspaceService service, CancellationToken ct) => Outcome(await service.AddPaymentAsync(user.RequireUserId(), fullWorthSpaceId, id, request, ct), true));
        group.MapPatch("/{id:guid}/payments/{linkId:guid}", async (Guid id, Guid linkId, Guid fullWorthSpaceId, PurchasePaymentPatch request, FullWorth.Backend.Security.CurrentUserContext user, PurchaseWorkspaceService service, CancellationToken ct) => Outcome(await service.UpdatePaymentAsync(user.RequireUserId(), fullWorthSpaceId, id, linkId, request, ct)));
        group.MapDelete("/{id:guid}/payments/{linkId:guid}", async (Guid id, Guid linkId, Guid fullWorthSpaceId, FullWorth.Backend.Security.CurrentUserContext user, PurchaseWorkspaceService service, CancellationToken ct) => Mutation(await service.DeletePaymentAsync(user.RequireUserId(), fullWorthSpaceId, id, linkId, ct)));
        group.MapPost("/{id:guid}/payments/auto-link", async (Guid id, Guid fullWorthSpaceId, FullWorth.Backend.Security.CurrentUserContext user, PurchaseWorkspaceService service, CancellationToken ct) => { var value = await service.AutoLinkAsync(user.RequireUserId(), fullWorthSpaceId, id, ct); return value.Result == PurchaseMutationResult.NotFound ? Results.NotFound() : Results.Ok(new { value.Linked }); });
        group.MapPost("/{id:guid}/reconciliation/accept-difference", async (Guid id, Guid fullWorthSpaceId, DifferenceAcceptanceWrite request, FullWorth.Backend.Security.CurrentUserContext user, PurchaseWorkspaceService service, CancellationToken ct) => Outcome(await service.AcceptDifferenceAsync(user.RequireUserId(), fullWorthSpaceId, id, request, ct), true));
        group.MapDelete("/{id:guid}/reconciliation/accept-difference/{kind}", async (Guid id, string kind, Guid fullWorthSpaceId, FullWorth.Backend.Security.CurrentUserContext user, PurchaseWorkspaceService service, CancellationToken ct) => Mutation(await service.ClearDifferenceAsync(user.RequireUserId(), fullWorthSpaceId, id, kind, ct)));
        group.MapPut("/{id:guid}/visibility", async (Guid id, Guid fullWorthSpaceId, PurchaseVisibilityWrite request, FullWorth.Backend.Security.CurrentUserContext user, PurchaseWorkspaceService service, CancellationToken ct) => Mutation(await service.SetVisibilityAsync(user.RequireUserId(), fullWorthSpaceId, id, request, ct)));
        group.MapPost("/{id:guid}/items/{itemId:guid}/returns", async (Guid id, Guid itemId, Guid fullWorthSpaceId, PurchaseReturnWrite request, FullWorth.Backend.Security.CurrentUserContext user, PurchaseWorkspaceService service, CancellationToken ct) => Outcome(await service.RecordReturnAsync(user.RequireUserId(), fullWorthSpaceId, id, itemId, request, ct), true));

        app.MapPost("/api/transactions/{transactionId:guid}/allocations/from-purchase/{purchaseId:guid}", async (Guid transactionId, Guid purchaseId, Guid fullWorthSpaceId, PurchaseAllocationImportRequest request, FullWorth.Backend.Security.CurrentUserContext user, PurchaseWorkspaceService service, CancellationToken ct) => Outcome(await service.ImportAllocationsFromPurchaseAsync(user.RequireUserId(), fullWorthSpaceId, transactionId, purchaseId, request, ct)));
        app.MapPost("/api/transactions/{transactionId:guid}/allocations/clear", async (Guid transactionId, Guid fullWorthSpaceId, FullWorth.Backend.Security.CurrentUserContext user, PurchaseWorkspaceService service, CancellationToken ct) => Mutation(await service.ClearAllocationsAsync(user.RequireUserId(), fullWorthSpaceId, transactionId, ct)));
        return app;
    }

    private static IResult Mutation(PurchaseMutationResult result) => result switch
    {
        PurchaseMutationResult.Success => Results.NoContent(), PurchaseMutationResult.Invalid => Results.BadRequest(),
        PurchaseMutationResult.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden), _ => Results.NotFound()
    };
    private static IResult Outcome((PurchaseMutationResult Result, object? Value, string? Error) outcome, bool created = false) => outcome.Result switch
    {
        PurchaseMutationResult.Success when created => Results.Created(string.Empty, outcome.Value), PurchaseMutationResult.Success => Results.Ok(outcome.Value),
        PurchaseMutationResult.Invalid when outcome.Value is not null => Results.Conflict(new { error = outcome.Error, detail = outcome.Value }),
        PurchaseMutationResult.Invalid => Results.BadRequest(new { error = outcome.Error }), PurchaseMutationResult.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden), _ => Results.NotFound()
    };
}
