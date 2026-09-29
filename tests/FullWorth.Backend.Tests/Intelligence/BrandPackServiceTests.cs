using System.Security.Cryptography;
using System.Text;
using FullWorth.Backend.Modules.Intelligence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Intelligence;

public sealed class BrandPackServiceTests
{
    [Fact]
    public async Task Custom_pack_overrides_official_brand_and_disable_restores_official()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<IntelligenceDbContext>().UseSqlite(connection).Options;
        await using var db = new IntelligenceDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var officialSvg = Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 4 4\"><path d=\"M0 0h4v4H0z\"/></svg>");
        var officialHash = Convert.ToHexString(SHA256.HashData(officialSvg)).ToLowerInvariant();
        db.BrandAssetBlobs.Add(new BrandAssetBlob
        {
            ContentSha256 = officialHash,
            MediaType = "image/svg+xml",
            ByteLength = officialSvg.Length,
            Content = officialSvg
        });
        db.OfficialBrandAssets.Add(new OfficialBrandAsset
        {
            BrandKey = "testbrand",
            CanonicalName = "Official Test Brand",
            LogoKey = "testbrand",
            MediaType = "image/svg+xml",
            ContentSha256 = officialHash,
            ByteLength = officialSvg.Length
        });
        db.OfficialBrandAliases.Add(new OfficialBrandAlias
        {
            AliasKey = "TEST BRAND",
            BrandKey = "testbrand",
            Country = "GLOBAL"
        });
        await db.SaveChangesAsync();

        var customSvg = Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 4 4\"><circle cx=\"2\" cy=\"2\" r=\"2\"/></svg>");
        var service = new BrandPackService(db);
        var imported = await service.ImportCustomPackAsync(new CustomBrandPackImportRequest(
            "Meine Logos",
            "1.0",
            2000,
            true,
            [
                new CustomBrandAssetImport(
                    "testbrand",
                    "Mein Test Brand",
                    null,
                    "image/svg+xml",
                    Convert.ToBase64String(customSvg),
                    null,
                    "user",
                    null,
                    null)
            ],
            [
                new CustomBrandAliasImport("TEST BRAND", "testbrand", null)
            ]), CancellationToken.None);

        var effective = await service.GetEffectiveCatalogAsync(CancellationToken.None);
        var asset = Assert.Single(effective.Assets);
        Assert.Equal("Mein Test Brand", asset.CanonicalName);
        Assert.Equal("custom:Meine Logos", asset.Source);
        Assert.Equal(2000, asset.Priority);
        Assert.Equal("custom:Meine Logos", effective.Aliases.First(x => x.AliasKey == "TEST BRAND").Source);

        Assert.True(await service.SetCustomPackEnabledAsync(imported.Id, false, CancellationToken.None));
        effective = await service.GetEffectiveCatalogAsync(CancellationToken.None);
        asset = Assert.Single(effective.Assets);
        Assert.Equal("Official Test Brand", asset.CanonicalName);
        Assert.Equal("official", asset.Source);
    }

    [Fact]
    public async Task Custom_packs_share_identical_content_hash_blob()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<IntelligenceDbContext>().UseSqlite(connection).Options;
        await using var db = new IntelligenceDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var svg = Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 2 2\"/>");
        var base64 = Convert.ToBase64String(svg);
        var service = new BrandPackService(db);

        foreach (var (name, brand) in new[] { ("Pack A", "brand-a"), ("Pack B", "brand-b") })
        {
            await service.ImportCustomPackAsync(new CustomBrandPackImportRequest(
                name,
                "1",
                1000,
                true,
                [new CustomBrandAssetImport(brand, brand, null, "image/svg+xml", base64, null, null, null, null)],
                [new CustomBrandAliasImport(brand, brand, null)]), CancellationToken.None);
        }

        Assert.Equal(2, await db.CustomBrandAssets.CountAsync());
        Assert.Single(await db.BrandAssetBlobs.ToListAsync());
    }

    [Fact]
    public async Task Custom_pack_rejects_active_svg_content()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<IntelligenceDbContext>().UseSqlite(connection).Options;
        await using var db = new IntelligenceDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var unsafeSvg = Convert.ToBase64String(
            Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>"));
        var service = new BrandPackService(db);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.ImportCustomPackAsync(new CustomBrandPackImportRequest(
                "Unsafe",
                "1",
                1000,
                true,
                [new CustomBrandAssetImport("unsafe", "Unsafe", null, "image/svg+xml", unsafeSvg, null, null, null, null)],
                [new CustomBrandAliasImport("UNSAFE", "unsafe", null)]), CancellationToken.None));

        Assert.Equal("brand_svg_unsafe", ex.Message);
        Assert.Empty(await db.CustomBrandPacks.ToListAsync());
        Assert.Empty(await db.BrandAssetBlobs.ToListAsync());
    }

    /// <summary>
    /// Der Blob-Speicher ist inhaltsadressiert und hat keine Besitzerspalte. Wer beim Aufraeumen eine
    /// der Quellen vergisst, loescht die Bytes eines Logos, dessen Zeile stehenbleibt - und der
    /// Katalog laesst es danach still weg, ohne dass irgendwo etwas fehlschlaegt.
    ///
    /// Genau das passierte selbst recherchierten Logos: sie wurden nicht mitgezaehlt und verloren
    /// nach dreissig Tagen ihre Bytes. Dieser Test haelt alle drei Quellen fest.
    /// </summary>
    [Fact]
    public async Task Pruning_keeps_a_blob_that_any_of_the_three_sources_still_references()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<IntelligenceDbContext>().UseSqlite(connection).Options;
        await using var db = new IntelligenceDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var stale = DateTimeOffset.UtcNow.AddDays(-40);
        string Add(string marker)
        {
            var bytes = Encoding.UTF8.GetBytes(
                $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 4 4\"><title>{marker}</title></svg>");
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            db.BrandAssetBlobs.Add(new BrandAssetBlob
            {
                ContentSha256 = hash,
                MediaType = "image/svg+xml",
                ByteLength = bytes.Length,
                Content = bytes,
                LastUsedAt = stale
            });
            return hash;
        }

        var bundled = Add("bundled");
        var custom = Add("custom");
        var researched = Add("researched");
        var orphan = Add("orphan");

        db.OfficialBrandAssets.Add(new OfficialBrandAsset
        {
            BrandKey = "bundled", CanonicalName = "Bundled", LogoKey = "bundled",
            ContentSha256 = bundled, ByteLength = 1
        });
        var pack = new CustomBrandPack { Name = "Eigenes" };
        db.CustomBrandPacks.Add(pack);
        db.CustomBrandAssets.Add(new CustomBrandAsset
        {
            PackId = pack.Id, BrandKey = "custom", CanonicalName = "Custom", LogoKey = "custom",
            ContentSha256 = custom, ByteLength = 1
        });
        db.ResearchedBrandAssets.Add(new ResearchedBrandAsset
        {
            BrandKey = "researched", CanonicalName = "Researched", LogoKey = "researched",
            ContentSha256 = researched, ByteLength = 1
        });
        await db.SaveChangesAsync();

        await new BrandPackService(db).PruneUnreferencedBlobsAsync(CancellationToken.None);

        var left = await db.BrandAssetBlobs.Select(x => x.ContentSha256).ToListAsync();
        Assert.Contains(bundled, left);
        Assert.Contains(custom, left);
        Assert.Contains(researched, left);
        Assert.DoesNotContain(orphan, left);
    }
}
