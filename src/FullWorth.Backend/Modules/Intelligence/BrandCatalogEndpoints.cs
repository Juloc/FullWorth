using FullWorth.Backend.Security;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Intelligence;

public sealed record UpdateCustomBrandPackStateRequest(bool Enabled);

/// <summary>
/// Brand identities are resolved from verified local data only. Official assets arrive through signed
/// FullWorth knowledge packs; instance admins may add local custom packs that override official aliases.
/// </summary>
public static class BrandCatalogEndpoints
{
    public static IEndpointRouteBuilder MapBrandCatalogEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/intelligence/brand-catalog", async (
            CurrentUserContext currentUser,
            IntelligenceDbContext db,
            BrandPackService brandPacks,
            CancellationToken ct) =>
        {
            _ = currentUser.RequireUserId();
            var installation = await db.KnowledgePackInstallations.AsNoTracking()
                .SingleOrDefaultAsync(x => x.ScopeKey == KnowledgePackProtocol.InstallationScopeKey, ct);
            var catalog = await brandPacks.GetEffectiveCatalogAsync(ct);

            return Results.Ok(new
            {
                packVersion = installation?.Version,
                assets = catalog.Assets,
                aliases = catalog.Aliases
            });
        });

        var custom = app.MapGroup("/api/intelligence/admin/brand-packs/custom")
            .WithTags("Intelligence Admin");

        custom.MapGet("", async (
            CurrentUserContext currentUser,
            IntelligenceAdminAuthorizer authorizer,
            BrandPackService brandPacks,
            CancellationToken ct) =>
        {
            var userId = currentUser.RequireUserId();
            if (!await authorizer.IsAdminAsync(userId, ct))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            return Results.Ok(await brandPacks.ListCustomPacksAsync(ct));
        });

        custom.MapPost("", async (
            CustomBrandPackImportRequest request,
            CurrentUserContext currentUser,
            IntelligenceAdminAuthorizer authorizer,
            IntelligenceDbContext db,
            BrandPackService brandPacks,
            CancellationToken ct) =>
        {
            var userId = currentUser.RequireUserId();
            if (!await authorizer.IsAdminAsync(userId, ct))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            try
            {
                var pack = await brandPacks.ImportCustomPackAsync(request, ct);
                IntelligenceAuditWriter.Record(
                    db,
                    userId,
                    "brand_pack.imported",
                    "CustomBrandPack",
                    pack.Id,
                    $"{pack.Name}@{pack.Version}");
                await db.SaveChangesAsync(ct);
                return Results.Ok(pack);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        custom.MapPut("/{id:guid}/enabled", async (
            Guid id,
            UpdateCustomBrandPackStateRequest request,
            CurrentUserContext currentUser,
            IntelligenceAdminAuthorizer authorizer,
            IntelligenceDbContext db,
            BrandPackService brandPacks,
            CancellationToken ct) =>
        {
            var userId = currentUser.RequireUserId();
            if (!await authorizer.IsAdminAsync(userId, ct))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (!await brandPacks.SetCustomPackEnabledAsync(id, request.Enabled, ct))
                return Results.NotFound();

            IntelligenceAuditWriter.Record(
                db,
                userId,
                request.Enabled ? "brand_pack.enabled" : "brand_pack.disabled",
                "CustomBrandPack",
                id);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        custom.MapDelete("/{id:guid}", async (
            Guid id,
            CurrentUserContext currentUser,
            IntelligenceAdminAuthorizer authorizer,
            IntelligenceDbContext db,
            BrandPackService brandPacks,
            CancellationToken ct) =>
        {
            var userId = currentUser.RequireUserId();
            if (!await authorizer.IsAdminAsync(userId, ct))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (!await brandPacks.DeleteCustomPackAsync(id, ct))
                return Results.NotFound();

            IntelligenceAuditWriter.Record(
                db,
                userId,
                "brand_pack.deleted",
                "CustomBrandPack",
                id);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        return app;
    }
}
