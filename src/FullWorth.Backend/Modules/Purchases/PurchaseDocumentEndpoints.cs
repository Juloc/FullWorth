using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Purchases;

public static class PurchaseDocumentEndpoints
{
    public static IEndpointRouteBuilder MapPurchaseDocumentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/purchases/{purchaseId:guid}/documents").WithTags("Purchases");
        group.MapGet("/", async (Guid purchaseId, Guid fullWorthSpaceId, FullWorth.Backend.Security.CurrentUserContext user, PurchaseDocumentService service, CancellationToken ct) =>
        { var value = await service.ListAsync(user.RequireUserId(), fullWorthSpaceId, purchaseId, ct); return value is null ? Results.NotFound() : Results.Ok(value); });
        group.MapPost("/", async (Guid purchaseId, Guid fullWorthSpaceId, HttpRequest request, FullWorth.Backend.Security.CurrentUserContext user, PurchaseDocumentService service, CancellationToken ct) => Map(await service.UploadAsync(user.RequireUserId(), fullWorthSpaceId, purchaseId, request, ct), true));
        group.MapGet("/{documentId:guid}/content", async (Guid purchaseId, Guid documentId, Guid fullWorthSpaceId, HttpContext http, FullWorth.Backend.Security.CurrentUserContext user, PurchaseDocumentService service, CancellationToken ct) =>
        {
            var file = await service.GetContentAsync(user.RequireUserId(), fullWorthSpaceId, purchaseId, documentId, ct);
            if (file is null) return Results.NotFound();
            http.Response.Headers["X-Content-Type-Options"] = "nosniff";
            var downloadName = file.MediaType == "application/pdf" ? file.FileName : null;
            return Results.File(file.AbsolutePath, file.MediaType, fileDownloadName: downloadName, enableRangeProcessing: true);
        });
        group.MapDelete("/{documentId:guid}", async (Guid purchaseId, Guid documentId, Guid fullWorthSpaceId, FullWorth.Backend.Security.CurrentUserContext user, PurchaseDocumentService service, CancellationToken ct) => Mutation(await service.DeleteAsync(user.RequireUserId(), fullWorthSpaceId, purchaseId, documentId, ct)));
        group.MapPost("/{documentId:guid}/extract", async (Guid purchaseId, Guid documentId, Guid fullWorthSpaceId, FullWorth.Backend.Security.CurrentUserContext user, PurchaseDocumentService service, CancellationToken ct) => Map(await service.ExtractAsync(user.RequireUserId(), fullWorthSpaceId, purchaseId, documentId, ct)));
        group.MapGet("/{documentId:guid}/extractions", async (Guid purchaseId, Guid documentId, Guid fullWorthSpaceId, FullWorth.Backend.Security.CurrentUserContext user, PurchaseDocumentService service, CancellationToken ct) =>
        { var value = await service.ExtractionsAsync(user.RequireUserId(), fullWorthSpaceId, purchaseId, documentId, ct); return value is null ? Results.NotFound() : Results.Ok(value); });
        app.MapPost("/api/purchases/{purchaseId:guid}/apply-extraction/{runId:guid}", async (Guid purchaseId, Guid runId, Guid fullWorthSpaceId, ApplyExtractionRunRequest request, FullWorth.Backend.Security.CurrentUserContext user, PurchaseDocumentService service, CancellationToken ct) => Map(await service.ApplyRunAsync(user.RequireUserId(), fullWorthSpaceId, purchaseId, runId, request, ct)));
        return app;
    }

    private static IResult Mutation(PurchaseMutationResult result) => result switch
    { PurchaseMutationResult.Success => Results.NoContent(), PurchaseMutationResult.Invalid => Results.BadRequest(), PurchaseMutationResult.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden), _ => Results.NotFound() };
    private static IResult Map(PurchaseDocumentMutation outcome, bool created = false) => outcome switch
    {
        { Duplicate: true } => Results.Conflict(new { error = outcome.Error, duplicate = outcome.Value }),
        { Result: PurchaseMutationResult.Success } when created => Results.Created(string.Empty, outcome.Value),
        { Result: PurchaseMutationResult.Success } => Results.Ok(outcome.Value),
        { Result: PurchaseMutationResult.Invalid } => Results.BadRequest(new { error = outcome.Error, detail = outcome.Value }),
        { Result: PurchaseMutationResult.Forbidden } => Results.StatusCode(StatusCodes.Status403Forbidden),
        _ => Results.NotFound()
    };
}