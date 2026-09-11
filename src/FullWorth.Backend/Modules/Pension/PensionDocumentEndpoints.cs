using FullWorth.Backend.Modules.Purchases;
using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Pension;

/// <summary>
/// The bAV document routes, on the same <c>/api/pension</c> group and with the same status semantics
/// as <see cref="PensionEndpoints"/>: a non-member gets 404 so the existence of a document does not
/// leak, a member without the owner role gets 403 on a write, and a re-uploaded file gets 409 with
/// the id of the document that already holds it.
///
/// Nothing here trusts the upload's own claim about itself. The extension is checked against a fixed
/// list and the leading bytes against that extension, because a payload renamed to <c>.pdf</c> would
/// otherwise be stored and later handed back to a browser.
/// </summary>
public static class PensionDocumentEndpoints
{
    public static IEndpointRouteBuilder MapPensionDocumentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/pension").WithTags("Pension");

        group.MapPost("/documents", async (
            Guid fullWorthSpaceId,
            HttpRequest request,
            CurrentUserContext currentUser,
            PensionDocumentStore store,
            CancellationToken ct) =>
        {
            if (!request.HasFormContentType)
                return Results.BadRequest(new { error = "multipart/form-data is required." });

            var form = await request.ReadFormAsync(ct);
            var file = form.Files.GetFile("document") ?? form.Files.GetFile("file");
            if (file is null) return Results.BadRequest(new { error = "A document file is required." });
            if (file.Length <= 0 || file.Length > PensionDocumentStore.MaxDocumentBytes)
                return Results.BadRequest(new
                {
                    error = $"A document is between 1 byte and {PensionDocumentStore.MaxDocumentBytes / (1024 * 1024)} MB."
                });

            var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (!PensionDocumentStore.SupportedExtensions.Contains(extension))
                return Results.BadRequest(new { error = "A pension document is a PDF, a JPEG or a PNG." });

            byte[] content;
            await using (var source = file.OpenReadStream())
            await using (var memory = new MemoryStream())
            {
                await source.CopyToAsync(memory, ct);
                content = memory.ToArray();
            }

            // The same leading-byte check the receipt upload uses, so a mislabeled payload is never
            // stored and never served back with a type it does not have.
            if (!ReceiptSignature.Matches(content.AsSpan(0, Math.Min(16, content.Length)), extension))
                return Results.BadRequest(new { error = "The file's content does not match its type." });

            var kind = form["kind"].ToString();
            var outcome = await store.UploadAsync(
                currentUser.RequireUserId(), fullWorthSpaceId, content, file.FileName, kind, ct);
            return Map(outcome, "Invalid document.");
        }).DisableAntiforgery();

        group.MapGet("/documents", async (
            Guid fullWorthSpaceId,
            CurrentUserContext currentUser,
            PensionDocumentStore store,
            CancellationToken ct) =>
        {
            var rows = await store.ListAsync(currentUser.RequireUserId(), fullWorthSpaceId, ct);
            return rows is null ? Results.NotFound() : Results.Ok(rows);
        });

        group.MapGet("/documents/{documentId:guid}", async (
            Guid documentId,
            Guid fullWorthSpaceId,
            CurrentUserContext currentUser,
            PensionDocumentStore store,
            CancellationToken ct) =>
        {
            var detail = await store.GetAsync(currentUser.RequireUserId(), fullWorthSpaceId, documentId, ct);
            return detail is null ? Results.NotFound() : Results.Ok(detail);
        });

        group.MapGet("/documents/{documentId:guid}/content", async (
            Guid documentId,
            Guid fullWorthSpaceId,
            HttpContext http,
            CurrentUserContext currentUser,
            PensionDocumentStore store,
            CancellationToken ct) =>
        {
            var content = await store.ContentAsync(currentUser.RequireUserId(), fullWorthSpaceId, documentId, ct);
            if (content is null) return Results.NotFound();

            // A pension statement names a person and their policy number. It must not survive in a
            // shared or disk cache, and it must not be rendered inline where a wrong sniffed type
            // could execute it.
            http.Response.Headers.CacheControl = "no-store";
            http.Response.Headers["X-Content-Type-Options"] = "nosniff";
            return Results.File(content.Content, content.MediaType, fileDownloadName: content.FileName);
        });

        group.MapPut("/documents/{documentId:guid}/review", async (
            Guid documentId,
            Guid fullWorthSpaceId,
            BavDocumentReviewRequest request,
            CurrentUserContext currentUser,
            PensionDocumentStore store,
            CancellationToken ct) =>
            Map(await store.ReviewAsync(currentUser.RequireUserId(), fullWorthSpaceId, documentId, request, ct),
                "Invalid draft."));

        group.MapPost("/documents/{documentId:guid}/commit", async (
            Guid documentId,
            Guid fullWorthSpaceId,
            BavDocumentCommitRequest request,
            CurrentUserContext currentUser,
            PensionDocumentStore store,
            CancellationToken ct) =>
            Map(await store.CommitAsync(currentUser.RequireUserId(), fullWorthSpaceId, documentId, request, ct),
                "Invalid commit."));

        group.MapDelete("/documents/{documentId:guid}", async (
            Guid documentId,
            Guid fullWorthSpaceId,
            CurrentUserContext currentUser,
            PensionDocumentStore store,
            CancellationToken ct) =>
        {
            var outcome = await store.DeleteAsync(currentUser.RequireUserId(), fullWorthSpaceId, documentId, ct);
            return outcome.Result switch
            {
                BavMutationResult.Success => Results.NoContent(),
                BavMutationResult.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
                BavMutationResult.Conflict => Results.Conflict(new { error = outcome.Error }),
                BavMutationResult.Invalid => Results.BadRequest(new { error = outcome.Error }),
                _ => Results.NotFound()
            };
        });

        return app;
    }

    private static IResult Map(BavDocumentOutcome outcome, string fallback) => outcome.Result switch
    {
        BavMutationResult.Success => Results.Ok(outcome.Value),
        BavMutationResult.NotFound => Results.NotFound(),
        BavMutationResult.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
        // The re-upload case carries the document that already holds the file, so the caller lands on
        // the review that is already in progress instead of creating a second one.
        BavMutationResult.Conflict => Results.Conflict(new
        {
            error = outcome.Error ?? fallback,
            existingDocumentId = outcome.ExistingDocumentId,
            detail = outcome.Value
        }),
        _ => Results.BadRequest(new { error = outcome.Error ?? fallback })
    };
}
