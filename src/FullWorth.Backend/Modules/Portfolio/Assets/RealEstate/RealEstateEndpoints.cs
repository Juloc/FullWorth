using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Audit;
using FullWorth.Backend.Modules.Fx;
using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Portfolio;

public static class RealEstateEndpoints
{
    public static IEndpointRouteBuilder MapRealEstateEndpoints(this IEndpointRouteBuilder app)
    {
        var property = app.MapGroup("/api/assets/{assetId:guid}/real-estate").WithTags("Real estate");

        property.MapGet("/", async (
            Guid assetId, Guid fullWorthSpaceId, CurrentUserContext user, FullWorthDbContext db,
            AuditService audit, CurrencyConverter fx, CancellationToken ct) =>
            ToResult(await Store(db, audit, fx).GetPropertyAsync(user.RequireUserId(), fullWorthSpaceId, assetId, ct)));

        property.MapPut("/", async (
            Guid assetId, Guid fullWorthSpaceId, RealEstateDetailWrite request, CurrentUserContext user, FullWorthDbContext db,
            AuditService audit, CurrencyConverter fx, CancellationToken ct) =>
            ToResult(await Store(db, audit, fx).UpsertDetailAsync(user.RequireUserId(), fullWorthSpaceId, assetId, request, ct)));

        property.MapGet("/metrics", async (
            Guid assetId, Guid fullWorthSpaceId, DateOnly? from, DateOnly? to, CurrentUserContext user, FullWorthDbContext db,
            AuditService audit, CurrencyConverter fx, CancellationToken ct) =>
        {
            var basis = await Store(db, audit, fx).GetMetricsAsync(user.RequireUserId(), fullWorthSpaceId, assetId, ct);
            if (basis.Result != RealEstateMutationResult.Success || basis.Value is null) return ToResult(basis);
            var enriched = await RealEstateRentalMetrics.EnrichAsync(db, fx, fullWorthSpaceId, assetId, basis.Value, from, to, ct);
            return Results.Ok(enriched);
        });

        property.MapGet("/acquisition-costs", async (
            Guid assetId, Guid fullWorthSpaceId, CurrentUserContext user, FullWorthDbContext db,
            AuditService audit, CurrencyConverter fx, CancellationToken ct) =>
            ToResult(await Store(db, audit, fx).ListCostsAsync(user.RequireUserId(), fullWorthSpaceId, assetId, ct)));

        property.MapPost("/acquisition-costs", async (
            Guid assetId, Guid fullWorthSpaceId, RealEstateAcquisitionCostWrite request, CurrentUserContext user, FullWorthDbContext db,
            AuditService audit, CurrencyConverter fx, CancellationToken ct) =>
            ToResult(await Store(db, audit, fx).CreateCostAsync(user.RequireUserId(), fullWorthSpaceId, assetId, request, ct)));

        property.MapPut("/acquisition-costs/{costId:guid}", async (
            Guid assetId, Guid costId, Guid fullWorthSpaceId, RealEstateAcquisitionCostWrite request, CurrentUserContext user,
            FullWorthDbContext db, AuditService audit, CancellationToken ct) =>
            ToResult(await UpdateStore(db, audit).UpdateCostAsync(user.RequireUserId(), fullWorthSpaceId, assetId, costId, request, ct)));

        property.MapDelete("/acquisition-costs/{costId:guid}", async (
            Guid assetId, Guid costId, Guid fullWorthSpaceId, CurrentUserContext user, FullWorthDbContext db,
            AuditService audit, CurrencyConverter fx, CancellationToken ct) =>
            ToResult(await Store(db, audit, fx).DeleteCostAsync(user.RequireUserId(), fullWorthSpaceId, assetId, costId, ct)));

        var debt = app.MapGroup("/api/assets/{assetId:guid}/debts").WithTags("Asset debt");

        debt.MapGet("/", async (
            Guid assetId, Guid fullWorthSpaceId, CurrentUserContext user, FullWorthDbContext db,
            AuditService audit, CurrencyConverter fx, CancellationToken ct) =>
            ToResult(await Store(db, audit, fx).ListDebtLinksAsync(user.RequireUserId(), fullWorthSpaceId, assetId, ct)));

        debt.MapPost("/", async (
            Guid assetId, Guid fullWorthSpaceId, AssetDebtLinkWrite request, CurrentUserContext user, FullWorthDbContext db,
            AuditService audit, CurrencyConverter fx, CancellationToken ct) =>
            ToResult(await Store(db, audit, fx).CreateDebtLinkAsync(user.RequireUserId(), fullWorthSpaceId, assetId, request, ct)));

        debt.MapPut("/{linkId:guid}", async (
            Guid assetId, Guid linkId, Guid fullWorthSpaceId, AssetDebtLinkWrite request, CurrentUserContext user,
            FullWorthDbContext db, AuditService audit, CancellationToken ct) =>
            ToResult(await UpdateStore(db, audit).UpdateDebtLinkAsync(user.RequireUserId(), fullWorthSpaceId, assetId, linkId, request, ct)));

        debt.MapDelete("/{linkId:guid}", async (
            Guid assetId, Guid linkId, Guid fullWorthSpaceId, CurrentUserContext user, FullWorthDbContext db,
            AuditService audit, CurrencyConverter fx, CancellationToken ct) =>
            ToResult(await Store(db, audit, fx).DeleteDebtLinkAsync(user.RequireUserId(), fullWorthSpaceId, assetId, linkId, ct)));

        app.MapRealEstateOperationsEndpoints();
        app.MapRealEstateAdvancedEndpoints();
        return app;
    }

    private static RealEstateStore Store(FullWorthDbContext db, AuditService audit, CurrencyConverter fx) => new(db, audit, fx);
    private static RealEstateUpdateStore UpdateStore(FullWorthDbContext db, AuditService audit) => new(db, audit);

    private static IResult ToResult<T>(RealEstateMutationOutcome<T> outcome) => outcome.Result switch
    {
        RealEstateMutationResult.Success => Results.Ok(outcome.Value),
        RealEstateMutationResult.NotFound => Results.NotFound(),
        RealEstateMutationResult.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
        RealEstateMutationResult.Invalid => Results.BadRequest(new { error = outcome.Error ?? "Invalid real-estate request." }),
        _ => Results.StatusCode(StatusCodes.Status409Conflict)
    };

    private static IResult ToResult(RealEstateMutationResult result) => result switch
    {
        RealEstateMutationResult.Success => Results.NoContent(),
        RealEstateMutationResult.NotFound => Results.NotFound(),
        RealEstateMutationResult.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
        RealEstateMutationResult.Invalid => Results.BadRequest(),
        _ => Results.StatusCode(StatusCodes.Status409Conflict)
    };
}
