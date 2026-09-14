using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Export;

/// <summary>Der Tabellenexport. Kam aus Parity/ExperienceParityModule.</summary>
public static class XlsxExportEndpoints
{
    public static IEndpointRouteBuilder MapXlsxExportEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/export/xlsx", ExportXlsx).WithTags("Export");
        return app;
    }

    private static async Task<IResult> ExportXlsx(
        Guid fullWorthSpaceId, CurrentUserContext currentUser, SpaceAccess space,
        XlsxExportService export, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await space.IsMemberAsync(userId, fullWorthSpaceId, ct)) return Results.NotFound();

        var visible = await space.VisibleAccountIdsAsync(userId, fullWorthSpaceId, ct);
        var bytes = await export.BuildAsync(fullWorthSpaceId, visible, ct);

        return Results.File(bytes,
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"fullworth-{DateTime.UtcNow:yyyyMMdd}.xlsx");
    }
}
