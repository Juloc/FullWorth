using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Portfolio;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Export;

public sealed class WealthPortableExportTests
{
    [Fact]
    public async Task FullExportContainsStructuredWealthWithoutInternalStoragePath()
    {
        using var factory = new BackendWebApplicationFactory();
        var seed = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Request(HttpMethod.Get,
            $"/api/export/wealth-full?fullWorthSpaceId={seed.SpaceId}", seed.OwnerId));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var jsonText = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("StoragePath", jsonText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(seed.RelativeStoragePath, jsonText, StringComparison.Ordinal);

        using var json = JsonDocument.Parse(jsonText);
        Assert.Equal("fullworth-wealth-export-v1", json.RootElement.GetProperty("format").GetString());
        Assert.Equal(1, json.RootElement.GetProperty("schemaVersion").GetInt32());
        var wealth = json.RootElement.GetProperty("wealth");
        Assert.Single(wealth.GetProperty("realEstateDetails").EnumerateArray());
        Assert.Single(wealth.GetProperty("assetDocuments").EnumerateArray());
        Assert.True(wealth.TryGetProperty("assetValuations", out _));
        Assert.True(wealth.TryGetProperty("investmentPortfolios", out _));
    }

    [Fact]
    public async Task BackupZipContainsManifestAndAuthorizedDocumentBinary()
    {
        using var factory = new BackendWebApplicationFactory();
        var seed = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Request(HttpMethod.Get,
            $"/api/export/wealth-backup?fullWorthSpaceId={seed.SpaceId}", seed.OwnerId));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/zip", response.Content.Headers.ContentType?.MediaType);

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        var manifest = zip.GetEntry("fullworth-wealth-export.json");
        Assert.NotNull(manifest);
        var document = zip.GetEntry($"documents/{seed.AssetId:D}/{seed.DocumentId:D}.pdf");
        Assert.NotNull(document);
        await using var documentStream = document!.Open();
        using var memory = new MemoryStream();
        await documentStream.CopyToAsync(memory);
        Assert.Equal(seed.DocumentBytes, memory.ToArray());

        using var reader = new StreamReader(manifest!.Open(), Encoding.UTF8);
        var manifestText = await reader.ReadToEndAsync();
        Assert.DoesNotContain("StoragePath", manifestText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(seed.DocumentSha256, manifestText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MemberExportMasksTransactionReferencesForAccountTheyCannotAccess()
    {
        using var factory = new BackendWebApplicationFactory();
        var seed = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Request(HttpMethod.Get,
            $"/api/export/wealth-full?fullWorthSpaceId={seed.SpaceId}", seed.MemberId));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var wealth = json.RootElement.GetProperty("wealth");
        var cashflow = wealth.GetProperty("assetCashflows").EnumerateArray().Single();
        var payment = wealth.GetProperty("receivablePayments").EnumerateArray().Single();
        Assert.Equal(JsonValueKind.Null, cashflow.GetProperty("TransactionId").ValueKind);
        Assert.Equal(JsonValueKind.Null, payment.GetProperty("TransactionId").ValueKind);
        Assert.Empty(json.RootElement.GetProperty("snapshot").GetProperty("transactions").EnumerateArray());
    }

    [Fact]
    public async Task NonMemberCannotDiscoverFullExportOrBackup()
    {
        using var factory = new BackendWebApplicationFactory();
        var seed = await SeedAsync(factory);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(Request(HttpMethod.Get,
            $"/api/export/wealth-full?fullWorthSpaceId={seed.SpaceId}", seed.OutsideId))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(Request(HttpMethod.Get,
            $"/api/export/wealth-backup?fullWorthSpaceId={seed.SpaceId}", seed.OutsideId))).StatusCode);
    }

    private static async Task<SeedResult> SeedAsync(BackendWebApplicationFactory factory)
    {
        var owner = Guid.NewGuid();
        var member = Guid.NewGuid();
        var outside = Guid.NewGuid();
        var space = Guid.NewGuid();
        var property = Guid.NewGuid();
        var receivable = Guid.NewGuid();
        var account = Guid.NewGuid();
        var transaction = Guid.NewGuid();
        var document = Guid.NewGuid();
        var documentBytes = Encoding.UTF8.GetBytes("%PDF-1.4\nFullWorth test document\n%%EOF");
        var sha = Convert.ToHexString(SHA256.HashData(documentBytes)).ToLowerInvariant();
        var relative = $"asset-documents/{space:D}/{property:D}/{document:D}.pdf";
        var absolute = Path.Combine(factory.PurchaseStorageRoot, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        await File.WriteAllBytesAsync(absolute, documentBytes);

        await factory.SeedAsync(async db =>
        {
            db.Users.AddRange(
                new FullWorthUser { Id = owner, EmailNormalized = $"OWNER-{owner:N}@EXAMPLE.COM", DisplayName = "Owner", IsActive = true },
                new FullWorthUser { Id = member, EmailNormalized = $"MEMBER-{member:N}@EXAMPLE.COM", DisplayName = "Member", IsActive = true },
                new FullWorthUser { Id = outside, EmailNormalized = $"OUTSIDE-{outside:N}@EXAMPLE.COM", DisplayName = "Outside", IsActive = true });
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = space, Name = "Portable", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.AddRange(
                new FullWorthSpaceMember { FullWorthSpaceId = space, UserId = owner, Role = FullWorthSpaceRoles.Owner },
                new FullWorthSpaceMember { FullWorthSpaceId = space, UserId = member, Role = FullWorthSpaceRoles.Member });
            db.Assets.AddRange(
                new Asset { Id = property, FullWorthSpaceId = space, Name = "Apartment", Kind = AssetKinds.RealEstate, CurrentValue = 250_000m, Currency = "EUR", IncludeInNetWorth = true },
                new Asset { Id = receivable, FullWorthSpaceId = space, Name = "Private loan", Kind = AssetKinds.Receivable, CurrentValue = 5_000m, Currency = "EUR", IncludeInNetWorth = true });
            db.Accounts.Add(new FinanceAccount
            {
                Id = account, FullWorthSpaceId = space, Provider = "manual", IdentificationHash = Guid.NewGuid().ToString("N"),
                ProviderAccountId = "private-account", InstitutionName = "Manual", DisplayName = "Owner only", Currency = "EUR"
            });
            db.AccountOwners.Add(new AccountOwner { AccountId = account, UserId = owner, OwnershipType = AccountOwnershipTypes.Owner });
            db.Transactions.Add(new FinanceTransaction
            {
                Id = transaction, AccountId = account, ExternalKey = "portable-income", Amount = 500m, Currency = "EUR",
                BookingDate = new DateOnly(2026, 9, 2), Counterparty = "Sensitive counterparty"
            });
            await db.SaveChangesAsync();

            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "RealEstateAssetDetails" ("AssetId","PropertyType","UsageType","CountryCode","City","OwnershipSharePercent","UpdatedAt")
                VALUES ({property},'apartment','owner_occupied','DE','Test City',100,now());
                INSERT INTO "ReceivableAssetDetails" ("AssetId","CounterpartyDisplayLabel","OriginalPrincipal","OutstandingPrincipal","Currency","Status","UpdatedAt")
                VALUES ({receivable},'Person',5000,5000,'EUR','active',now());
                INSERT INTO "AssetCashflowEntries" ("Id","FullWorthSpaceId","AssetId","TransactionId","Date","Type","Amount","Direction","Currency","IsPlanned","CreatedAt","UpdatedAt")
                VALUES ({Guid.NewGuid()},{space},{property},{transaction},{new DateOnly(2026,9,2)},'income',100,'income','EUR',false,now(),now());
                INSERT INTO "ReceivablePayments" ("Id","FullWorthSpaceId","AssetId","TransactionId","Date","PrincipalAmount","InterestAmount","Currency","CreatedByUserId","CreatedAt")
                VALUES ({Guid.NewGuid()},{space},{receivable},{transaction},{new DateOnly(2026,9,2)},200,50,'EUR',{owner},now());
                INSERT INTO "AssetDocuments" ("Id","FullWorthSpaceId","AssetId","Category","OriginalFileName","MediaType","StoragePath","Sha256","SizeBytes","CreatedByUserId","CreatedAt")
                VALUES ({document},{space},{property},'purchase_contract','contract.pdf','application/pdf',{relative},{sha},{documentBytes.LongLength},{owner},now());
                """);
        });

        return new(owner, member, outside, space, property, document, relative, sha, documentBytes);
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, Guid userId)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        return request;
    }

    private sealed record SeedResult(
        Guid OwnerId,
        Guid MemberId,
        Guid OutsideId,
        Guid SpaceId,
        Guid AssetId,
        Guid DocumentId,
        string RelativeStoragePath,
        string DocumentSha256,
        byte[] DocumentBytes);
}
