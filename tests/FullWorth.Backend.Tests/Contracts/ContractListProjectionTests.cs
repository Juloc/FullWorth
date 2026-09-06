using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FullWorth.Backend.Modules.Contracts;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;

namespace FullWorth.Backend.Tests.Contracts;

public sealed class ContractListProjectionTests
{
    [Fact]
    public async Task List_ContainsMonthlyAndAnnualizedCost()
    {
        using var factory = new BackendWebApplicationFactory();
        var user = Guid.NewGuid();
        var space = Guid.NewGuid();

        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = user,
                EmailNormalized = $"{user:N}@EXAMPLE.COM",
                DisplayName = "Projection owner",
                IsActive = true
            });
            db.FullWorthSpaces.Add(new FullWorthSpace
            {
                Id = space,
                Name = "Projection",
                BaseCurrency = "EUR"
            });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = space,
                UserId = user,
                Role = FullWorthSpaceRoles.Owner
            });
            db.Contracts.AddRange(
                new RecurringContract
                {
                    FullWorthSpaceId = space,
                    Name = "Monthly",
                    Amount = 120m,
                    Currency = "EUR",
                    BillingCycle = "monthly",
                    Interval = 1,
                    IsActive = true
                },
                new RecurringContract
                {
                    FullWorthSpaceId = space,
                    Name = "Quarterly",
                    Amount = 300m,
                    Currency = "EUR",
                    BillingCycle = "quarterly",
                    Interval = 1,
                    IsActive = true
                });
            await db.SaveChangesAsync();
        });

        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/contracts?fullWorthSpaceId={space}");
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", user.ToString("D"));
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var rows = await response.Content.ReadFromJsonAsync<List<JsonElement>>();
        Assert.NotNull(rows);

        var monthly = rows!.Single(row => row.GetProperty("name").GetString() == "Monthly");
        Assert.Equal(120m, monthly.GetProperty("monthlyEquivalent").GetDecimal());
        Assert.Equal(1440m, monthly.GetProperty("annualizedAmount").GetDecimal());

        var quarterly = rows.Single(row => row.GetProperty("name").GetString() == "Quarterly");
        Assert.Equal(100m, quarterly.GetProperty("monthlyEquivalent").GetDecimal());
        Assert.Equal(1200m, quarterly.GetProperty("annualizedAmount").GetDecimal());
    }
}
