using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Audit;
using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Portfolio;

public static class RealEstateOperationsEndpoints
{
    public static IEndpointRouteBuilder MapRealEstateOperationsEndpoints(this IEndpointRouteBuilder app)
    {
        var property = app.MapGroup("/api/assets/{assetId:guid}/real-estate").WithTags("Real estate operations");

        property.MapGet("/units", async (Guid assetId, Guid fullWorthSpaceId, CurrentUserContext user, FullWorthDbContext db, AuditService audit, CancellationToken ct) =>
            ToResult(await Rental(db, audit).ListUnitsAsync(user.RequireUserId(), fullWorthSpaceId, assetId, ct)));
        property.MapPost("/units", async (Guid assetId, Guid fullWorthSpaceId, PropertyUnitWrite request, CurrentUserContext user, FullWorthDbContext db, AuditService audit, CancellationToken ct) =>
            ToResult(await Rental(db, audit).CreateUnitAsync(user.RequireUserId(), fullWorthSpaceId, assetId, request, ct)));
        property.MapPut("/units/{unitId:guid}", async (Guid assetId, Guid unitId, Guid fullWorthSpaceId, PropertyUnitWrite request, CurrentUserContext user, FullWorthDbContext db, AuditService audit, CancellationToken ct) =>
            ToResult(await Rental(db, audit).UpdateUnitAsync(user.RequireUserId(), fullWorthSpaceId, assetId, unitId, request, ct)));
        property.MapDelete("/units/{unitId:guid}", async (Guid assetId, Guid unitId, Guid fullWorthSpaceId, CurrentUserContext user, FullWorthDbContext db, AuditService audit, CancellationToken ct) =>
            ToResult(await Rental(db, audit).DeactivateUnitAsync(user.RequireUserId(), fullWorthSpaceId, assetId, unitId, ct)));

        property.MapGet("/leases", async (Guid assetId, Guid fullWorthSpaceId, CurrentUserContext user, FullWorthDbContext db, AuditService audit, CancellationToken ct) =>
            ToResult(await Rental(db, audit).ListLeasesAsync(user.RequireUserId(), fullWorthSpaceId, assetId, ct)));
        property.MapPost("/leases", async (Guid assetId, Guid fullWorthSpaceId, RentalLeaseWrite request, CurrentUserContext user, FullWorthDbContext db, AuditService audit, CancellationToken ct) =>
            ToResult(await Rental(db, audit).CreateLeaseAsync(user.RequireUserId(), fullWorthSpaceId, assetId, request, ct)));
        property.MapPut("/leases/{leaseId:guid}", async (Guid assetId, Guid leaseId, Guid fullWorthSpaceId, RentalLeaseWrite request, CurrentUserContext user, FullWorthDbContext db, AuditService audit, CancellationToken ct) =>
            ToResult(await Rental(db, audit).UpdateLeaseAsync(user.RequireUserId(), fullWorthSpaceId, assetId, leaseId, request, ct)));
        property.MapDelete("/leases/{leaseId:guid}", async (Guid assetId, Guid leaseId, Guid fullWorthSpaceId, CurrentUserContext user, FullWorthDbContext db, AuditService audit, CancellationToken ct) =>
            ToResult(await Rental(db, audit).EndLeaseAsync(user.RequireUserId(), fullWorthSpaceId, assetId, leaseId, ct)));

        property.MapGet("/improvements", async (Guid assetId, Guid fullWorthSpaceId, CurrentUserContext user, FullWorthDbContext db, AuditService audit, CancellationToken ct) =>
            ToResult(await Operations(db, audit).ListImprovementsAsync(user.RequireUserId(), fullWorthSpaceId, assetId, ct)));
        property.MapPost("/improvements", async (Guid assetId, Guid fullWorthSpaceId, PropertyImprovementWrite request, CurrentUserContext user, FullWorthDbContext db, AuditService audit, CancellationToken ct) =>
            ToResult(await Operations(db, audit).CreateImprovementAsync(user.RequireUserId(), fullWorthSpaceId, assetId, request, ct)));
        property.MapPut("/improvements/{improvementId:guid}", async (Guid assetId, Guid improvementId, Guid fullWorthSpaceId, PropertyImprovementWrite request, CurrentUserContext user, FullWorthDbContext db, AuditService audit, CancellationToken ct) =>
            ToResult(await Operations(db, audit).UpdateImprovementAsync(user.RequireUserId(), fullWorthSpaceId, assetId, improvementId, request, ct)));
        property.MapDelete("/improvements/{improvementId:guid}", async (Guid assetId, Guid improvementId, Guid fullWorthSpaceId, CurrentUserContext user, FullWorthDbContext db, AuditService audit, CancellationToken ct) =>
            ToResult(await Operations(db, audit).DeleteImprovementAsync(user.RequireUserId(), fullWorthSpaceId, assetId, improvementId, ct)));
        property.MapPost("/improvements/{improvementId:guid}/cashflows", async (Guid assetId, Guid improvementId, Guid fullWorthSpaceId, ImprovementCashflowLinkWrite request, CurrentUserContext user, FullWorthDbContext db, AuditService audit, CancellationToken ct) =>
            ToResult(await Operations(db, audit).LinkImprovementCashflowAsync(user.RequireUserId(), fullWorthSpaceId, assetId, improvementId, request.CashflowEntryId, ct)));
        property.MapDelete("/improvements/{improvementId:guid}/cashflows/{cashflowId:guid}", async (Guid assetId, Guid improvementId, Guid cashflowId, Guid fullWorthSpaceId, CurrentUserContext user, FullWorthDbContext db, AuditService audit, CancellationToken ct) =>
            ToResult(await Operations(db, audit).UnlinkImprovementCashflowAsync(user.RequireUserId(), fullWorthSpaceId, assetId, improvementId, cashflowId, ct)));

        var cashflows = app.MapGroup("/api/assets/{assetId:guid}/cashflows").WithTags("Asset cashflows");
        cashflows.MapGet("/", async (Guid assetId, Guid fullWorthSpaceId, DateOnly? from, DateOnly? to, CurrentUserContext user, FullWorthDbContext db, AuditService audit, CancellationToken ct) =>
            ToResult(await Cashflows(db, audit).ListAsync(user.RequireUserId(), fullWorthSpaceId, assetId, from, to, ct)));
        cashflows.MapPost("/", async (Guid assetId, Guid fullWorthSpaceId, AssetCashflowWrite request, CurrentUserContext user, FullWorthDbContext db, AuditService audit, CancellationToken ct) =>
            ToResult(await Cashflows(db, audit).CreateAsync(user.RequireUserId(), fullWorthSpaceId, assetId, request, ct)));
        cashflows.MapPut("/{entryId:guid}", async (Guid assetId, Guid entryId, Guid fullWorthSpaceId, AssetCashflowWrite request, CurrentUserContext user, FullWorthDbContext db, AuditService audit, CancellationToken ct) =>
            ToResult(await Cashflows(db, audit).UpdateAsync(user.RequireUserId(), fullWorthSpaceId, assetId, entryId, request, ct)));
        cashflows.MapDelete("/{entryId:guid}", async (Guid assetId, Guid entryId, Guid fullWorthSpaceId, CurrentUserContext user, FullWorthDbContext db, AuditService audit, CancellationToken ct) =>
            ToResult(await Cashflows(db, audit).DeleteAsync(user.RequireUserId(), fullWorthSpaceId, assetId, entryId, ct)));

        var contracts = app.MapGroup("/api/assets/{assetId:guid}/recurring-contracts").WithTags("Asset recurring contracts");
        contracts.MapGet("/", async (Guid assetId, Guid fullWorthSpaceId, CurrentUserContext user, FullWorthDbContext db, AuditService audit, CancellationToken ct) =>
            ToResult(await Operations(db, audit).ListContractLinksAsync(user.RequireUserId(), fullWorthSpaceId, assetId, ct)));
        contracts.MapPost("/", async (Guid assetId, Guid fullWorthSpaceId, AssetRecurringContractLinkWrite request, CurrentUserContext user, FullWorthDbContext db, AuditService audit, CancellationToken ct) =>
            ToResult(await Operations(db, audit).CreateContractLinkAsync(user.RequireUserId(), fullWorthSpaceId, assetId, request, ct)));
        contracts.MapDelete("/{contractId:guid}", async (Guid assetId, Guid contractId, Guid fullWorthSpaceId, CurrentUserContext user, FullWorthDbContext db, AuditService audit, CancellationToken ct) =>
            ToResult(await Operations(db, audit).DeleteContractLinkAsync(user.RequireUserId(), fullWorthSpaceId, assetId, contractId, ct)));

        return app;
    }

    private static PropertyRentalStore Rental(FullWorthDbContext db, AuditService audit) => new(db, audit);
    private static AssetCashflowStore Cashflows(FullWorthDbContext db, AuditService audit) => new(db, audit);
    private static PropertyOperationsStore Operations(FullWorthDbContext db, AuditService audit) => new(db, audit);

    private static IResult ToResult<T>(RealEstateMutationOutcome<T> outcome) => outcome.Result switch
    {
        RealEstateMutationResult.Success => Results.Ok(outcome.Value),
        RealEstateMutationResult.NotFound => Results.NotFound(),
        RealEstateMutationResult.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
        RealEstateMutationResult.Invalid => Results.BadRequest(new { error = outcome.Error ?? "Invalid property operation." }),
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
