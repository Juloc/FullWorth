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
