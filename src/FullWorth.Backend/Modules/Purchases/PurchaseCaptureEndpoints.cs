using FullWorth.Backend.Security;
using Microsoft.Extensions.Options;

namespace FullWorth.Backend.Modules.Purchases;

public static class PurchaseCaptureEndpoints
{
    public static IEndpointRouteBuilder MapPurchaseCaptureEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/purchases").WithTags("Purchases");

        group.MapPost("/receipt-scan", async (
            Guid fullWorthSpaceId,
            HttpRequest request,
            CurrentUserContext currentUser,
            PurchaseCaptureService service,
            CancellationToken ct) =>
            CaptureOutcome(await service.CaptureReceiptAsync(currentUser.RequireUserId(), fullWorthSpaceId, request, ct)));

        group.MapPost("/{id:guid}/extraction", async (
            Guid id,
            Guid fullWorthSpaceId,
            PurchaseExtractionRequest request,
            CurrentUserContext currentUser,
            PurchaseCaptureService service,
            CancellationToken ct) =>
            CaptureOutcome(await service.ApplyExtractionAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, request, ct)));

        group.MapPost("/import/amazon", async (
            Guid fullWorthSpaceId,
            AmazonOrderImportRequest request,
            CurrentUserContext currentUser,
            PurchaseCaptureService service,
            CancellationToken ct) =>
            CaptureOutcome(await service.ImportAmazonAsync(currentUser.RequireUserId(), fullWorthSpaceId, request, ct)));

        group.MapPost("/{id:guid}/auto-link", async (
            Guid id,
            Guid fullWorthSpaceId,
            CurrentUserContext currentUser,
            PurchaseCaptureService service,
            CancellationToken ct) =>
        {
            var outcome = await service.TryAutoLinkAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, ct);
            return outcome.Result switch
            {
                PurchaseMutationResult.Success => Results.Ok(new { outcome.Linked }),
                PurchaseMutationResult.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
                _ => Results.NotFound()
            };
        });

        group.MapGet("/{id:guid}/receipt", async (
            Guid id,
            Guid fullWorthSpaceId,
            HttpContext http,
            CurrentUserContext currentUser,
            PurchaseAuthorizationStore authorization,
            IOptions<PurchaseStorageOptions> storageOptions,
            CancellationToken ct) =>
        {
            var relative = await authorization.GetReceiptPathForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, ct);
            if (string.IsNullOrWhiteSpace(relative)) return Results.NotFound();

            var root = Path.GetFullPath(storageOptions.Value.RootPath);
            var candidate = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            var rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!candidate.StartsWith(rootPrefix, StringComparison.Ordinal) || !File.Exists(candidate))
                return Results.NotFound();

            // P1.2d: never let the browser sniff the type; PDFs are forced to download (they can carry
            // active content), images may render inline (their bytes are magic-byte validated on upload).
            var contentType = ContentType(candidate);
            http.Response.Headers["X-Content-Type-Options"] = "nosniff";
            var downloadName = contentType == "application/pdf" ? $"receipt-{id}{Path.GetExtension(candidate)}" : null;
            return Results.File(candidate, contentType, fileDownloadName: downloadName, enableRangeProcessing: true);
        });

        PurchaseDiscountAnalyticsEndpoints.Map(group);
        ReceiptScanQueueEndpoints.Map(group);
        CodexReceiptTestEndpoints.Map(group);
        return app;
    }

    private static IResult CaptureOutcome(PurchaseMutationOutcome outcome) => outcome.Result switch
    {
        PurchaseMutationResult.Success when outcome.Purchase is not null => Results.Ok(outcome.Purchase),
        PurchaseMutationResult.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
        PurchaseMutationResult.Invalid => Results.BadRequest(new { error = outcome.Error ?? "Invalid purchase input." }),
        _ => Results.NotFound()
    };

    private static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".heic" => "image/heic",
        ".pdf" => "application/pdf",
        _ => "application/octet-stream"
    };
}
