using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Loans;
using FullWorth.Backend.Modules.Portfolio;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;

namespace FullWorth.Backend.Tests.Portfolio;

public sealed class RealEstateUpdateIntegrationTests
{
    [Fact]
    public async Task OwnerCanUpdateAcquisitionCostAndDebtAllocationWithoutChangingDebtBalance()
    {
        using var factory = new BackendWebApplicationFactory();
        var owner = Guid.NewGuid();
        var space = Guid.NewGuid();
        var asset = Guid.NewGuid();
        var loan = Guid.NewGuid();

        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = owner,
                EmailNormalized = $"PROPERTY-UPDATE-{owner:N}@EXAMPLE.COM",
                DisplayName = "Property update owner",
                IsActive = true
            });
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = space, Name = "Property updates", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember { FullWorthSpaceId = space, UserId = owner, Role = FullWorthSpaceRoles.Owner });
            db.Assets.Add(new Asset
            {
                Id = asset,
                FullWorthSpaceId = space,
                Name = "Eigentumswohnung",
                Kind = AssetKinds.RealEstate,
                CurrentValue = 300_000m,
                Currency = "EUR",
                IncludeInNetWorth = true
            });
            db.Loans.Add(new Loan
            {
                Id = loan,
                FullWorthSpaceId = space,
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

        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Request(HttpMethod.Put,
            $"/api/assets/{asset}/real-estate?fullWorthSpaceId={space}", owner,
            new
            {
                propertyType = "apartment",
                usageType = "owner_occupied",
                countryCode = "DE",
                ownershipSharePercent = 100m,
                purchaseDate = "2022-05-01",
                purchasePrice = 200_000m,
                purchaseCurrency = "EUR"
            }))).StatusCode);

        using var createdCostResponse = await client.SendAsync(Request(HttpMethod.Post,
            $"/api/assets/{asset}/real-estate/acquisition-costs?fullWorthSpaceId={space}", owner,
            new { type = "notary", amount = 2_000m, currency = "EUR", date = "2022-05-02", notes = "Alt" }));
        Assert.Equal(HttpStatusCode.OK, createdCostResponse.StatusCode);
        using var createdCostJson = JsonDocument.Parse(await createdCostResponse.Content.ReadAsStringAsync());
        var costId = createdCostJson.RootElement.GetProperty("id").GetGuid();

        using var updateCost = await client.SendAsync(Request(HttpMethod.Put,
            $"/api/assets/{asset}/real-estate/acquisition-costs/{costId}?fullWorthSpaceId={space}", owner,
            new { type = "land_registry", amount = 2_500m, currency = "EUR", date = "2022-05-03", notes = "Korrigiert" }));
        Assert.Equal(HttpStatusCode.NoContent, updateCost.StatusCode);

        using var createdLinkResponse = await client.SendAsync(Request(HttpMethod.Post,
            $"/api/assets/{asset}/debts?fullWorthSpaceId={space}", owner,
            new { loanId = loan, liabilityId = (Guid?)null, relationType = "mortgage", allocationPercent = 50m }));
        Assert.Equal(HttpStatusCode.OK, createdLinkResponse.StatusCode);
        using var createdLinkJson = JsonDocument.Parse(await createdLinkResponse.Content.ReadAsStringAsync());
        var linkId = createdLinkJson.RootElement.GetProperty("id").GetGuid();

        using var updateLink = await client.SendAsync(Request(HttpMethod.Put,
            $"/api/assets/{asset}/debts/{linkId}?fullWorthSpaceId={space}", owner,
            new { loanId = loan, liabilityId = (Guid?)null, relationType = "mortgage", allocationPercent = 75m }));
        Assert.Equal(HttpStatusCode.NoContent, updateLink.StatusCode);

        using var costsResponse = await client.SendAsync(Request(HttpMethod.Get,
            $"/api/assets/{asset}/real-estate/acquisition-costs?fullWorthSpaceId={space}", owner));
        using var costsJson = JsonDocument.Parse(await costsResponse.Content.ReadAsStringAsync());
        var cost = costsJson.RootElement.EnumerateArray().Single(item => item.GetProperty("id").GetGuid() == costId);
        Assert.Equal("land_registry", cost.GetProperty("type").GetString());
        Assert.Equal(2_500m, cost.GetProperty("amount").GetDecimal());

        using var linksResponse = await client.SendAsync(Request(HttpMethod.Get,
            $"/api/assets/{asset}/debts?fullWorthSpaceId={space}", owner));
        using var linksJson = JsonDocument.Parse(await linksResponse.Content.ReadAsStringAsync());
        var link = linksJson.RootElement.EnumerateArray().Single(item => item.GetProperty("id").GetGuid() == linkId);
        Assert.Equal(75m, link.GetProperty("allocationPercent").GetDecimal());
        Assert.Equal(200_000m, link.GetProperty("currentBalance").GetDecimal());
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
