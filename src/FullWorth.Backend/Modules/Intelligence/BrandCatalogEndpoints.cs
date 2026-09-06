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
                assets = catalog.Assets.Select(x => new
                {
                    x.BrandKey,
                    x.CanonicalName,
                    x.LogoKey,
                    assetPath = $"/api/intelligence/brand-assets/{x.ContentSha256}",
                    x.ContentSha256,
                    x.SourceName,
                    x.SourceUrl,
                    x.LicenseNote,
                    x.Source,
                    x.Priority
                }),
                aliases = catalog.Aliases
            });
        });

        app.MapGet("/api/intelligence/brand-assets/{contentSha256}", async (
            string contentSha256,
            CurrentUserContext currentUser,
            IntelligenceDbContext db,
            HttpContext http,
            CancellationToken ct) =>
        {
            _ = currentUser.RequireUserId();
            var hash = contentSha256?.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(hash) || hash.Length != 64 || !hash.All(Uri.IsHexDigit))
                return Results.NotFound();

            var blob = await db.BrandAssetBlobs.AsNoTracking()
                .SingleOrDefaultAsync(x => x.ContentSha256 == hash, ct);
            if (blob is null) return Results.NotFound();

            var bytes = blob.Content;

            try
            {
                _ = BrandAssetVerifier.VerifySvg(bytes, blob.MediaType, hash, blob.ByteLength);
            }
            catch (KnowledgePackVerificationException)
            {
                return Results.NotFound();
            }

            http.Response.Headers.ETag = $"\"{hash}\"";
            http.Response.Headers.CacheControl = "private, max-age=31536000, immutable";
            return Results.Bytes(bytes, blob.MediaType);
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
                    pack.Id);
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
