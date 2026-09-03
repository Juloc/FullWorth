using System.Net;
using System.Text.Json;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Fx;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Api;

public sealed class InvestmentNetWorthV2Tests
{
    [Fact]
    public async Task ContributionConvertsEveryPortfolioIntoFullWorthSpaceBaseCurrency()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var owner = Guid.NewGuid();
        var eurPortfolio = Guid.NewGuid();
        var usdPortfolio = Guid.NewGuid();
        var day = new DateOnly(2026, 8, 30);

        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = owner,
                EmailNormalized = $"{owner:N}@EXAMPLE.COM",
                DisplayName = "Net worth owner",
                IsActive = true
            });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                UserId = owner,
                Role = FullWorthSpaceRoles.Owner
            });
            db.FxRates.Add(new FxRate
            {
                Id = Guid.NewGuid(),
                Date = day,
                Currency = "USD",
                Rate = 2m,
                FetchedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();

            var now = DateTimeOffset.UtcNow;
            await db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO "InvestmentPortfolios"
("Id","FullWorthSpaceId","Name","Currency","IsManual","IncludeInNetWorth","IsArchived","CreatedAt","UpdatedAt")
VALUES
({eurPortfolio},{FullWorthSpaceDefaults.LegacyId},{"EUR Depot"},{"EUR"},{true},{true},{false},{now},{now}),
({usdPortfolio},{FullWorthSpaceDefaults.LegacyId},{"USD Depot"},{"USD"},{true},{true},{false},{now},{now})
""");
            await InsertDeposit(db, eurPortfolio, day, 50m, "EUR", now);
            await InsertDeposit(db, usdPortfolio, day, 100m, "USD", now.AddSeconds(1));
        });

        using var request = UserRequest(HttpMethod.Get,
            $"/api/investments/net-worth-contribution-v2?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}&asOf={day:yyyy-MM-dd}", owner);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("EUR", document.RootElement.GetProperty("currency").GetString());
        Assert.Equal("fullworth-space-base", document.RootElement.GetProperty("currencyMode").GetString());
        Assert.Equal(100m, document.RootElement.GetProperty("total").GetDecimal());
        Assert.False(document.RootElement.GetProperty("incomplete").GetBoolean());
    }

    [Fact]
    public async Task MissingFxMarksContributionIncompleteInsteadOfAssumingOneToOne()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var owner = Guid.NewGuid();
        var portfolio = Guid.NewGuid();
        var day = new DateOnly(2026, 8, 30);

        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = owner,
                EmailNormalized = $"{owner:N}@EXAMPLE.COM",
                DisplayName = "FX incomplete owner",
                IsActive = true
            });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                UserId = owner,
                Role = FullWorthSpaceRoles.Owner
            });
            await db.SaveChangesAsync();
            var now = DateTimeOffset.UtcNow;
            await db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO "InvestmentPortfolios"
("Id","FullWorthSpaceId","Name","Currency","IsManual","IncludeInNetWorth","IsArchived","CreatedAt","UpdatedAt")
VALUES ({portfolio},{FullWorthSpaceDefaults.LegacyId},{"GBP Depot"},{"GBP"},{true},{true},{false},{now},{now})
""");
            await InsertDeposit(db, portfolio, day, 100m, "GBP", now);
        });

        using var request = UserRequest(HttpMethod.Get,
            $"/api/investments/net-worth-contribution-v2?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}&asOf={day:yyyy-MM-dd}", owner);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(document.RootElement.GetProperty("incomplete").GetBoolean());
        Assert.Equal(0m, document.RootElement.GetProperty("total").GetDecimal());
    }

    private static async Task InsertDeposit(
        FullWorth.Backend.Data.FullWorthDbContext db,
        Guid portfolioId,
        DateOnly date,
        decimal amount,
        string currency,
        DateTimeOffset now)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO "InvestmentTrades"
("Id","FullWorthSpaceId","PortfolioId","TradeType","TradeDate","Amount","Currency","Fees","Taxes","WithholdingTax","Source","CreatedAt","UpdatedAt")
VALUES ({Guid.NewGuid()},{FullWorthSpaceDefaults.LegacyId},{portfolioId},{"deposit"},{date},{amount},{currency},{0m},{0m},{0m},{"manual"},{now},{now})
""");
    }

    private static HttpRequestMessage UserRequest(HttpMethod method, string path, Guid userId)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        return request;
    }
}
