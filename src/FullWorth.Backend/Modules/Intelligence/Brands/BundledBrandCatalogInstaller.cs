using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Intelligence.Brands;

/// <summary>
/// Schreibt den mitgelieferten Markenkatalog in die Datenbank - bei jedem Start, ergebnisgleich.
///
/// Warum ueberhaupt in die Datenbank und nicht direkt aus der Assembly serviert? Weil der Katalog
/// nur eine von drei Quellen ist. <see cref="BrandPackService"/> mischt eigene Pakete (Vorrang
/// ueber allem), diesen Katalog und selbst recherchierte Logos zu einer Antwort zusammen, und die
/// Rangfolge ergibt nur Sinn, wenn alle drei in derselben Form vorliegen. Die Bytes liegen
/// inhaltsadressiert in BrandAssetBlobs, geteilt mit den beiden anderen Quellen - dieselbe Datei
/// zweimal gibt es dort nicht.
///
/// Abgleich statt Anhaengen: was der Katalog nicht mehr nennt, verschwindet. Sonst blieben nach
/// einem Neuaufbau die Marken einer aelteren Fassung stehen, und niemand koennte erklaeren, woher
/// ein Logo kommt, das in keiner Quelldatei steht. Eigene Pakete bleiben unberuehrt; sie haben
/// ohnehin Vorrang.
/// </summary>
public sealed class BundledBrandCatalogInstaller(
    IntelligenceDbContext db,
    ILogger<BundledBrandCatalogInstaller> logger)
{
    public async Task<int> InstallAsync(CancellationToken ct)
    {
        var brands = BundledBrandCatalog.Brands;
        if (brands.Count == 0) return 0;

        var wantedKeys = brands.Select(x => x.BrandKey).ToHashSet(StringComparer.Ordinal);

        var existingAssets = await db.OfficialBrandAssets.ToListAsync(ct);
        var byKey = existingAssets.ToDictionary(x => x.BrandKey, StringComparer.Ordinal);
        var knownBlobs = (await db.BrandAssetBlobs.Select(x => x.ContentSha256).ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);

        var changed = 0;
        foreach (var brand in brands)
        {
            if (!knownBlobs.Contains(brand.ContentSha256))
            {
                var content = BundledBrandCatalog.Content(brand);
                if (content.Length != brand.ByteLength)
                    throw new InvalidOperationException(
                        $"{brand.File}: das Manifest nennt {brand.ByteLength} Bytes, die Ressource hat {content.Length}.");

                db.BrandAssetBlobs.Add(new BrandAssetBlob
                {
                    ContentSha256 = brand.ContentSha256,
                    MediaType = "image/svg+xml",
                    ByteLength = content.Length,
                    Content = content
                });
                knownBlobs.Add(brand.ContentSha256);
            }

            if (!byKey.TryGetValue(brand.BrandKey, out var asset))
            {
                asset = new OfficialBrandAsset { BrandKey = brand.BrandKey };
                db.OfficialBrandAssets.Add(asset);
                changed++;
            }
            else if (!string.Equals(asset.ContentSha256, brand.ContentSha256, StringComparison.Ordinal))
            {
                changed++;
            }

            asset.CanonicalName = brand.CanonicalName;
            asset.LogoKey = brand.BrandKey;
            asset.MediaType = "image/svg+xml";
            asset.ContentSha256 = brand.ContentSha256;
            asset.ByteLength = brand.ByteLength;
            asset.SourceName = brand.SourceName;
            asset.SourceUrl = brand.SourceUrl;
            asset.LicenseNote = brand.LicenseNote;
        }

        var obsoleteAssets = existingAssets.Where(x => !wantedKeys.Contains(x.BrandKey)).ToList();
        if (obsoleteAssets.Count > 0) db.OfficialBrandAssets.RemoveRange(obsoleteAssets);

        // Aliase werden ersetzt, nicht abgeglichen: sie sind reine Ableitung aus dem Manifest, es
        // haengt nichts daran, und ein Abgleich waere mehr Code fuer dasselbe Ergebnis.
        var wantedAliases = brands
            .SelectMany(brand => brand.Aliases.Select(alias => (Alias: alias, brand.BrandKey)))
            .ToList();
        var existingAliases = await db.OfficialBrandAliases.ToListAsync(ct);
        var sameAliases = existingAliases.Count == wantedAliases.Count &&
            existingAliases
                .OrderBy(x => x.AliasKey, StringComparer.Ordinal)
                .Zip(wantedAliases.OrderBy(x => x.Alias, StringComparer.Ordinal))
                .All(pair =>
                    string.Equals(pair.First.AliasKey, pair.Second.Alias, StringComparison.Ordinal) &&
                    string.Equals(pair.First.BrandKey, pair.Second.BrandKey, StringComparison.Ordinal));

        if (!sameAliases)
        {
            db.OfficialBrandAliases.RemoveRange(existingAliases);
            await db.SaveChangesAsync(ct);
            db.OfficialBrandAliases.AddRange(wantedAliases.Select(x => new OfficialBrandAlias
            {
                AliasKey = x.Alias,
                BrandKey = x.BrandKey,
                Country = "GLOBAL"
            }));
            changed++;
        }

        if (changed == 0 && obsoleteAssets.Count == 0) return 0;

        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "Mitgelieferter Markenkatalog installiert: {Brands} Marken, {Aliases} Schreibweisen, Simple Icons {Version}.",
            brands.Count, wantedAliases.Count, BundledBrandCatalog.SimpleIconsVersion);
        return brands.Count;
    }
}
