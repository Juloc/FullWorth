using System.Net;
using System.Text.Json;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Api;

public sealed class MarketDataRegressionTests
{
    [Fact]
    public async Task EffectivePricePrefersManualSourceForSameDate()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var owner = Guid.NewGuid();
        var security = Guid.NewGuid();
        await SeedOwnerAndSecurity(factory, owner, security);

        await factory.SeedAsync(async db =>
        {
            var now = DateTimeOffset.UtcNow;
            await db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO "SecurityPrices" ("SecurityId","PriceDate","Price","Currency","Source","CreatedAt","FetchedAt")
VALUES ({security},{new DateOnly(2026,8,29)},{101m},{"EUR"},{"provider"},{now},{now})
""");
            await db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO "SecurityPrices" ("SecurityId","PriceDate","Price","Currency","Source","CreatedAt","FetchedAt")
VALUES ({security},{new DateOnly(2026,8,29)},{99m},{"EUR"},{"manual"},{now.AddMinutes(1)},{now.AddMinutes(1)})
""");
        });

        using var response = await client.SendAsync(UserRequest(HttpMethod.Get,
            $"/api/market-data/securities/{security:D}/effective-price?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}&date=2026-08-30", owner));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(99m, doc.RootElement.GetProperty("price").GetDecimal());
        Assert.Equal("manual", doc.RootElement.GetProperty("source").GetString());
        Assert.Equal("current", doc.RootElement.GetProperty("state").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("ageDays").GetInt32());
    }

    [Fact]
    public async Task EffectivePriceMarksOldCachedValueAsStaleInsteadOfCurrent()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var owner = Guid.NewGuid();
        var security = Guid.NewGuid();
        await SeedOwnerAndSecurity(factory, owner, security);

        await factory.SeedAsync(async db =>
        {
            var now = DateTimeOffset.UtcNow;
            await db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO "SecurityPrices" ("SecurityId","PriceDate","Price","Currency","Source","CreatedAt","FetchedAt")
VALUES ({security},{new DateOnly(2026,8,1)},{80m},{"EUR"},{"manual"},{now},{now})
""");
        });

        using var response = await client.SendAsync(UserRequest(HttpMethod.Get,
            $"/api/market-data/securities/{security:D}/effective-price?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}&date=2026-08-30", owner));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("stale", doc.RootElement.GetProperty("state").GetString());
        Assert.Equal(29, doc.RootElement.GetProperty("ageDays").GetInt32());
    }

    [Fact]
    public async Task RefreshWithoutConfiguredProviderReturnsExplicitConflictAndKeepsCachedPrice()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var owner = Guid.NewGuid();
        var security = Guid.NewGuid();
        await SeedOwnerAndSecurity(factory, owner, security);

        await factory.SeedAsync(async db =>
        {
            var now = DateTimeOffset.UtcNow;
            await db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO "SecurityPrices" ("SecurityId","PriceDate","Price","Currency","Source","CreatedAt","FetchedAt")
VALUES ({security},{new DateOnly(2026,8,20)},{88m},{"EUR"},{"manual"},{now},{now})
""");
        });

        using var refresh = await client.SendAsync(UserRequest(HttpMethod.Post,
            $"/api/market-data/securities/{security:D}/refresh?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}&from=2026-08-20&to=2026-08-30", owner));
        Assert.Equal(HttpStatusCode.Conflict, refresh.StatusCode);
        using (var doc = JsonDocument.Parse(await refresh.Content.ReadAsStringAsync()))
            Assert.Equal("provider_unavailable", doc.RootElement.GetProperty("state").GetString());

        using var effective = await client.SendAsync(UserRequest(HttpMethod.Get,
            $"/api/market-data/securities/{security:D}/effective-price?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}&date=2026-08-30", owner));
        Assert.Equal(HttpStatusCode.OK, effective.StatusCode);
        using var effectiveDoc = JsonDocument.Parse(await effective.Content.ReadAsStringAsync());
        Assert.Equal(88m, effectiveDoc.RootElement.GetProperty("price").GetDecimal());
        Assert.Equal("manual", effectiveDoc.RootElement.GetProperty("source").GetString());
        Assert.Equal("stale", effectiveDoc.RootElement.GetProperty("state").GetString());
    }

    [Fact]
    public async Task BackfillPricesWithoutConfiguredProviderReportsProviderUnavailableAndKeepsStoredPrices()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var owner = Guid.NewGuid();
        var security = Guid.NewGuid();
        var portfolio = Guid.NewGuid();
        await SeedOwnerAndSecurity(factory, owner, security);

        await factory.SeedAsync(async db =>
        {
            var now = DateTimeOffset.UtcNow;
            await db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO "InvestmentPortfolios" ("Id","FullWorthSpaceId","Name","Currency","IsManual","IncludeInNetWorth","IsArchived","CreatedAt","UpdatedAt")
VALUES ({portfolio},{FullWorthSpaceDefaults.LegacyId},{"Backfill Depot"},{"EUR"},{true},{true},{false},{now},{now})
""");
            await db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO "InvestmentTrades"
("Id","FullWorthSpaceId","PortfolioId","SecurityId","TradeType","TradeDate","Quantity","Price","Amount","Currency","Fees","Taxes","WithholdingTax","Source","CreatedAt","UpdatedAt")
VALUES ({Guid.NewGuid()},{FullWorthSpaceDefaults.LegacyId},{portfolio},{security},{"buy"},{new DateOnly(2026,8,1)},{2m},{100m},{200m},{"EUR"},{0m},{0m},{0m},{"manual"},{now},{now})
""");
            // Ein vorhandener Kurs muss unangetastet bleiben, wenn kein Anbieter konfiguriert ist -
            // das ist die eigentliche Behauptung dieses Tests, nicht nur die Abwesenheit eines Fehlers.
            await db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO "SecurityPrices" ("SecurityId","PriceDate","Price","Currency","Source","CreatedAt","FetchedAt")
VALUES ({security},{new DateOnly(2026,8,20)},{88m},{"EUR"},{"manual"},{now},{now})
""");
        });

        using var response = await client.SendAsync(UserRequest(HttpMethod.Post,
            $"/api/investments/portfolios/{portfolio:D}/backfill-prices?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", owner));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var entry = Assert.Single(doc.RootElement.EnumerateArray());
        Assert.Equal(security, entry.GetProperty("securityId").GetGuid());
        Assert.Equal("provider_unavailable", entry.GetProperty("state").GetString());
        Assert.Equal(0, entry.GetProperty("stored").GetInt32());

        using var effective = await client.SendAsync(UserRequest(HttpMethod.Get,
            $"/api/market-data/securities/{security:D}/effective-price?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}&date=2026-08-30", owner));
        using var effectiveDoc = JsonDocument.Parse(await effective.Content.ReadAsStringAsync());
        Assert.Equal(88m, effectiveDoc.RootElement.GetProperty("price").GetDecimal());
        Assert.Equal("manual", effectiveDoc.RootElement.GetProperty("source").GetString());
    }

    [Fact]
    public async Task BackfillPricesForPortfolioWithoutPositionsReturnsEmptyArray()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var owner = Guid.NewGuid();
        var portfolio = Guid.NewGuid();

        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = owner,
                EmailNormalized = $"{owner:N}@EXAMPLE.COM",
                DisplayName = "Empty depot owner",
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
INSERT INTO "InvestmentPortfolios" ("Id","FullWorthSpaceId","Name","Currency","IsManual","IncludeInNetWorth","IsArchived","CreatedAt","UpdatedAt")
VALUES ({portfolio},{FullWorthSpaceDefaults.LegacyId},{"Empty Depot"},{"EUR"},{true},{true},{false},{now},{now})
""");
        });

        using var response = await client.SendAsync(UserRequest(HttpMethod.Post,
            $"/api/investments/portfolios/{portfolio:D}/backfill-prices?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", owner));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Empty(doc.RootElement.EnumerateArray());
    }

    [Fact]
    public async Task BackfillPricesForUnknownPortfolioReturnsNotFound()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var owner = Guid.NewGuid();

        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = owner,
                EmailNormalized = $"{owner:N}@EXAMPLE.COM",
                DisplayName = "Missing depot owner",
                IsActive = true
            });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                UserId = owner,
                Role = FullWorthSpaceRoles.Owner
            });
            await db.SaveChangesAsync();
        });

        using var response = await client.SendAsync(UserRequest(HttpMethod.Post,
            $"/api/investments/portfolios/{Guid.NewGuid():D}/backfill-prices?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", owner));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static async Task SeedOwnerAndSecurity(BackendWebApplicationFactory factory, Guid owner, Guid security)
    {
        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = owner,
                EmailNormalized = $"{owner:N}@EXAMPLE.COM",
                DisplayName = "Market owner",
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
INSERT INTO "Securities" ("Id","FullWorthSpaceId","Name","Ticker","AssetType","Currency","IsActive","CreatedAt","UpdatedAt")
VALUES ({security},{FullWorthSpaceDefaults.LegacyId},{"Market Test ETF"},{"MKT"},{"etf"},{"EUR"},{true},{now},{now})
""");
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
