using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Audit;
using FullWorth.Backend.Modules.Purchases;
using FullWorth.Backend.Security;
using Microsoft.Extensions.Options;

namespace FullWorth.Backend.Modules.Portfolio;

public static class RealEstateAdvancedEndpoints
{
    public static IEndpointRouteBuilder MapRealEstateAdvancedEndpoints(this IEndpointRouteBuilder app)
    {
        var energy = app.MapGroup("/api/assets/{assetId:guid}/real-estate/energy-certificates").WithTags("Real estate energy");
        energy.MapGet("/", async (Guid assetId, Guid fullWorthSpaceId, CurrentUserContext user, FullWorthDbContext db, AuditService audit, IOptions<PurchaseStorageOptions> storage, CancellationToken ct) =>
            ToResult(await Store(db,audit,storage).ListEnergyAsync(user.RequireUserId(), fullWorthSpaceId, assetId, ct)));
        energy.MapPost("/", async (Guid assetId, Guid fullWorthSpaceId, PropertyEnergyCertificateWrite request, CurrentUserContext user, FullWorthDbContext db, AuditService audit, IOptions<PurchaseStorageOptions> storage, CancellationToken ct) =>
            ToResult(await Store(db,audit,storage).CreateEnergyAsync(user.RequireUserId(), fullWorthSpaceId, assetId, request, ct)));
        energy.MapPut("/{certificateId:guid}", async (Guid assetId, Guid certificateId, Guid fullWorthSpaceId, PropertyEnergyCertificateWrite request, CurrentUserContext user, FullWorthDbContext db, AuditService audit, IOptions<PurchaseStorageOptions> storage, CancellationToken ct) =>
            ToResult(await Store(db,audit,storage).UpdateEnergyAsync(user.RequireUserId(), fullWorthSpaceId, assetId, certificateId, request, ct)));
        energy.MapDelete("/{certificateId:guid}", async (Guid assetId, Guid certificateId, Guid fullWorthSpaceId, CurrentUserContext user, FullWorthDbContext db, AuditService audit, IOptions<PurchaseStorageOptions> storage, CancellationToken ct) =>
            ToResult(await Store(db,audit,storage).DeleteEnergyAsync(user.RequireUserId(), fullWorthSpaceId, assetId, certificateId, ct)));

        var documents = app.MapGroup("/api/assets/{assetId:guid}/documents").WithTags("Asset documents");
        documents.MapGet("/", async (Guid assetId, Guid fullWorthSpaceId, CurrentUserContext user, FullWorthDbContext db, AuditService audit, IOptions<PurchaseStorageOptions> storage, CancellationToken ct) =>
            ToResult(await Store(db,audit,storage).ListDocumentsAsync(user.RequireUserId(), fullWorthSpaceId, assetId, ct)));
        documents.MapPost("/", async (Guid assetId, Guid fullWorthSpaceId, HttpRequest request, CurrentUserContext user, FullWorthDbContext db, AuditService audit, IOptions<PurchaseStorageOptions> storage, CancellationToken ct) =>
            ToResult(await Store(db,audit,storage).UploadDocumentAsync(user.RequireUserId(), fullWorthSpaceId, assetId, request, ct)));
        documents.MapGet("/{documentId:guid}/content", async (Guid assetId, Guid documentId, Guid fullWorthSpaceId, HttpContext http, CurrentUserContext user, FullWorthDbContext db, AuditService audit, IOptions<PurchaseStorageOptions> storage, CancellationToken ct) =>
        {
            var file = await Store(db,audit,storage).GetDocumentContentAsync(user.RequireUserId(), fullWorthSpaceId, assetId, documentId, ct);
            if (file is null) return Results.NotFound();
            http.Response.Headers["X-Content-Type-Options"] = "nosniff";
            return Results.File(file.AbsolutePath, file.MediaType, fileDownloadName: file.MediaType == "application/pdf" ? file.FileName : null, enableRangeProcessing: true);
        });
        documents.MapDelete("/{documentId:guid}", async (Guid assetId, Guid documentId, Guid fullWorthSpaceId, CurrentUserContext user, FullWorthDbContext db, AuditService audit, IOptions<PurchaseStorageOptions> storage, CancellationToken ct) =>
            ToResult(await Store(db,audit,storage).DeleteDocumentAsync(user.RequireUserId(), fullWorthSpaceId, assetId, documentId, ct)));

        var valuation = app.MapGroup("/api/assets/{assetId:guid}/real-estate").WithTags("Real estate valuation");
        valuation.MapGet("/valuation-capabilities", async (Guid assetId, Guid fullWorthSpaceId, CurrentUserContext user, PropertyValuationService service, CancellationToken ct) =>
            ToResult(await service.GetCapabilitiesAsync(user.RequireUserId(), fullWorthSpaceId, assetId, ct)));
        valuation.MapPost("/estimate", async (Guid assetId, Guid fullWorthSpaceId, InternalPropertyEstimateWrite request, CurrentUserContext user, PropertyValuationService service, CancellationToken ct) =>
            ToResult(await service.EstimateInternalAsync(user.RequireUserId(), fullWorthSpaceId, assetId, request, ct)));
        valuation.MapPost("/external-valuation", async (Guid assetId, Guid fullWorthSpaceId, ExternalPropertyValuationWrite request, CurrentUserContext user, PropertyValuationService service, CancellationToken ct) =>
            ToResult(await service.EstimateExternalAsync(user.RequireUserId(), fullWorthSpaceId, assetId, request, ct)));
        return app;
    }

    private static RealEstateAdvancedStore Store(FullWorthDbContext db, AuditService audit, IOptions<PurchaseStorageOptions> storage) => new(db,audit,storage);

    private static IResult ToResult<T>(RealEstateMutationOutcome<T> outcome) => outcome.Result switch
    {
        RealEstateMutationResult.Success => Results.Ok(outcome.Value),
        RealEstateMutationResult.NotFound => Results.NotFound(),
        RealEstateMutationResult.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
        RealEstateMutationResult.Invalid => Results.BadRequest(new { error = outcome.Error ?? "Invalid request." }),
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
