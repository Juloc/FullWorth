using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Export;

public static class WealthBackupImportValidationEndpoints
{
    private const long MaxValidationBytes = 1L * 1024 * 1024 * 1024;

    public static IEndpointRouteBuilder MapWealthBackupImportValidationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/import/wealth-backup/validate", async (
            Guid fullWorthSpaceId,
            HttpRequest request,
            CurrentUserContext currentUser,
            WealthPortableExportService service,
            CancellationToken ct) =>
        {
            if (request.ContentLength is > MaxValidationBytes)
                return Results.BadRequest(new { error = "Backup is too large for in-app validation." });

            await using var buffer = new MemoryStream();
            await request.Body.CopyToAsync(buffer, ct);
            if (buffer.Length == 0)
                return Results.BadRequest(new { error = "Backup ZIP body is required." });

            buffer.Position = 0;
            var result = await service.ValidateBackupAsync(currentUser.RequireUserId(), fullWorthSpaceId, buffer, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        }).WithTags("Import");

        return app;
    }
}
