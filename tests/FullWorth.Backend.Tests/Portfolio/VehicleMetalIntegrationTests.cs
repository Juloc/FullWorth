using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Portfolio;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;

namespace FullWorth.Backend.Tests.Portfolio;

public sealed class VehicleMetalIntegrationTests
{
    [Fact]
    public async Task VehicleEstimateIsTransparentAndChangesNetWorthOnlyAfterAcceptance()
    {
        using var factory = new BackendWebApplicationFactory();
        var owner = Guid.NewGuid();
        var space = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        await SeedAsync(factory, owner, null, null, space, new Asset
        {
            Id = assetId,
            FullWorthSpaceId = space,
            Name = "Auto",
            Kind = AssetKinds.Vehicle,
            CurrentValue = 20_000m,
            Currency = "EUR",
            IncludeInNetWorth = true
        });
        using var client = factory.CreateClient();

        var detail = await client.SendAsync(Request(HttpMethod.Put, $"/api/assets/{assetId}/vehicle?fullWorthSpaceId={space}", owner, new
        {
            vehicleType = "car",
            manufacturer = "Test",
            model = "EV",
            vin = "SECRET-VIN",
            licensePlate = "LB-X 1",
            firstRegistrationDate = "2024-01-01",
            modelYear = 2024,
            mileageKm = 20_000,
            powertrain = "electric",
            powerKw = 150m,
            purchaseDate = "2024-01-01",
            purchasePrice = 40_000m,
            purchaseCurrency = "EUR",
            annualMileageEstimate = 15_000
        }));
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);

        var estimateResponse = await client.SendAsync(Request(HttpMethod.Post, $"/api/assets/{assetId}/vehicle/estimate?fullWorthSpaceId={space}", owner, new
        {
            annualDepreciationPercent = 10m,
            mileageAdjustmentPercent = -2m,
            conditionAdjustmentPercent = 1m,
            rangePercent = 10m
        }));
        Assert.Equal(HttpStatusCode.OK, estimateResponse.StatusCode);
        using var estimateJson = JsonDocument.Parse(await estimateResponse.Content.ReadAsStringAsync());
        var estimate = estimateJson.RootElement;
        var amount = estimate.GetProperty("amount").GetDecimal();
        Assert.InRange(amount, 0.01m, 39_999.99m);
        Assert.True(estimate.GetProperty("lowEstimate").GetDecimal() <= amount);
        Assert.True(estimate.GetProperty("highEstimate").GetDecimal() >= amount);
        Assert.Equal("internal_estimate", estimate.GetProperty("method").GetString());

        Assert.Equal(20_000m, await CurrentValueAsync(factory, assetId));

        var accepted = await client.SendAsync(Request(HttpMethod.Post, $"/api/assets/{assetId}/valuations?fullWorthSpaceId={space}", owner, new
        {
            amount,
            currency = "EUR",
            valuedAt = estimate.GetProperty("valuedAt").GetString(),
            method = "internal_estimate",
            lowEstimate = estimate.GetProperty("lowEstimate").GetDecimal(),
            highEstimate = estimate.GetProperty("highEstimate").GetDecimal(),
            isAccepted = true
        }));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(amount, await CurrentValueAsync(factory, assetId));

        var history = await client.SendAsync(Request(HttpMethod.Get, $"/api/assets/{assetId}/valuations?fullWorthSpaceId={space}", owner));
        Assert.Equal(HttpStatusCode.OK, history.StatusCode);
        using var historyJson = JsonDocument.Parse(await history.Content.ReadAsStringAsync());
        Assert.Contains(historyJson.RootElement.EnumerateArray(), x => x.GetProperty("method").GetString() == "internal_estimate" && x.GetProperty("isCurrent").GetBoolean());
    }

    [Fact]
    public async Task PreciousMetalUsesTotalFineWeightAndExplicitReferencePrice()
    {
        using var factory = new BackendWebApplicationFactory();
        var owner = Guid.NewGuid();
        var space = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        await SeedAsync(factory, owner, null, null, space, new Asset
        {
            Id = assetId,
            FullWorthSpaceId = space,
            Name = "Gold",
            Kind = AssetKinds.PreciousMetal,
            CurrentValue = 5_000m,
            Currency = "EUR",
            IncludeInNetWorth = true
        });
        using var client = factory.CreateClient();

        var put = await client.SendAsync(Request(HttpMethod.Put, $"/api/assets/{assetId}/precious-metal?fullWorthSpaceId={space}", owner, new
        {
            metalType = "gold",
            form = "bar",
            quantity = 2m,
            grossWeightGrams = 100m,
            purity = .999m,
            storageLabel = "Tresor",
            purchasePrice = 12_000m,
            purchaseCurrency = "EUR"
        }));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        using var detailJson = JsonDocument.Parse(await put.Content.ReadAsStringAsync());
        Assert.Equal(199.8m, detailJson.RootElement.GetProperty("fineWeightGrams").GetDecimal());

        var estimateResponse = await client.SendAsync(Request(HttpMethod.Post, $"/api/assets/{assetId}/precious-metal/estimate?fullWorthSpaceId={space}", owner, new
        {
            referencePricePerFineGram = 70m,
            currency = "EUR",
            premiumAdjustmentPercent = 0m,
            rangePercent = 5m
        }));
        Assert.Equal(HttpStatusCode.OK, estimateResponse.StatusCode);
        using var estimateJson = JsonDocument.Parse(await estimateResponse.Content.ReadAsStringAsync());
        Assert.Equal(13_986m, estimateJson.RootElement.GetProperty("amount").GetDecimal());
        Assert.Equal(5_000m, await CurrentValueAsync(factory, assetId));
    }

    [Fact]
    public async Task MemberReadsButOnlyOwnerWritesAndOutsideUserGetsNotFound()
    {
        using var factory = new BackendWebApplicationFactory();
        var owner = Guid.NewGuid();
        var member = Guid.NewGuid();
        var outside = Guid.NewGuid();
        var space = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        await SeedAsync(factory, owner, member, outside, space, new Asset
        {
            Id = assetId,
            FullWorthSpaceId = space,
            Name = "Camper",
            Kind = AssetKinds.Vehicle,
            CurrentValue = 30_000m,
            Currency = "EUR",
            IncludeInNetWorth = true
        });
        using var client = factory.CreateClient();

        var ownerPut = await client.SendAsync(Request(HttpMethod.Put, $"/api/assets/{assetId}/vehicle?fullWorthSpaceId={space}", owner, new { vehicleType = "camper" }));
        Assert.Equal(HttpStatusCode.OK, ownerPut.StatusCode);

        var memberGet = await client.SendAsync(Request(HttpMethod.Get, $"/api/assets/{assetId}/vehicle?fullWorthSpaceId={space}", member));
        Assert.Equal(HttpStatusCode.OK, memberGet.StatusCode);

        var memberPut = await client.SendAsync(Request(HttpMethod.Put, $"/api/assets/{assetId}/vehicle?fullWorthSpaceId={space}", member, new { vehicleType = "camper" }));
        Assert.Equal(HttpStatusCode.Forbidden, memberPut.StatusCode);

        var outsideGet = await client.SendAsync(Request(HttpMethod.Get, $"/api/assets/{assetId}/vehicle?fullWorthSpaceId={space}", outside));
        Assert.Equal(HttpStatusCode.NotFound, outsideGet.StatusCode);
    }

    [Fact]
    public async Task SpecializedDetailCannotBeAttachedToWrongAssetKind()
    {
        using var factory = new BackendWebApplicationFactory();
        var owner = Guid.NewGuid();
        var space = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        await SeedAsync(factory, owner, null, null, space, new Asset
        {
            Id = assetId,
            FullWorthSpaceId = space,
            Name = "Other",
            Kind = AssetKinds.Other,
            CurrentValue = 1_000m,
            Currency = "EUR",
            IncludeInNetWorth = true
        });
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Request(HttpMethod.Put, $"/api/assets/{assetId}/vehicle?fullWorthSpaceId={space}", owner, new { vehicleType = "car" }));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static async Task<decimal> CurrentValueAsync(BackendWebApplicationFactory factory, Guid assetId)
    {
        decimal value = 0m;
        await factory.SeedAsync(async db =>
        {
            value = (await db.Assets.FindAsync(assetId))!.CurrentValue;
        });
        return value;
    }

    private static Task SeedAsync(
        BackendWebApplicationFactory factory,
        Guid owner,
        Guid? member,
        Guid? outside,
        Guid space,
        Asset asset) => factory.SeedAsync(async db =>
    {
        db.Users.Add(new FullWorthUser { Id = owner, EmailNormalized = $"OWNER-{owner:N}@EXAMPLE.COM", DisplayName = "Owner", IsActive = true });
        if (member.HasValue) db.Users.Add(new FullWorthUser { Id = member.Value, EmailNormalized = $"MEMBER-{member:N}@EXAMPLE.COM", DisplayName = "Member", IsActive = true });
        if (outside.HasValue) db.Users.Add(new FullWorthUser { Id = outside.Value, EmailNormalized = $"OUTSIDE-{outside:N}@EXAMPLE.COM", DisplayName = "Outside", IsActive = true });
        db.FullWorthSpaces.Add(new FullWorthSpace { Id = space, Name = "Specialized", BaseCurrency = "EUR" });
        db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember { FullWorthSpaceId = space, UserId = owner, Role = FullWorthSpaceRoles.Owner });
        if (member.HasValue) db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember { FullWorthSpaceId = space, UserId = member.Value, Role = FullWorthSpaceRoles.Member });
        db.Assets.Add(asset);
        await db.SaveChangesAsync();
    });

    private static HttpRequestMessage Request(HttpMethod method, string path, Guid userId, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        if (body is not null) request.Content = JsonContent.Create(body);
        return request;
    }
}
