using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Portfolio;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;

namespace FullWorth.Backend.Tests.Portfolio;

public sealed class RealEstateAdvancedIntegrationTests
{
    [Fact]
    public async Task InternalEstimateIsTransparentAndDoesNotChangeAcceptedValueUntilExplicitlyAccepted()
    {
        using var factory = new BackendWebApplicationFactory();
        var (owner, member, outside, space, asset) = await SeedPropertyAsync(factory);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Request(HttpMethod.Put,
            $"/api/assets/{asset}/real-estate?fullWorthSpaceId={space}", owner,
            new { propertyType = "apartment", usageType = "owner_occupied", countryCode = "DE", livingAreaSqm = 80m, ownershipSharePercent = 100m }))).StatusCode);

        using var capabilities = await client.SendAsync(Request(HttpMethod.Get, $"/api/assets/{asset}/real-estate/valuation-capabilities?fullWorthSpaceId={space}", owner));
        Assert.Equal(HttpStatusCode.OK, capabilities.StatusCode);
        using (var json = JsonDocument.Parse(await capabilities.Content.ReadAsStringAsync()))
        {
            Assert.True(json.RootElement.GetProperty("manualAvailable").GetBoolean());
            Assert.True(json.RootElement.GetProperty("internalEstimateAvailable").GetBoolean());
            Assert.Empty(json.RootElement.GetProperty("externalProviders").EnumerateArray());
        }

        using var estimate = await client.SendAsync(Request(HttpMethod.Post, $"/api/assets/{asset}/real-estate/estimate?fullWorthSpaceId={space}", owner,
            new { referencePricePerSqm = 3_000m, conditionAdjustmentPercent = 5m, modernizationAdjustmentPercent = -2m, featureAdjustmentPercent = 2m, rangePercent = 10m }));
        Assert.Equal(HttpStatusCode.OK, estimate.StatusCode);
        decimal estimated;
        using (var json = JsonDocument.Parse(await estimate.Content.ReadAsStringAsync()))
        {
            estimated = json.RootElement.GetProperty("amount").GetDecimal();
            Assert.Equal(252_000m, estimated);
            Assert.Equal(226_800m, json.RootElement.GetProperty("lowEstimate").GetDecimal());
            Assert.Equal(277_200m, json.RootElement.GetProperty("highEstimate").GetDecimal());
            Assert.Equal(3_000m, json.RootElement.GetProperty("inputs").GetProperty("referencePricePerSqm").GetDecimal());
        }

        Assert.Equal(300_000m, await CurrentValueAsync(factory, asset));

        using var accept = await client.SendAsync(Request(HttpMethod.Post, $"/api/assets/{asset}/valuations?fullWorthSpaceId={space}", owner,
            new { amount = estimated, currency = "EUR", valuedAt = "2026-09-02", method = "internal_estimate", lowEstimate = 226_800m, highEstimate = 277_200m, isAccepted = true }));
        Assert.Equal(HttpStatusCode.OK, accept.StatusCode);
        Assert.Equal(252_000m, await CurrentValueAsync(factory, asset));

        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Request(HttpMethod.Post,
            $"/api/assets/{asset}/real-estate/estimate?fullWorthSpaceId={space}", member,
            new { referencePricePerSqm = 3_000m }))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(Request(HttpMethod.Get,
            $"/api/assets/{asset}/real-estate/valuation-capabilities?fullWorthSpaceId={space}", outside))).StatusCode);
    }

    [Fact]
    public async Task OnlyOneEnergyCertificateIsCurrentAndInvalidDatesAreRejected()
    {
        using var factory = new BackendWebApplicationFactory();
        var (owner, member, outside, space, asset) = await SeedPropertyAsync(factory);
        using var client = factory.CreateClient();
        await client.SendAsync(Request(HttpMethod.Put, $"/api/assets/{asset}/real-estate?fullWorthSpaceId={space}", owner,
            new { propertyType = "apartment", usageType = "owner_occupied", countryCode = "DE", ownershipSharePercent = 100m }));

        using var first = await client.SendAsync(Request(HttpMethod.Post, $"/api/assets/{asset}/real-estate/energy-certificates?fullWorthSpaceId={space}", owner,
            new { certificateType = "consumption", energyClass = "C", energyValueKwhSqmYear = 85m, issuedAt = "2021-01-01", validUntil = "2031-01-01", isCurrent = true }));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using var second = await client.SendAsync(Request(HttpMethod.Post, $"/api/assets/{asset}/real-estate/energy-certificates?fullWorthSpaceId={space}", owner,
            new { certificateType = "demand", energyClass = "B", energyValueKwhSqmYear = 70m, issuedAt = "2026-01-01", validUntil = "2036-01-01", isCurrent = true }));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        using var list = await client.SendAsync(Request(HttpMethod.Get, $"/api/assets/{asset}/real-estate/energy-certificates?fullWorthSpaceId={space}", member));
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using (var json = JsonDocument.Parse(await list.Content.ReadAsStringAsync()))
            Assert.Single(json.RootElement.EnumerateArray().Where(x => x.GetProperty("isCurrent").GetBoolean()));

        using var invalid = await client.SendAsync(Request(HttpMethod.Post, $"/api/assets/{asset}/real-estate/energy-certificates?fullWorthSpaceId={space}", owner,
            new { certificateType = "demand", issuedAt = "2030-01-01", validUntil = "2029-01-01", isCurrent = true }));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Request(HttpMethod.Post,
            $"/api/assets/{asset}/real-estate/energy-certificates?fullWorthSpaceId={space}", member,
            new { certificateType = "demand", isCurrent = false }))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(Request(HttpMethod.Get,
            $"/api/assets/{asset}/real-estate/energy-certificates?fullWorthSpaceId={space}", outside))).StatusCode);
    }

    [Fact]
    public async Task AssetDocumentContentIsAuthorizedAndMemberCannotMutate()
    {
        using var factory = new BackendWebApplicationFactory();
        var (owner, member, outside, space, asset) = await SeedPropertyAsync(factory);
        using var client = factory.CreateClient();

        using var upload = Request(HttpMethod.Post, $"/api/assets/{asset}/documents?fullWorthSpaceId={space}", owner);
        var multipart = new MultipartFormDataContent();
        var bytes = Encoding.ASCII.GetBytes("%PDF-1.4\n% FullWorth test\n");
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        multipart.Add(file, "document", "energy.pdf");
        multipart.Add(new StringContent("energy_certificate"), "category");
        upload.Content = multipart;
        using var uploaded = await client.SendAsync(upload);
        Assert.Equal(HttpStatusCode.OK, uploaded.StatusCode);
        Guid documentId;
        using (var json = JsonDocument.Parse(await uploaded.Content.ReadAsStringAsync())) documentId = json.RootElement.GetProperty("id").GetGuid();

        using var memberRead = await client.SendAsync(Request(HttpMethod.Get, $"/api/assets/{asset}/documents/{documentId}/content?fullWorthSpaceId={space}", member));
        Assert.Equal(HttpStatusCode.OK, memberRead.StatusCode);
        Assert.Equal(bytes, await memberRead.Content.ReadAsByteArrayAsync());

        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(Request(HttpMethod.Get,
            $"/api/assets/{asset}/documents/{documentId}/content?fullWorthSpaceId={space}", outside))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Request(HttpMethod.Delete,
            $"/api/assets/{asset}/documents/{documentId}?fullWorthSpaceId={space}", member))).StatusCode);
        Assert.True(File.Exists(Directory.EnumerateFiles(factory.PurchaseStorageRoot, "*", SearchOption.AllDirectories).Single()));
    }

    private static async Task<(Guid Owner, Guid Member, Guid Outside, Guid Space, Guid Asset)> SeedPropertyAsync(BackendWebApplicationFactory factory)
    {
        var owner = Guid.NewGuid(); var member = Guid.NewGuid(); var outside = Guid.NewGuid(); var space = Guid.NewGuid(); var asset = Guid.NewGuid();
        await factory.SeedAsync(async db =>
        {
            db.Users.AddRange(
                new FullWorthUser { Id = owner, EmailNormalized = $"OWNER-{owner:N}@EXAMPLE.COM", DisplayName = "Owner", IsActive = true },
                new FullWorthUser { Id = member, EmailNormalized = $"MEMBER-{member:N}@EXAMPLE.COM", DisplayName = "Member", IsActive = true },
                new FullWorthUser { Id = outside, EmailNormalized = $"OUTSIDE-{outside:N}@EXAMPLE.COM", DisplayName = "Outside", IsActive = true });
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = space, Name = "Advanced property", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.AddRange(
                new FullWorthSpaceMember { FullWorthSpaceId = space, UserId = owner, Role = FullWorthSpaceRoles.Owner },
                new FullWorthSpaceMember { FullWorthSpaceId = space, UserId = member, Role = FullWorthSpaceRoles.Member });
            db.Assets.Add(new Asset { Id = asset, FullWorthSpaceId = space, Name = "Wohnung", Kind = AssetKinds.RealEstate, CurrentValue = 300_000m, Currency = "EUR", IncludeInNetWorth = true });
            await db.SaveChangesAsync();
        });
        return (owner, member, outside, space, asset);
    }

    private static async Task<decimal> CurrentValueAsync(BackendWebApplicationFactory factory, Guid assetId)
    {
        decimal result = 0m;
        await factory.SeedAsync(async db => { result = (await db.Assets.FindAsync(assetId))!.CurrentValue; });
        return result;
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, Guid userId, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        if (body is not null) request.Content = JsonContent.Create(body);
        return request;
    }
}
