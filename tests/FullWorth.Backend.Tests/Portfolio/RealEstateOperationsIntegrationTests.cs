using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FullWorth.Backend.Modules.Contracts;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Portfolio;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;

namespace FullWorth.Backend.Tests.Portfolio;

public sealed class RealEstateOperationsIntegrationTests
{
    [Fact]
    public async Task ImprovementAndRecurringCostLinksReuseCanonicalDomainsAndDoNotChangeMarketValue()
    {
        using var factory = new BackendWebApplicationFactory();
        var owner = Guid.NewGuid();
        var space = Guid.NewGuid();
        var property = Guid.NewGuid();
        var contract = Guid.NewGuid();

        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser { Id = owner, EmailNormalized = $"OPS-{owner:N}@EXAMPLE.COM", DisplayName = "Property ops owner", IsActive = true });
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = space, Name = "Property operations", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember { FullWorthSpaceId = space, UserId = owner, Role = FullWorthSpaceRoles.Owner });
            db.Assets.Add(new Asset { Id = property, FullWorthSpaceId = space, Name = "Eigentumswohnung", Kind = AssetKinds.RealEstate, CurrentValue = 300_000m, Currency = "EUR", IncludeInNetWorth = true });
            db.Contracts.Add(new RecurringContract
            {
                Id = contract,
                FullWorthSpaceId = space,
                Name = "Hausgeld",
                Kind = "contract",
                Amount = 250m,
                Currency = "EUR",
                BillingCycle = "monthly",
                Interval = 1,
                IsActive = true,
                NextDueDate = new DateOnly(2026, 10, 1)
            });
            await db.SaveChangesAsync();
        });

        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Request(HttpMethod.Put, $"/api/assets/{property}/real-estate?fullWorthSpaceId={space}", owner,
            new { propertyType = "apartment", usageType = "owner_occupied", countryCode = "DE", ownershipSharePercent = 100m }))).StatusCode);

        using var before = await client.SendAsync(Request(HttpMethod.Get, $"/api/assets/{property}/real-estate?fullWorthSpaceId={space}", owner));
        using var beforeJson = JsonDocument.Parse(await before.Content.ReadAsStringAsync());
        Assert.Equal(300_000m, beforeJson.RootElement.GetProperty("currentValue").GetDecimal());

        using var capex = await client.SendAsync(Request(HttpMethod.Post, $"/api/assets/{property}/cashflows?fullWorthSpaceId={space}", owner,
            new { transactionId = (Guid?)null, date = "2026-08-01", type = "capex", amount = 5_000m, direction = "expense", currency = "EUR", isPlanned = false, notes = "Fenster" }));
        Assert.Equal(HttpStatusCode.OK, capex.StatusCode);
        using var capexJson = JsonDocument.Parse(await capex.Content.ReadAsStringAsync());
        var cashflowId = capexJson.RootElement.GetProperty("id").GetGuid();

        using var improvement = await client.SendAsync(Request(HttpMethod.Post, $"/api/assets/{property}/real-estate/improvements?fullWorthSpaceId={space}", owner,
            new { title = "Neue Fenster", category = "windows", startDate = "2026-08-01", completedDate = "2026-08-10", cost = 5_000m, currency = "EUR", estimatedValueAdded = 8_000m, description = "Dreifachverglasung", documentId = (Guid?)null }));
        Assert.Equal(HttpStatusCode.OK, improvement.StatusCode);
        using var improvementJson = JsonDocument.Parse(await improvement.Content.ReadAsStringAsync());
        var improvementId = improvementJson.RootElement.GetProperty("id").GetGuid();

        using var linkCashflow = await client.SendAsync(Request(HttpMethod.Post, $"/api/assets/{property}/real-estate/improvements/{improvementId}/cashflows?fullWorthSpaceId={space}", owner,
            new { cashflowEntryId = cashflowId }));
        Assert.Equal(HttpStatusCode.NoContent, linkCashflow.StatusCode);

        using var linkContract = await client.SendAsync(Request(HttpMethod.Post, $"/api/assets/{property}/recurring-contracts?fullWorthSpaceId={space}", owner,
            new { recurringContractId = contract, role = "hoa" }));
        Assert.Equal(HttpStatusCode.OK, linkContract.StatusCode);

        using var contracts = await client.SendAsync(Request(HttpMethod.Get, $"/api/assets/{property}/recurring-contracts?fullWorthSpaceId={space}", owner));
        Assert.Equal(HttpStatusCode.OK, contracts.StatusCode);
        using var contractsJson = JsonDocument.Parse(await contracts.Content.ReadAsStringAsync());
        Assert.Equal(1, contractsJson.RootElement.GetArrayLength());
        var linked = contractsJson.RootElement.EnumerateArray().Single();
        Assert.Equal("Hausgeld", linked.GetProperty("contractName").GetString());
        Assert.Equal(250m, linked.GetProperty("amount").GetDecimal());
        Assert.Equal("hoa", linked.GetProperty("role").GetString());

        using var improvements = await client.SendAsync(Request(HttpMethod.Get, $"/api/assets/{property}/real-estate/improvements?fullWorthSpaceId={space}", owner));
        using var improvementsJson = JsonDocument.Parse(await improvements.Content.ReadAsStringAsync());
        var storedImprovement = improvementsJson.RootElement.EnumerateArray().Single();
        Assert.Equal(8_000m, storedImprovement.GetProperty("estimatedValueAdded").GetDecimal());
        Assert.Equal(cashflowId, storedImprovement.GetProperty("cashflowEntryIds")[0].GetGuid());

        using var after = await client.SendAsync(Request(HttpMethod.Get, $"/api/assets/{property}/real-estate?fullWorthSpaceId={space}", owner));
        using var afterJson = JsonDocument.Parse(await after.Content.ReadAsStringAsync());
        Assert.Equal(300_000m, afterJson.RootElement.GetProperty("currentValue").GetDecimal());
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
