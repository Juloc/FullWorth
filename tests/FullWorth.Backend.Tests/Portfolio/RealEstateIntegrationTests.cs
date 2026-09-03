using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Loans;
using FullWorth.Backend.Modules.Portfolio;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Portfolio;

public sealed class RealEstateIntegrationTests
{
    [Fact]
    public async Task OwnerCanStorePropertyDetailsCostsAndLoanLinkWithoutCopyingDebt()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory);
        using var client = factory.CreateClient();

        using var putDetail = await client.SendAsync(Request(HttpMethod.Put,
            $"/api/assets/{s.Property}/real-estate?fullWorthSpaceId={s.Space}", s.Owner,
            PropertyDetailPayload()));
        Assert.Equal(HttpStatusCode.OK, putDetail.StatusCode);

        using var propertyPrice = await client.SendAsync(Request(HttpMethod.Post,
            $"/api/assets/{s.Property}/real-estate/acquisition-costs?fullWorthSpaceId={s.Space}", s.Owner,
            new { type = "property_price", amount = 200_000m, currency = "EUR", date = "2022-05-01", notes = "Kaufpreis" }));
        using var tax = await client.SendAsync(Request(HttpMethod.Post,
            $"/api/assets/{s.Property}/real-estate/acquisition-costs?fullWorthSpaceId={s.Space}", s.Owner,
            new { type = "transfer_tax", amount = 10_000m, currency = "EUR", date = "2022-05-01", notes = (string?)null }));
        Assert.Equal(HttpStatusCode.OK, propertyPrice.StatusCode);
        Assert.Equal(HttpStatusCode.OK, tax.StatusCode);

        using var beforeWealth = await client.SendAsync(Request(HttpMethod.Get,
            $"/api/wealth/overview?fullWorthSpaceId={s.Space}&currency=EUR", s.Owner));
        Assert.Equal(HttpStatusCode.OK, beforeWealth.StatusCode);
        using var beforeJson = JsonDocument.Parse(await beforeWealth.Content.ReadAsStringAsync());
        var netWorthBeforeLink = beforeJson.RootElement.GetProperty("netWorth").GetDecimal();

        using var link = await client.SendAsync(Request(HttpMethod.Post,
            $"/api/assets/{s.Property}/debts?fullWorthSpaceId={s.Space}", s.Owner,
            new { loanId = s.Loan, liabilityId = (Guid?)null, relationType = "mortgage", allocationPercent = 50m }));
        Assert.Equal(HttpStatusCode.OK, link.StatusCode);

        using var metrics = await client.SendAsync(Request(HttpMethod.Get,
            $"/api/assets/{s.Property}/real-estate/metrics?fullWorthSpaceId={s.Space}", s.Owner));
        Assert.Equal(HttpStatusCode.OK, metrics.StatusCode);
        using var metricsJson = JsonDocument.Parse(await metrics.Content.ReadAsStringAsync());
        var root = metricsJson.RootElement;
        Assert.Equal(300_000m, root.GetProperty("currentValue").GetDecimal());
        Assert.Equal(100_000m, root.GetProperty("allocatedDebt").GetDecimal());
        Assert.Equal(200_000m, root.GetProperty("equity").GetDecimal());
        Assert.Equal(210_000m, root.GetProperty("acquisitionBasis").GetDecimal());
        Assert.Equal(90_000m, root.GetProperty("valueGain").GetDecimal());
        Assert.Equal(1m / 3m, root.GetProperty("ltv").GetDecimal());
        Assert.True(root.GetProperty("isComplete").GetBoolean());

        using var afterWealth = await client.SendAsync(Request(HttpMethod.Get,
            $"/api/wealth/overview?fullWorthSpaceId={s.Space}&currency=EUR", s.Owner));
        using var afterJson = JsonDocument.Parse(await afterWealth.Content.ReadAsStringAsync());
        Assert.Equal(netWorthBeforeLink, afterJson.RootElement.GetProperty("netWorth").GetDecimal());

        await factory.SeedAsync(async db =>
        {
            var loan = await db.Loans.SingleAsync(item => item.Id == s.Loan);
            Assert.Equal(200_000m, loan.CurrentBalance);
        });
    }

    [Fact]
    public async Task DebtAllocationCannotExceedOneHundredPercentAcrossAssets()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory, includeSecondProperty: true);
        using var client = factory.CreateClient();

        using var first = await client.SendAsync(Request(HttpMethod.Post,
            $"/api/assets/{s.Property}/debts?fullWorthSpaceId={s.Space}", s.Owner,
            new { loanId = s.Loan, liabilityId = (Guid?)null, relationType = "mortgage", allocationPercent = 60m }));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using var second = await client.SendAsync(Request(HttpMethod.Post,
            $"/api/assets/{s.SecondProperty}/debts?fullWorthSpaceId={s.Space}", s.Owner,
            new { loanId = s.Loan, liabilityId = (Guid?)null, relationType = "mortgage", allocationPercent = 50m }));
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
    }

    [Fact]
    public async Task MemberCanReadPropertyButOnlyOwnerCanMutateAndOutsideUserGetsNotFound()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory);
        using var client = factory.CreateClient();

        using var ownerPut = await client.SendAsync(Request(HttpMethod.Put,
            $"/api/assets/{s.Property}/real-estate?fullWorthSpaceId={s.Space}", s.Owner, PropertyDetailPayload()));
        Assert.Equal(HttpStatusCode.OK, ownerPut.StatusCode);

        using var memberRead = await client.SendAsync(Request(HttpMethod.Get,
            $"/api/assets/{s.Property}/real-estate?fullWorthSpaceId={s.Space}", s.Member));
        using var memberWrite = await client.SendAsync(Request(HttpMethod.Put,
            $"/api/assets/{s.Property}/real-estate?fullWorthSpaceId={s.Space}", s.Member, PropertyDetailPayload()));
        using var outsideRead = await client.SendAsync(Request(HttpMethod.Get,
            $"/api/assets/{s.Property}/real-estate?fullWorthSpaceId={s.Space}", s.Outside));

        Assert.Equal(HttpStatusCode.OK, memberRead.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, memberWrite.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, outsideRead.StatusCode);
    }

    [Fact]
    public async Task RealEstateMetadataCannotBeAttachedToOtherAssetKinds()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(Request(HttpMethod.Put,
            $"/api/assets/{s.OtherAsset}/real-estate?fullWorthSpaceId={s.Space}", s.Owner, PropertyDetailPayload()));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static object PropertyDetailPayload() => new
    {
        propertyType = "apartment",
        usageType = "owner_occupied",
        countryCode = "DE",
        postalCode = "74392",
        city = "Freudental",
        street = "Teststraße",
        houseNumber = "1",
        unitLabel = "1. OG",
        yearBuilt = 1998,
        lastMajorModernizationYear = 2024,
        livingAreaSqm = 92.5m,
        rooms = 4m,
        bathrooms = 1,
        floor = 1,
        totalFloors = 3,
        ownershipSharePercent = 100m,
        parkingSpaces = 1,
        garageSpaces = 1,
        condition = "renovated",
        heatingType = "gas",
        balconyTerrace = true,
        basement = true,
        purchaseDate = "2022-05-01",
        purchasePrice = 200_000m,
        purchaseCurrency = "EUR",
        acquisitionCosts = 10_000m,
        equityAtPurchase = 60_000m,
        notes = "Test"
    };

    private static async Task<Scenario> SeedAsync(BackendWebApplicationFactory factory, bool includeSecondProperty = false)
    {
        var s = new Scenario(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await factory.SeedAsync(async db =>
        {
            db.Users.AddRange(User(s.Owner), User(s.Member), User(s.Outside));
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = s.Space, Name = "Property test", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.AddRange(
                new FullWorthSpaceMember { FullWorthSpaceId = s.Space, UserId = s.Owner, Role = FullWorthSpaceRoles.Owner },
                new FullWorthSpaceMember { FullWorthSpaceId = s.Space, UserId = s.Member, Role = FullWorthSpaceRoles.Member });
            db.Assets.AddRange(
                new Asset { Id = s.Property, FullWorthSpaceId = s.Space, Name = "Eigentumswohnung", Kind = AssetKinds.RealEstate, CurrentValue = 300_000m, Currency = "EUR", IncludeInNetWorth = true },
                new Asset { Id = s.OtherAsset, FullWorthSpaceId = s.Space, Name = "Gold", Kind = AssetKinds.PreciousMetal, CurrentValue = 5_000m, Currency = "EUR", IncludeInNetWorth = true });
            if (includeSecondProperty)
                db.Assets.Add(new Asset { Id = s.SecondProperty, FullWorthSpaceId = s.Space, Name = "Wohnung 2", Kind = AssetKinds.RealEstate, CurrentValue = 250_000m, Currency = "EUR", IncludeInNetWorth = true });
            db.Loans.Add(new Loan
            {
                Id = s.Loan,
                FullWorthSpaceId = s.Space,
                Name = "Immobilienkredit",
                OriginalPrincipal = 240_000m,
                CurrentBalance = 200_000m,
                PaymentAmount = 900m,
                NominalInterestRate = 1.84m,
                StartDate = new DateOnly(2022, 5, 1),
                EndDate = new DateOnly(2044, 3, 1),
                PaymentFrequency = "monthly",
                Currency = "EUR",
                IsActive = true
            });
            await db.SaveChangesAsync();
        });
        return s;
    }

    private static FullWorthUser User(Guid id) => new()
    {
        Id = id,
        EmailNormalized = $"PROPERTY-{id:N}@EXAMPLE.COM",
        DisplayName = $"Property {id:N}",
        IsActive = true
    };

    private static HttpRequestMessage Request(HttpMethod method, string path, Guid userId, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        if (body is not null) request.Content = JsonContent.Create(body);
        return request;
    }

    private sealed record Scenario(
        Guid Owner,
        Guid Member,
        Guid Outside,
        Guid Space,
        Guid Property,
        Guid SecondProperty,
        Guid OtherAsset,
        Guid Loan);
}
