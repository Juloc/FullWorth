using System.Net;
using System.Text.Json;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.BankConnections;
using FullWorth.Backend.Modules.Categories;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;

namespace FullWorth.Backend.Tests.Analytics.Merchants;

public sealed class MerchantAnalyticsIntegrationTests
{
    [Fact]
    public async Task RanksMerchantsWithCountsAveragesTrendAndCategorySplit()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(UserRequest(HttpMethod.Get,
            $"/api/analytics/merchants?fullWorthSpaceId={scenario.Space}&year=2026&month=8", scenario.Owner));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var merchants = json.RootElement.GetProperty("merchants").EnumerateArray().ToList();

        // Ordered by current spend: REWE (150) > SHELL (40) > Unknown (25).
        Assert.Equal("REWE", merchants[0].GetProperty("merchant").GetString());
        Assert.Equal(150m, merchants[0].GetProperty("currentSpend").GetDecimal());
        Assert.Equal(2, merchants[0].GetProperty("currentCount").GetInt32());
        Assert.Equal(75m, merchants[0].GetProperty("currentAverage").GetDecimal());
        Assert.Equal(60m, merchants[0].GetProperty("previousSpend").GetDecimal());
        Assert.Equal(90m, merchants[0].GetProperty("trendAbsolute").GetDecimal());
        Assert.Equal(150m, merchants[0].GetProperty("trendPercent").GetDecimal());

        var reweCategories = merchants[0].GetProperty("categories").EnumerateArray().ToList();
        Assert.Equal("Groceries", reweCategories[0].GetProperty("name").GetString());
        Assert.Equal(150m, reweCategories[0].GetProperty("amount").GetDecimal());

        Assert.Equal("SHELL", merchants[1].GetProperty("merchant").GetString());
        Assert.Equal(40m, merchants[1].GetProperty("currentSpend").GetDecimal());
        Assert.Equal("Unknown", merchants[2].GetProperty("merchant").GetString());
        Assert.Equal(25m, merchants[2].GetProperty("currentSpend").GetDecimal());
    }

    [Fact]
    public async Task TopParameterLimitsResults()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(UserRequest(HttpMethod.Get,
            $"/api/analytics/merchants?fullWorthSpaceId={scenario.Space}&year=2026&month=8&top=1", scenario.Owner));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var merchants = json.RootElement.GetProperty("merchants").EnumerateArray().ToList();
        Assert.Single(merchants);
        Assert.Equal("REWE", merchants[0].GetProperty("merchant").GetString());
    }

    [Fact]
    public async Task Outsider_Gets404()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(UserRequest(HttpMethod.Get,
            $"/api/analytics/merchants?fullWorthSpaceId={scenario.Space}&year=2026&month=8", scenario.Outside));
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
            Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid());

        await factory.SeedAsync(async db =>
        {
            foreach (var userId in new[] { scenario.Owner, scenario.Outside })
            {
                db.Users.Add(new FullWorthUser
                {
                    Id = userId,
                    EmailNormalized = $"{userId:N}@EXAMPLE.COM".ToUpperInvariant(),
                    DisplayName = $"J5 {userId:N}",
                    IsActive = true
                });
            }

            db.FullWorthSpaces.Add(new FullWorthSpace { Id = scenario.Space, Name = "J5 Space", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember { FullWorthSpaceId = scenario.Space, UserId = scenario.Owner, Role = FullWorthSpaceRoles.Owner });

            db.BankConnections.Add(new BankConnection
            {
                Id = scenario.Connection,
                FullWorthSpaceId = scenario.Space,
                Provider = "test",
                InstitutionName = "J5 Bank",
                Country = "DE",
                ProviderSessionId = $"j5-{scenario.Connection:N}",
                Status = "AUTHORIZED"
            });

            db.Accounts.Add(new FinanceAccount
            {
                Id = scenario.Account,
                FullWorthSpaceId = scenario.Space,
                BankConnectionId = scenario.Connection,
                Provider = "test",
                IdentificationHash = $"j5-{scenario.Account:N}",
                ProviderAccountId = $"provider-{scenario.Account:N}",
                InstitutionName = "J5 Bank",
                DisplayName = "J5 Account",
                Currency = "EUR"
            });
            db.AccountOwners.Add(new AccountOwner { AccountId = scenario.Account, UserId = scenario.Owner, OwnershipType = AccountOwnershipTypes.Owner });

            db.Categories.Add(new FinanceCategory { Id = scenario.Groceries, FullWorthSpaceId = scenario.Space, Key = $"groc-{scenario.Groceries:N}", Name = "Groceries" });

            // Current month (August 2026).
            Add(db, scenario.Account, scenario.Groceries, -100m, new DateOnly(2026, 8, 5), "REWE");
            Add(db, scenario.Account, scenario.Groceries, -50m, new DateOnly(2026, 8, 20), "REWE");
            Add(db, scenario.Account, null, -40m, new DateOnly(2026, 8, 7), "SHELL");
            Add(db, scenario.Account, null, -25m, new DateOnly(2026, 8, 8), normalized: null, counterparty: null);
            // Previous month (July) for REWE trend.
            Add(db, scenario.Account, scenario.Groceries, -60m, new DateOnly(2026, 7, 5), "REWE");

            await db.SaveChangesAsync();
        });

        return scenario;
    }

    private static void Add(
        FullWorth.Backend.Data.FullWorthDbContext db, Guid accountId, Guid? categoryId, decimal amount, DateOnly bookingDate,
        string? normalized = null, string? counterparty = null) =>
        db.Transactions.Add(new FinanceTransaction
        {
            AccountId = accountId,
            CategoryId = categoryId,
            ExternalKey = $"J5-{Guid.NewGuid():N}",
            Amount = amount,
            Currency = "EUR",
            BookingDate = bookingDate,
            NormalizedCounterparty = normalized,
            Counterparty = counterparty ?? normalized,
            RawJson = "{}"
        });

    private sealed record Scenario(
        Guid Owner,
        Guid Outside,
        Guid Space,
        Guid Connection,
        Guid Account,
        Guid Groceries,
        Guid Unused);
}
