using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Loans;
using FullWorth.Backend.Modules.Portfolio;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;

namespace FullWorth.Backend.Tests.Portfolio;

public sealed class RealEstateMultipleLoansIntegrationTests
{
    [Fact]
    public async Task PropertyCanLinkMultipleCanonicalLoansAndAggregateAllocatedDebt()
    {
        using var factory = new BackendWebApplicationFactory();
        var owner = Guid.NewGuid();
        var space = Guid.NewGuid();
        var asset = Guid.NewGuid();
        var firstLoan = Guid.NewGuid();
        var secondLoan = Guid.NewGuid();

        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = owner,
                EmailNormalized = $"PROPERTY-MULTI-{owner:N}@EXAMPLE.COM",
                DisplayName = "Property multi loan owner",
                IsActive = true
            });
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = space, Name = "Multiple property loans", BaseCurrency = "EUR" });
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
            db.Loans.AddRange(
                Loan(firstLoan, space, "Hauptdarlehen", 160_000m),
                Loan(secondLoan, space, "KfW-Darlehen", 40_000m));
            await db.SaveChangesAsync();
        });

        using var client = factory.CreateClient();
        using var detail = await client.SendAsync(Request(HttpMethod.Put,
            $"/api/assets/{asset}/real-estate?fullWorthSpaceId={space}", owner,
            new { propertyType = "apartment", usageType = "owner_occupied", countryCode = "DE", ownershipSharePercent = 100m }));
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);

        using var first = await client.SendAsync(Request(HttpMethod.Post,
            $"/api/assets/{asset}/debts?fullWorthSpaceId={space}", owner,
            new { loanId = firstLoan, liabilityId = (Guid?)null, relationType = "mortgage", allocationPercent = 100m }));
        using var second = await client.SendAsync(Request(HttpMethod.Post,
            $"/api/assets/{asset}/debts?fullWorthSpaceId={space}", owner,
            new { loanId = secondLoan, liabilityId = (Guid?)null, relationType = "mortgage", allocationPercent = 50m }));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        using var linksResponse = await client.SendAsync(Request(HttpMethod.Get,
            $"/api/assets/{asset}/debts?fullWorthSpaceId={space}", owner));
        Assert.Equal(HttpStatusCode.OK, linksResponse.StatusCode);
        using var linksJson = JsonDocument.Parse(await linksResponse.Content.ReadAsStringAsync());
        Assert.Equal(2, linksJson.RootElement.GetArrayLength());

        using var metricsResponse = await client.SendAsync(Request(HttpMethod.Get,
            $"/api/assets/{asset}/real-estate/metrics?fullWorthSpaceId={space}", owner));
        Assert.Equal(HttpStatusCode.OK, metricsResponse.StatusCode);
        using var metricsJson = JsonDocument.Parse(await metricsResponse.Content.ReadAsStringAsync());
        var metrics = metricsJson.RootElement;
        Assert.Equal(180_000m, metrics.GetProperty("allocatedDebt").GetDecimal());
        Assert.Equal(120_000m, metrics.GetProperty("equity").GetDecimal());
        Assert.Equal(0.6m, metrics.GetProperty("ltv").GetDecimal());
    }

    private static Loan Loan(Guid id, Guid space, string name, decimal balance) => new()
    {
        Id = id,
        FullWorthSpaceId = space,
        Name = name,
        OriginalPrincipal = balance,
        CurrentBalance = balance,
        PaymentAmount = 500m,
        NominalInterestRate = 2m,
        StartDate = new DateOnly(2024, 1, 1),
        EndDate = new DateOnly(2044, 1, 1),
        PaymentFrequency = "monthly",
        Currency = "EUR",
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
}
