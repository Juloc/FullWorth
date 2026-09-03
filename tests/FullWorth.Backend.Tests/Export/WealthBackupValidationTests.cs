using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Portfolio;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Export;

public sealed class WealthBackupValidationTests
{
    [Fact]
    public async Task GeneratedBackupPassesImportPreflightManifestAndDocumentHashValidation()
    {
        using var factory = new BackendWebApplicationFactory();
        var owner = Guid.NewGuid();
        var outside = Guid.NewGuid();
        var space = Guid.NewGuid();
        var asset = Guid.NewGuid();
        var document = Guid.NewGuid();
        var bytes = Encoding.UTF8.GetBytes("%PDF-1.4\nvalidation\n%%EOF");
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var relative = $"asset-documents/{space:D}/{asset:D}/{document:D}.pdf";
        var absolute = Path.Combine(factory.PurchaseStorageRoot, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        await File.WriteAllBytesAsync(absolute, bytes);

        await factory.SeedAsync(async db =>
        {
            db.Users.AddRange(
                new FullWorthUser { Id = owner, EmailNormalized = $"OWNER-{owner:N}@EXAMPLE.COM", DisplayName = "Owner", IsActive = true },
                new FullWorthUser { Id = outside, EmailNormalized = $"OUT-{outside:N}@EXAMPLE.COM", DisplayName = "Outside", IsActive = true });
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = space, Name = "Backup validation", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember { FullWorthSpaceId = space, UserId = owner, Role = FullWorthSpaceRoles.Owner });
            db.Assets.Add(new Asset { Id = asset, FullWorthSpaceId = space, Name = "Property", Kind = AssetKinds.RealEstate, CurrentValue = 100_000m, Currency = "EUR", IncludeInNetWorth = true });
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "RealEstateAssetDetails" ("AssetId","PropertyType","UsageType","CountryCode","OwnershipSharePercent","UpdatedAt")
                VALUES ({asset},'apartment','owner_occupied','DE',100,now());
                INSERT INTO "AssetDocuments" ("Id","FullWorthSpaceId","AssetId","Category","OriginalFileName","MediaType","StoragePath","Sha256","SizeBytes","CreatedByUserId","CreatedAt")
                VALUES ({document},{space},{asset},'purchase_contract','contract.pdf','application/pdf',{relative},{sha},{bytes.LongLength},{owner},now());
                """);
        });

        using var client = factory.CreateClient();
        var backup = await client.SendAsync(Request(HttpMethod.Get, $"/api/export/wealth-backup?fullWorthSpaceId={space}", owner));
        Assert.Equal(HttpStatusCode.OK, backup.StatusCode);
        var archive = await backup.Content.ReadAsByteArrayAsync();

        var validate = Request(HttpMethod.Post, $"/api/import/wealth-backup/validate?fullWorthSpaceId={space}", owner);
        validate.Content = new ByteArrayContent(archive);
        validate.Content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        var result = await client.SendAsync(validate);
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        using var json = JsonDocument.Parse(await result.Content.ReadAsStringAsync());
        Assert.True(json.RootElement.GetProperty("valid").GetBoolean());
        Assert.Equal(1, json.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(space, json.RootElement.GetProperty("fullWorthSpaceId").GetGuid());
        Assert.Equal(1, json.RootElement.GetProperty("documentsChecked").GetInt32());
        Assert.Empty(json.RootElement.GetProperty("errors").EnumerateArray());

        var hidden = Request(HttpMethod.Post, $"/api/import/wealth-backup/validate?fullWorthSpaceId={space}", outside);
        hidden.Content = new ByteArrayContent(archive);
        hidden.Content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(hidden)).StatusCode);
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, Guid userId)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        return request;
    }
}
