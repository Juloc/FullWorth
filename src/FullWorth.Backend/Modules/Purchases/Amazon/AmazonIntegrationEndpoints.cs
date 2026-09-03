using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Purchases.Amazon;

public static class AmazonIntegrationEndpoints
{
    public static IEndpointRouteBuilder MapAmazonIntegrationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/purchases/amazon").WithTags("Purchases");

        group.MapGet("/status", async (
            Guid fullWorthSpaceId,
            CurrentUserContext currentUser,
            PurchaseAuthorizationStore authorization,
            AmazonOrderSyncService service,
            CancellationToken ct) =>
            await authorization.IsFullWorthSpaceMemberAsync(currentUser.RequireUserId(), fullWorthSpaceId, ct)
                ? Results.Ok(await service.GetStatusAsync(currentUser.RequireUserId(), fullWorthSpaceId, ct))
                : Results.NotFound());

        group.MapPost("/connect/start", async (
            Guid fullWorthSpaceId,
            AmazonLoginStartRequest request,
            CurrentUserContext currentUser,
            AmazonOrderSyncService service,
            CancellationToken ct) =>
            LoginResult(await service.StartLoginAsync(currentUser.RequireUserId(), fullWorthSpaceId, request, ct)));

        group.MapPost("/connect/{challengeId:guid}/complete", async (
            Guid challengeId,
            Guid fullWorthSpaceId,
            AmazonLoginCompleteRequest request,
            CurrentUserContext currentUser,
            AmazonOrderSyncService service,
            CancellationToken ct) =>
            LoginResult(await service.CompleteLoginAsync(currentUser.RequireUserId(), fullWorthSpaceId, challengeId, request, ct)));

        group.MapDelete("/connection", async (
            Guid fullWorthSpaceId,
            CurrentUserContext currentUser,
            PurchaseAuthorizationStore authorization,
            AmazonOrderSyncService service,
            CancellationToken ct) =>
        {
            if (!await authorization.IsFullWorthSpaceMemberAsync(currentUser.RequireUserId(), fullWorthSpaceId, ct)) return Results.NotFound();
            _ = await service.DisconnectAsync(currentUser.RequireUserId(), fullWorthSpaceId, ct);
            return Results.NoContent();
        });

        group.MapPost("/sync", async (
            Guid fullWorthSpaceId,
            AmazonSyncRequest? request,
            CurrentUserContext currentUser,
            PurchaseAuthorizationStore authorization,
            AmazonOrderSyncService service,
            CancellationToken ct) =>
        {
            if (!await authorization.IsFullWorthSpaceMemberAsync(currentUser.RequireUserId(), fullWorthSpaceId, ct)) return Results.NotFound();
            var result = await service.SyncAsync(currentUser.RequireUserId(), fullWorthSpaceId, request?.HistoryDays, ct);
            return result.State switch
            {
                AmazonSyncState.Success => Results.Ok(result.Result),
                AmazonSyncState.NotConnected => Results.Conflict(new { error = result.Error }),
                AmazonSyncState.ReauthenticationRequired => Results.Conflict(new { error = result.Error, requiresReauth = true }),
                _ => Results.Problem(result.Error ?? "Amazon sync failed.", statusCode: StatusCodes.Status502BadGateway)
            };
        });

        app.MapGet("/api/purchases/{id:guid}/amazon-payment-candidates", async (
            Guid id,
            Guid fullWorthSpaceId,
            CurrentUserContext currentUser,
            AmazonOrderSyncService service,
            CancellationToken ct) =>
        {
            var candidates = await service.GetPaymentCandidatesAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, ct);
            return candidates is null ? Results.NotFound() : Results.Ok(candidates);
        }).WithTags("Purchases");

        app.MapPost("/api/purchases/{id:guid}/amazon-payment-links", async (
            Guid id,
            Guid fullWorthSpaceId,
            AmazonPaymentLinkRequest request,
            CurrentUserContext currentUser,
            AmazonOrderSyncService service,
            CancellationToken ct) =>
            await service.LinkPaymentAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, request.TransactionId, request.Confidence, request.AllocatedAmount, ct)
                ? Results.NoContent() : Results.BadRequest(new { error = "The Amazon payment allocation is invalid or unavailable." })).WithTags("Purchases");

        app.MapDelete("/api/purchases/{id:guid}/amazon-payment-links/{transactionId:guid}", async (
            Guid id,
            Guid transactionId,
            Guid fullWorthSpaceId,
            CurrentUserContext currentUser,
            AmazonOrderSyncService service,
            CancellationToken ct) =>
            await service.UnlinkPaymentAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, transactionId, ct)
                ? Results.NoContent() : Results.NotFound()).WithTags("Purchases");

        app.MapPut("/api/purchases/{id:guid}/amazon-nonbank-payment", async (
            Guid id,
            Guid fullWorthSpaceId,
            AmazonNonBankPaymentRequest request,
            CurrentUserContext currentUser,
            AmazonOrderSyncService service,
            CancellationToken ct) =>
            await service.SetNonBankPaymentAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, request.Amount, ct)
                ? Results.NoContent() : Results.BadRequest(new { error = "The non-bank Amazon payment amount is invalid." })).WithTags("Purchases");

        app.MapGet("/api/purchases/{id:guid}/amazon-refunds/{refundId:guid}/candidates", async (
            Guid id,
            Guid refundId,
            Guid fullWorthSpaceId,
            CurrentUserContext currentUser,
            AmazonOrderSyncService service,
            CancellationToken ct) =>
        {
            var candidates = await service.GetRefundCandidatesAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, refundId, ct);
            return candidates is null ? Results.NotFound() : Results.Ok(candidates);
        }).WithTags("Purchases");

        app.MapPost("/api/purchases/{id:guid}/amazon-refunds/{refundId:guid}/link", async (
            Guid id,
            Guid refundId,
            Guid fullWorthSpaceId,
            LinkPurchaseRequest request,
            CurrentUserContext currentUser,
            AmazonOrderSyncService service,
            CancellationToken ct) =>
            await service.LinkRefundAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, refundId, request.TransactionId, request.Confidence, ct)
                ? Results.NoContent() : Results.BadRequest(new { error = "The Amazon refund link is invalid or unavailable." })).WithTags("Purchases");

        app.MapDelete("/api/purchases/{id:guid}/amazon-refunds/{refundId:guid}/link", async (
            Guid id,
            Guid refundId,
            Guid fullWorthSpaceId,
            CurrentUserContext currentUser,
            AmazonOrderSyncService service,
            CancellationToken ct) =>
            await service.UnlinkRefundAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, refundId, ct)
                ? Results.NoContent() : Results.NotFound()).WithTags("Purchases");

        app.MapGet("/api/purchases/{id:guid}/amazon-details", async (
            Guid id,
            Guid fullWorthSpaceId,
            CurrentUserContext currentUser,
            AmazonOrderSyncService service,
            CancellationToken ct) =>
        {
            var details = await service.GetDetailsAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, ct);
            return details is null ? Results.NotFound() : Results.Ok(details);
        }).WithTags("Purchases");

        return app;
    }

    private static IResult LoginResult(AmazonLoginResult result) => result.Status switch
    {
        "connected" or "otp" or "approval" => Results.Ok(result),
        "not_found" => Results.NotFound(),
        "invalid" or "expired" => Results.BadRequest(result),
        "disabled" or "blocked" => Results.Conflict(result),
        _ => Results.Problem(result.Message ?? "Amazon sign-in failed.", statusCode: StatusCodes.Status502BadGateway)
    };
}