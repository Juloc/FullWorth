using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Export;

public static class WealthPortableExportEndpoints
{
    private const long MaxValidationBytes = 1L * 1024 * 1024 * 1024;

    public static IEndpointRouteBuilder MapWealthPortableExportEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/export/wealth-full", async (
            Guid fullWorthSpaceId,
            CurrentUserContext currentUser,
            WealthPortableExportService service,
            CancellationToken ct) =>
        {
            var export = await service.BuildAsync(currentUser.RequireUserId(), fullWorthSpaceId, ct);
            return export is null ? Results.NotFound() : Results.Ok(export);
        }).WithTags("Export");

        app.MapGet("/api/export/wealth-backup", async (
            Guid fullWorthSpaceId,
            CurrentUserContext currentUser,
            WealthPortableExportService service,
            CancellationToken ct) =>
        {
            var backup = await service.BackupAsync(currentUser.RequireUserId(), fullWorthSpaceId, ct);
            return backup is null ? Results.NotFound() : Results.File(backup.Bytes, "application/zip", backup.FileName);
        }).WithTags("Export");

        app.MapPost("/api/export/wealth-backup/validate", async (
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
            if (buffer.Length == 0) return Results.BadRequest(new { error = "Backup ZIP body is required." });
            buffer.Position = 0;
            var result = await service.ValidateBackupAsync(currentUser.RequireUserId(), fullWorthSpaceId, buffer, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        }).WithTags("Export");

        return app;
    }
}
