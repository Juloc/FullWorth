using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Export;

public static class ExportEndpoints
{
    public static IEndpointRouteBuilder MapExportEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/export/snapshot", async (Guid fullWorthSpaceId, CurrentUserContext currentUser, ExportService service, CancellationToken ct) =>
        {
            var snapshot = await service.SnapshotForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, ct);
            return snapshot is null ? Results.NotFound() : Results.Ok(snapshot);
        }).WithTags("Export");

        app.MapGet("/api/capabilities", () => Results.Ok(new
        {
            version = "0.3.0",
            resources = new[] { "accounts", "transactions", "purchases", "purchase-items", "categories", "categorization-rules", "contracts", "budgets", "assets", "liabilities", "analytics", "net-worth", "export" },
            fullSnapshot = "/api/export/snapshot"
        })).WithTags("Meta");
        return app;
    }
}
