using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Compensation;

/// <summary>Die Funde aus Paperless, die auf Pruefung warten. Alles hier sieht nur der Benutzer, dem der Fund gehoert.</summary>
public static class DocumentImportEndpoints
{
    public static IEndpointRouteBuilder MapCompensationDocumentImportEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/compensation/document-imports").WithTags("Compensation");

        group.MapGet("/", async (Guid fullWorthSpaceId, string? kind, CurrentUserContext currentUser, SpaceAccess access, DocumentImportStore store, CancellationToken ct) =>
        {
            var userId = currentUser.RequireUserId();
            kind ??= DocumentKinds.Payslip;
            if (kind is not (DocumentKinds.Payslip or DocumentKinds.Contract)) return Results.BadRequest(new { error = "Unknown document kind." });
            return await access.IsMemberAsync(userId, fullWorthSpaceId, ct)
                ? Results.Ok(await store.ListOpenAsync(userId, fullWorthSpaceId, kind, ct))
                : Results.NotFound();
        });

        group.MapGet("/{id:guid}/document", async (
            Guid id, Guid fullWorthSpaceId, HttpContext http, CurrentUserContext currentUser, DocumentImportStore store, CancellationToken ct) =>
        {
            var document = await store.GetDocumentAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, ct);
            if (document is null) return Results.NotFound();

            // Lohnabrechnung und Arbeitsvertrag nennen Person, Arbeitgeber und Gehalt: sie darf in keinem geteilten oder
            // Festplatten-Cache bleiben, und sie wird nur als PDF inline gezeigt - was Paperless sonst als
            // Typ meldet, wird heruntergeladen, nicht ausgefuehrt.
            http.Response.Headers.CacheControl = "no-store";
            http.Response.Headers["X-Content-Type-Options"] = "nosniff";
            return document.ContentType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase)
                ? Results.File(document.Content, "application/pdf")
                : Results.File(document.Content, "application/octet-stream", fileDownloadName: document.FileName);
        });

        group.MapPost("/{id:guid}/dismiss", async (Guid id, CurrentUserContext currentUser, DocumentImportStore store, CancellationToken ct) =>
            await store.DismissAsync(currentUser.RequireUserId(), id, ct) ? Results.NoContent() : Results.NotFound());

        return app;
    }
}
