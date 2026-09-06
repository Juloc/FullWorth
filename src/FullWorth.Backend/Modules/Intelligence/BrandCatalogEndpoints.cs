using FullWorth.Backend.Security;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Intelligence;

/// <summary>
/// Exposes only the already verified, locally installed brand catalog. No transaction/counterparty text
/// leaves the instance; browser-side identity matching uses these aliases and data URIs locally.
/// </summary>
public static class BrandCatalogEndpoints
{
    public static IEndpointRouteBuilder MapBrandCatalogEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/intelligence/brand-catalog", async (
            CurrentUserContext currentUser,
            IntelligenceDbContext db,
            CancellationToken ct) =>
        {
            _ = currentUser.RequireUserId();

            var installation = await db.KnowledgePackInstallations.AsNoTracking()
                .SingleOrDefaultAsync(x => x.ScopeKey == KnowledgePackProtocol.InstallationScopeKey, ct);
            var assetRows = await db.OfficialBrandAssets.AsNoTracking()
                .OrderBy(x => x.BrandKey)
                .Select(x => new
                {
                    x.BrandKey,
                    x.CanonicalName,
                    x.LogoKey,
                    x.MediaType,
                    x.ContentBase64,
                    x.ContentSha256,
                    x.SourceName,
                    x.SourceUrl,
                    x.LicenseNote
                })
                .ToListAsync(ct);
            var assets = assetRows.Select(x => new
            {
                x.BrandKey,
                x.CanonicalName,
                x.LogoKey,
                dataUri = "data:" + x.MediaType + ";base64," + x.ContentBase64,
                x.ContentSha256,
                x.SourceName,
                x.SourceUrl,
                x.LicenseNote
            }).ToList();
            var aliases = await db.OfficialBrandAliases.AsNoTracking()
                .OrderBy(x => x.AliasKey)
                .Select(x => new { x.AliasKey, x.BrandKey, x.Country })
                .ToListAsync(ct);

            return Results.Ok(new
            {
                packVersion = installation?.Version,
                assets,
                aliases
            });
        });

        return app;
    }
}
