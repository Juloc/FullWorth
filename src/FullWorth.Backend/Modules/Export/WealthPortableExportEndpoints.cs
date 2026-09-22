using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Export;

public static class WealthPortableExportEndpoints
{

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

        // Das Pruefen einer hochgeladenen Sicherung stand hier ein zweites Mal, Zeile fuer Zeile
        // gleich wie POST /api/import/wealth-backup/validate und mit demselben Aufruf dahinter.
        // Keines von beiden hatte einen Aufrufer, und eine Sicherung prueft man, bevor man sie
        // einspielt - das ist ein Import. Uebrig bleibt der eine Weg (#177).

        return app;
    }
}
