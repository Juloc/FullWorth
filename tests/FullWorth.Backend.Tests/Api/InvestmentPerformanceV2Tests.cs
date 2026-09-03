using System.Net;
using System.Text.Json;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Api;

public sealed class InvestmentPerformanceV2Tests
{
    [Fact]
    public async Task MidPeriodDepositDoesNotCreateFakeTwrReturn()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var owner = Guid.NewGuid();
        var portfolioId = Guid.NewGuid();
        var securityId = Guid.NewGuid();
        await SeedMember(factory, owner, "owner");

        await factory.SeedAsync(async db =>
        {
            var now = DateTimeOffset.UtcNow;
            await db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO "Securities" ("Id","FullWorthSpaceId","Name","AssetType","Currency","IsActive","CreatedAt","UpdatedAt")
VALUES ({securityId},{FullWorthSpaceDefaults.LegacyId},{"Performance ETF"},{"etf"},{"EUR"},{true},{now},{now})
""");
            await db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO "InvestmentPortfolios"
("Id","FullWorthSpaceId","Name","Currency","IsManual","IncludeInNetWorth","IsArchived","CreatedAt","UpdatedAt")
VALUES ({portfolioId},{FullWorthSpaceDefaults.LegacyId},{"Performance Depot"},{"EUR"},{true},{true},{false},{now},{now})
""");

            await InsertTrade(db, portfolioId, null, "deposit", new DateOnly(2026,1,1), null, null, 100m, now);
            await InsertTrade(db, portfolioId, securityId, "buy", new DateOnly(2026,1,1), 1m, 100m, 100m, now.AddSeconds(1));
            await InsertTrade(db, portfolioId, null, "deposit", new DateOnly(2026,7,1), null, null, 100m, now.AddSeconds(2));

            await InsertPrice(db, securityId, new DateOnly(2026,1,1), 100m, now);
            await InsertPrice(db, securityId, new DateOnly(2026,7,1), 110m, now.AddSeconds(1));
            await InsertPrice(db, securityId, new DateOnly(2026,12,31), 110m, now.AddSeconds(2));
        });

        using var request = UserRequest(HttpMethod.Get,
            $"/api/investments/portfolios/{portfolioId:D}/performance-v2?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}&from=2026-01-01&to=2026-12-31", owner);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var twr = document.RootElement.GetProperty("twr").GetDecimal();
        var value = document.RootElement.GetProperty("marketValue").GetDecimal();
        Assert.InRange(twr, 0.0999m, 0.1001m);
        Assert.Equal(210m, value);
        Assert.True(document.RootElement.TryGetProperty("xirr", out var xirr));
        Assert.NotEqual(JsonValueKind.Null, xirr.ValueKind);
    }

    [Fact]
    public async Task PortfolioPerformanceIsHiddenWhenLinkedAccountIsNotVisible()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var viewer = Guid.NewGuid();
        var hiddenAccount = Guid.NewGuid();
        var portfolioId = Guid.NewGuid();
        await SeedMember(factory, viewer, "member");

        await factory.SeedAsync(async db =>
        {
            db.Accounts.Add(new FinanceAccount
            {
                Id = hiddenAccount,
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                Provider = "manual",
                IdentificationHash = $"performance-hidden-{hiddenAccount:N}",
                ProviderAccountId = $"performance-hidden-{hiddenAccount:N}",
                InstitutionName = "Manual",
                DisplayName = "Hidden depot cash",
                Currency = "EUR",
                IsActive = true
            });
            await db.SaveChangesAsync();
            var now = DateTimeOffset.UtcNow;
            await db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO "InvestmentPortfolios"
("Id","FullWorthSpaceId","Name","Currency","AccountId","IsManual","IncludeInNetWorth","IsArchived","CreatedAt","UpdatedAt")
VALUES ({portfolioId},{FullWorthSpaceDefaults.LegacyId},{"Hidden Performance Depot"},{"EUR"},{hiddenAccount},{true},{true},{false},{now},{now})
""");
        });

        using var request = UserRequest(HttpMethod.Get,
            $"/api/investments/portfolios/{portfolioId:D}/performance-v2?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", viewer);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static async Task InsertTrade(
        FullWorth.Backend.Data.FullWorthDbContext db,
        Guid portfolioId,
        Guid? securityId,
        string type,
        DateOnly date,
        decimal? quantity,
        decimal? price,
        decimal amount,
        DateTimeOffset now)
    {
        var grossAmount = price.HasValue && quantity.HasValue ? price.Value * quantity.Value : (decimal?)null;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO "InvestmentTrades"
("Id","FullWorthSpaceId","PortfolioId","SecurityId","TradeType","TradeDate","Quantity","Price","GrossAmount","Amount","Currency","Fees","Taxes","WithholdingTax","Source","CreatedAt","UpdatedAt")
VALUES ({Guid.NewGuid()},{FullWorthSpaceDefaults.LegacyId},{portfolioId},{securityId},{type},{date},{quantity},{price},{grossAmount},{amount},{"EUR"},{0m},{0m},{0m},{"manual"},{now},{now})
""");
    }

    private static async Task InsertPrice(
        FullWorth.Backend.Data.FullWorthDbContext db,
        Guid securityId,
        DateOnly date,
        decimal price,
        DateTimeOffset now)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO "SecurityPrices" ("SecurityId","PriceDate","Price","Currency","Source","CreatedAt","FetchedAt")
VALUES ({securityId},{date},{price},{"EUR"},{"manual"},{now},{now})
""");
    }

    private static async Task SeedMember(BackendWebApplicationFactory factory, Guid userId, string role)
    {
        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = userId,
                EmailNormalized = $"{userId:N}@EXAMPLE.COM",
                DisplayName = "Investment performance user",
                IsActive = true
            });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                UserId = userId,
                Role = role
            });
            await db.SaveChangesAsync();
        });
    }

    private static HttpRequestMessage UserRequest(HttpMethod method, string path, Guid userId)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        return request;
    }
}
