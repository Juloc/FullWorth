using System.Net;
using System.Text.Json;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.BankConnections;
using FullWorth.Backend.Modules.Categories;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;

namespace FullWorth.Backend.Tests.Analytics.Categories;

public sealed class CategoryAnalyticsIntegrationTests
{
    [Fact]
    public async Task RollsUpChildrenAndReportsTrendAndAverages()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(UserRequest(HttpMethod.Get,
            $"/api/analytics/categories?fullWorthSpaceId={scenario.Space}&year=2026&month=8", scenario.Owner));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var byName = json.RootElement.GetProperty("categories").EnumerateArray()
            .ToDictionary(item => item.GetProperty("name").GetString()!);

        // Parent "Food" rolls up Groceries (100) + Restaurants (40) this month.
        var food = byName["Food"];
        Assert.Equal(140m, food.GetProperty("current").GetDecimal());
        Assert.Equal(60m, food.GetProperty("previous").GetDecimal());   // July: only Groceries 60
        Assert.Equal(80m, food.GetProperty("trendAbsolute").GetDecimal());
        Assert.Equal(133.33m, food.GetProperty("trendPercent").GetDecimal());

        var groceries = byName["Groceries"];
        Assert.Equal(100m, groceries.GetProperty("current").GetDecimal());
        Assert.Equal(food.GetProperty("categoryId").GetGuid(), groceries.GetProperty("parentId").GetGuid());
        // Trailing 3 months before August: May 30, June 30, July 60 → average 40.
        Assert.Equal(40m, groceries.GetProperty("average3").GetDecimal());

        Assert.Equal(40m, byName["Restaurants"].GetProperty("current").GetDecimal());
        Assert.Equal(30m, byName["Transport"].GetProperty("current").GetDecimal());

        var uncategorized = byName["Uncategorized"];
        Assert.Equal(25m, uncategorized.GetProperty("current").GetDecimal());
        Assert.Equal(JsonValueKind.Null, uncategorized.GetProperty("parentId").ValueKind);
    }

    [Fact]
    public async Task SpaceMemberWithoutAccountOwnership_SeesNoSpend()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(UserRequest(HttpMethod.Get,
            $"/api/analytics/categories?fullWorthSpaceId={scenario.Space}&year=2026&month=8", scenario.Member));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Empty(json.RootElement.GetProperty("categories").EnumerateArray());
    }

    [Fact]
    public async Task Outsider_Gets404()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(UserRequest(HttpMethod.Get,
            $"/api/analytics/categories?fullWorthSpaceId={scenario.Space}&year=2026&month=8", scenario.Outside));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static HttpRequestMessage UserRequest(HttpMethod method, string path, Guid userId)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        return request;
    }

    private static async Task<Scenario> SeedScenarioAsync(BackendWebApplicationFactory factory)
    {
        var scenario = new Scenario(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        await factory.SeedAsync(async db =>
        {
            foreach (var userId in new[] { scenario.Owner, scenario.Member, scenario.Outside })
            {
                db.Users.Add(new FullWorthUser
                {
                    Id = userId,
                    EmailNormalized = $"{userId:N}@EXAMPLE.COM".ToUpperInvariant(),
                    DisplayName = $"J4 {userId:N}",
                    IsActive = true
                });
            }

            db.FullWorthSpaces.Add(new FullWorthSpace { Id = scenario.Space, Name = "J4 Space", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.AddRange(
                new FullWorthSpaceMember { FullWorthSpaceId = scenario.Space, UserId = scenario.Owner, Role = FullWorthSpaceRoles.Owner },
                new FullWorthSpaceMember { FullWorthSpaceId = scenario.Space, UserId = scenario.Member, Role = FullWorthSpaceRoles.Member });

            db.BankConnections.Add(new BankConnection
            {
                Id = scenario.Connection,
                FullWorthSpaceId = scenario.Space,
                Provider = "test",
                InstitutionName = "J4 Bank",
                Country = "DE",
                ProviderSessionId = $"j4-{scenario.Connection:N}",
                Status = "AUTHORIZED"
            });

            db.Accounts.Add(new FinanceAccount
            {
                Id = scenario.Account,
                FullWorthSpaceId = scenario.Space,
                BankConnectionId = scenario.Connection,
                Provider = "test",
                IdentificationHash = $"j4-{scenario.Account:N}",
                ProviderAccountId = $"provider-{scenario.Account:N}",
                InstitutionName = "J4 Bank",
                DisplayName = "J4 Account",
                Currency = "EUR"
            });
            db.AccountOwners.Add(new AccountOwner { AccountId = scenario.Account, UserId = scenario.Owner, OwnershipType = AccountOwnershipTypes.Owner });

            db.Categories.AddRange(
                new FinanceCategory { Id = scenario.Food, FullWorthSpaceId = scenario.Space, Key = $"food-{scenario.Food:N}", Name = "Food" },
                new FinanceCategory { Id = scenario.Groceries, FullWorthSpaceId = scenario.Space, ParentId = scenario.Food, Key = $"groc-{scenario.Groceries:N}", Name = "Groceries" },
                new FinanceCategory { Id = scenario.Restaurants, FullWorthSpaceId = scenario.Space, ParentId = scenario.Food, Key = $"rest-{scenario.Restaurants:N}", Name = "Restaurants" },
                new FinanceCategory { Id = scenario.Transport, FullWorthSpaceId = scenario.Space, Key = $"trans-{scenario.Transport:N}", Name = "Transport" });

            // Current month (August 2026).
            Add(db, scenario.Account, scenario.Groceries, -100m, new DateOnly(2026, 8, 5));
            Add(db, scenario.Account, scenario.Restaurants, -40m, new DateOnly(2026, 8, 6));
            Add(db, scenario.Account, scenario.Transport, -30m, new DateOnly(2026, 8, 7));
            Add(db, scenario.Account, null, -25m, new DateOnly(2026, 8, 8));
            // Previous month (July) and trailing history for averages.
            Add(db, scenario.Account, scenario.Groceries, -60m, new DateOnly(2026, 7, 5));
            Add(db, scenario.Account, scenario.Groceries, -30m, new DateOnly(2026, 6, 5));
            Add(db, scenario.Account, scenario.Groceries, -30m, new DateOnly(2026, 5, 5));

            await db.SaveChangesAsync();
        });

        return scenario;
    }

    private static void Add(FullWorth.Backend.Data.FullWorthDbContext db, Guid accountId, Guid? categoryId, decimal amount, DateOnly bookingDate) =>
        db.Transactions.Add(new FinanceTransaction
        {
            AccountId = accountId,
            CategoryId = categoryId,
            ExternalKey = $"J4-{Guid.NewGuid():N}",
            Amount = amount,
            Currency = "EUR",
            BookingDate = bookingDate,
            RawJson = "{}"
        });

    private sealed record Scenario(
        Guid Owner,
        Guid Member,
        Guid Outside,
        Guid Space,
        Guid Connection,
        Guid Account,
        Guid Food,
        Guid Groceries,
        Guid Restaurants,
        Guid Transport);
}
