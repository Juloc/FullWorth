using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Purchases;

public static class PurchaseExportEndpoints
{
    public static IEndpointRouteBuilder MapPurchaseExportEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/purchases/export", async (Guid fullWorthSpaceId, string? format, bool? includeDocuments, FullWorth.Backend.Security.CurrentUserContext user, PurchaseExportService service, CancellationToken ct) =>
        {
            var file = await service.ExportAsync(user.RequireUserId(), fullWorthSpaceId, format ?? "json", includeDocuments == true, ct);
            return file is null ? Results.NotFound() : Results.File(file.Bytes, file.ContentType, file.FileName);
        }).WithTags("Purchases");
        return app;
    }
}