using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Portfolio;

public static class RemainingAssetEndpoints
{
    public static IEndpointRouteBuilder MapRemainingSpecializedAssetEndpoints(this IEndpointRouteBuilder app)
    {
        var collectible = app.MapGroup("/api/assets/{assetId:guid}/collectible").WithTags("Collectible assets");
        collectible.MapGet("/", async (
            Guid assetId, Guid fullWorthSpaceId, CurrentUserContext user, RemainingAssetStore detailStore, ReceivablePaymentStore paymentStore, CancellationToken ct) =>
            ToResult(await detailStore.GetCollectibleAsync(user.RequireUserId(), fullWorthSpaceId, assetId, ct)));
        collectible.MapPut("/", async (
            Guid assetId, Guid fullWorthSpaceId, CollectibleDetailWrite request, CurrentUserContext user, RemainingAssetStore detailStore, ReceivablePaymentStore paymentStore, CancellationToken ct) =>
            ToResult(await detailStore.PutCollectibleAsync(user.RequireUserId(), fullWorthSpaceId, assetId, request, ct)));

        var receivable = app.MapGroup("/api/assets/{assetId:guid}/receivable").WithTags("Receivable assets");
        receivable.MapGet("/", async (
            Guid assetId, Guid fullWorthSpaceId, CurrentUserContext user, RemainingAssetStore detailStore, ReceivablePaymentStore paymentStore, CancellationToken ct) =>
            ToResult(await detailStore.GetReceivableAsync(user.RequireUserId(), fullWorthSpaceId, assetId, ct)));
        receivable.MapPut("/", async (
            Guid assetId, Guid fullWorthSpaceId, ReceivableDetailWrite request, CurrentUserContext user, RemainingAssetStore detailStore, ReceivablePaymentStore paymentStore, CancellationToken ct) =>
            ToResult(await detailStore.PutReceivableAsync(user.RequireUserId(), fullWorthSpaceId, assetId, request, ct)));
        receivable.MapGet("/payments", async (
            Guid assetId, Guid fullWorthSpaceId, CurrentUserContext user, RemainingAssetStore detailStore, ReceivablePaymentStore paymentStore, CancellationToken ct) =>
            ToResult(await paymentStore.ListAsync(user.RequireUserId(), fullWorthSpaceId, assetId, ct)));
        receivable.MapPost("/payments", async (
            Guid assetId, Guid fullWorthSpaceId, ReceivablePaymentWrite request, CurrentUserContext user, RemainingAssetStore detailStore, ReceivablePaymentStore paymentStore, CancellationToken ct) =>
            ToResult(await paymentStore.CreateAsync(user.RequireUserId(), fullWorthSpaceId, assetId, request, ct)));
        receivable.MapPost("/write-down", async (
            Guid assetId, Guid fullWorthSpaceId, ReceivableWriteDownRequest request, CurrentUserContext user, RemainingAssetStore detailStore, ReceivablePaymentStore paymentStore, CancellationToken ct) =>
            ToResult(await paymentStore.WriteDownAsync(user.RequireUserId(), fullWorthSpaceId, assetId, request, ct)));

        var business = app.MapGroup("/api/assets/{assetId:guid}/business-interest").WithTags("Business interest assets");
        business.MapGet("/", async (
            Guid assetId, Guid fullWorthSpaceId, CurrentUserContext user, RemainingAssetStore detailStore, ReceivablePaymentStore paymentStore, CancellationToken ct) =>
            ToResult(await detailStore.GetBusinessInterestAsync(user.RequireUserId(), fullWorthSpaceId, assetId, ct)));
        business.MapPut("/", async (
            Guid assetId, Guid fullWorthSpaceId, BusinessInterestDetailWrite request, CurrentUserContext user, RemainingAssetStore detailStore, ReceivablePaymentStore paymentStore, CancellationToken ct) =>
            ToResult(await detailStore.PutBusinessInterestAsync(user.RequireUserId(), fullWorthSpaceId, assetId, request, ct)));

        var pension = app.MapGroup("/api/assets/{assetId:guid}/insurance-pension").WithTags("Insurance and pension assets");
        pension.MapGet("/", async (
            Guid assetId, Guid fullWorthSpaceId, CurrentUserContext user, RemainingAssetStore detailStore, ReceivablePaymentStore paymentStore, CancellationToken ct) =>
            ToResult(await detailStore.GetInsurancePensionAsync(user.RequireUserId(), fullWorthSpaceId, assetId, ct)));
        pension.MapPut("/", async (
            Guid assetId, Guid fullWorthSpaceId, InsurancePensionDetailWrite request, CurrentUserContext user, RemainingAssetStore detailStore, ReceivablePaymentStore paymentStore, CancellationToken ct) =>
            ToResult(await detailStore.PutInsurancePensionAsync(user.RequireUserId(), fullWorthSpaceId, assetId, request, ct)));

        return app;
    }

    private static IResult ToResult<T>(SpecializedAssetOutcome<T> outcome) => outcome.Result switch
    {
        SpecializedAssetMutationResult.Success => Results.Ok(outcome.Value),
        SpecializedAssetMutationResult.NotFound => Results.NotFound(),
        SpecializedAssetMutationResult.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
        SpecializedAssetMutationResult.Invalid => Results.BadRequest(new { error = outcome.Error ?? "Invalid specialized asset request." }),
        SpecializedAssetMutationResult.Conflict => Results.Conflict(new { error = outcome.Error ?? "Specialized asset conflict." }),
        _ => Results.StatusCode(StatusCodes.Status409Conflict)
    };
}
