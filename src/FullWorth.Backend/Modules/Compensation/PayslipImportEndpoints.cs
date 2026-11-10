using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Compensation;

/// <summary>Die Funde aus Paperless, die auf Pruefung warten. Alles hier sieht nur der Benutzer, dem der Fund gehoert.</summary>
public static class PayslipImportEndpoints
{
    public static IEndpointRouteBuilder MapCompensationPayslipImportEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/compensation/payslip-imports").WithTags("Compensation");

        group.MapGet("/", async (Guid fullWorthSpaceId, CurrentUserContext currentUser, SpaceAccess access, PayslipImportStore store, CancellationToken ct) =>
        {
            var userId = currentUser.RequireUserId();
            return await access.IsMemberAsync(userId, fullWorthSpaceId, ct)
                ? Results.Ok(await store.ListOpenAsync(userId, fullWorthSpaceId, ct))
                : Results.NotFound();
        });

        group.MapGet("/{id:guid}/document", async (
            Guid id, Guid fullWorthSpaceId, HttpContext http, CurrentUserContext currentUser, PayslipImportStore store, CancellationToken ct) =>
        {
            var document = await store.GetDocumentAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, ct);
            if (document is null) return Results.NotFound();

            // Eine Lohnabrechnung nennt Person, Arbeitgeber und Gehalt: sie darf in keinem geteilten oder
            // Festplatten-Cache bleiben, und sie wird nur als PDF inline gezeigt - was Paperless sonst als
            // Typ meldet, wird heruntergeladen, nicht ausgefuehrt.
            http.Response.Headers.CacheControl = "no-store";
            http.Response.Headers["X-Content-Type-Options"] = "nosniff";
            return document.ContentType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase)
                ? Results.File(document.Content, "application/pdf")
                : Results.File(document.Content, "application/octet-stream", fileDownloadName: document.FileName);
        });

        group.MapPost("/{id:guid}/dismiss", async (Guid id, CurrentUserContext currentUser, PayslipImportStore store, CancellationToken ct) =>
            await store.DismissAsync(currentUser.RequireUserId(), id, ct) ? Results.NoContent() : Results.NotFound());

        return app;
    }
}
