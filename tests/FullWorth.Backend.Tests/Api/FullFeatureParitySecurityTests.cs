using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FullWorth.Backend.Modules.Categories;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Api;

public sealed class FullFeatureParitySecurityTests
{
    [Fact]
    public async Task OwnerCapabilitiesCannotBeReducedByStaleOverrideRows()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var userId = Guid.NewGuid();

        await SeedMember(factory, userId, "owner");
        await factory.SeedAsync(async db =>
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO "FinanceCapabilityGrants" ("FullWorthSpaceId","UserId","Capability","IsAllowed","UpdatedAt")
VALUES ({FullWorthSpaceDefaults.LegacyId},{userId},{"sharing.manage"},{false},{DateTimeOffset.UtcNow})
ON CONFLICT ("FullWorthSpaceId","UserId","Capability") DO UPDATE SET "IsAllowed"=false
""");
        });

        using var request = UserRequest(HttpMethod.Get,
            $"/api/access/effective?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", userId);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("owner", document.RootElement.GetProperty("template").GetString());
        foreach (var capability in document.RootElement.GetProperty("capabilities").EnumerateObject())
            Assert.True(capability.Value.GetBoolean(), $"Owner capability {capability.Name} was unexpectedly denied.");
    }

    [Fact]
    public async Task DelegatedSharingManagerCannotGrantCapabilitiesTheyDoNotHave()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var caller = Guid.NewGuid();
        var target = Guid.NewGuid();

        await SeedMember(factory, caller, "member");
        await SeedMember(factory, target, "member");
        await factory.SeedAsync(async db =>
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO "FinanceCapabilityGrants" ("FullWorthSpaceId","UserId","Capability","IsAllowed","UpdatedAt")
VALUES ({FullWorthSpaceDefaults.LegacyId},{caller},{"sharing.manage"},{true},{DateTimeOffset.UtcNow})
""");
        });

        using var request = UserRequest(HttpMethod.Put,
            $"/api/access/members/{target:D}?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", caller);
        request.Content = JsonContent.Create(new
        {
            template = "editor",
            overrides = new Dictionary<string, bool>()
        });
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task MemberTemplateCannotManufactureFullWorthSpaceOwner()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var caller = Guid.NewGuid();

        await SeedMember(factory, caller, "member");
        await factory.SeedAsync(async db =>
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO "FinanceCapabilityGrants" ("FullWorthSpaceId","UserId","Capability","IsAllowed","UpdatedAt")
VALUES ({FullWorthSpaceDefaults.LegacyId},{caller},{"sharing.manage"},{true},{DateTimeOffset.UtcNow})
""");
        });

        using var request = UserRequest(HttpMethod.Put,
            $"/api/access/members/{caller:D}?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", caller);
        request.Content = JsonContent.Create(new { template = "owner", overrides = new Dictionary<string, bool>() });
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CategoryMergeIntoDescendantIsRejectedBeforeReferencesMove()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var owner = Guid.NewGuid();
        var sourceId = Guid.NewGuid();
        var childId = Guid.NewGuid();

        await SeedMember(factory, owner, "owner");
        await factory.SeedAsync(async db =>
        {
            db.Categories.AddRange(
                new FinanceCategory
                {
                    Id = sourceId,
                    FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                    Key = $"test.{sourceId:N}",
                    Name = "Parent"
                },
                new FinanceCategory
                {
                    Id = childId,
                    FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                    Key = $"test.{childId:N}",
                    Name = "Child",
                    ParentId = sourceId
                });
            await db.SaveChangesAsync();
        });

        using var request = UserRequest(HttpMethod.Post,
            $"/api/category-ergonomics/{sourceId:D}/merge?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", owner);
        request.Content = JsonContent.Create(new { targetCategoryId = childId, archiveSource = true });
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await factory.SeedAsync(async db =>
        {
            var source = await db.Categories.AsNoTracking().SingleAsync(x => x.Id == sourceId);
            var child = await db.Categories.AsNoTracking().SingleAsync(x => x.Id == childId);
            Assert.False(source.IsArchived);
            Assert.Equal(sourceId, child.ParentId);
        });
    }

    [Fact]
    public async Task InvestmentSellCannotExceedOwnedQuantity()
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
VALUES ({securityId},{FullWorthSpaceDefaults.LegacyId},{"Test ETF"},{"etf"},{"EUR"},{true},{now},{now})
""");
            await db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO "InvestmentPortfolios" ("Id","FullWorthSpaceId","Name","Currency","IsManual","IncludeInNetWorth","IsArchived","CreatedAt","UpdatedAt")
VALUES ({portfolioId},{FullWorthSpaceDefaults.LegacyId},{"Test Depot"},{"EUR"},{true},{true},{false},{now},{now})
""");
            await db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO "InvestmentTrades"
("Id","FullWorthSpaceId","PortfolioId","SecurityId","TradeType","TradeDate","Quantity","Price","Amount","Currency","Fees","Taxes","WithholdingTax","Source","CreatedAt","UpdatedAt")
VALUES ({Guid.NewGuid()},{FullWorthSpaceDefaults.LegacyId},{portfolioId},{securityId},{"buy"},{new DateOnly(2026,8,1)},{1m},{100m},{100m},{"EUR"},{0m},{0m},{0m},{"manual"},{now},{now})
""");
        });

        using var request = UserRequest(HttpMethod.Post,
            $"/api/investments/portfolios/{portfolioId:D}/trades-v2?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", owner);
        request.Content = JsonContent.Create(new
        {
            securityId,
            tradeType = "sell",
            tradeDate = "2026-08-15",
            settlementDate = (string?)null,
            quantity = 2m,
            price = 110m,
            grossAmount = 220m,
            amount = 220m,
            currency = "EUR",
            fees = 0m,
            taxes = 0m,
            withholdingTax = 0m,
            source = "manual"
        });
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task DatabaseTriggerAlsoRejectsOversellOutsideApi()
    {
        using var factory = new BackendWebApplicationFactory();
        var owner = Guid.NewGuid();
        var portfolioId = Guid.NewGuid();
        var securityId = Guid.NewGuid();
        await SeedMember(factory, owner, "owner");

        await factory.SeedAsync(async db =>
        {
            var now = DateTimeOffset.UtcNow;
            await db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO "Securities" ("Id","FullWorthSpaceId","Name","AssetType","Currency","IsActive","CreatedAt","UpdatedAt")
VALUES ({securityId},{FullWorthSpaceDefaults.LegacyId},{"Trigger ETF"},{"etf"},{"EUR"},{true},{now},{now})
""");
            await db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO "InvestmentPortfolios" ("Id","FullWorthSpaceId","Name","Currency","IsManual","IncludeInNetWorth","IsArchived","CreatedAt","UpdatedAt")
VALUES ({portfolioId},{FullWorthSpaceDefaults.LegacyId},{"Trigger Depot"},{"EUR"},{true},{true},{false},{now},{now})
""");
            await db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO "InvestmentTrades"
("Id","FullWorthSpaceId","PortfolioId","SecurityId","TradeType","TradeDate","Quantity","Price","Amount","Currency","Fees","Taxes","WithholdingTax","Source","CreatedAt","UpdatedAt")
VALUES ({Guid.NewGuid()},{FullWorthSpaceDefaults.LegacyId},{portfolioId},{securityId},{"buy"},{new DateOnly(2026,8,1)},{1m},{100m},{100m},{"EUR"},{0m},{0m},{0m},{"manual"},{now},{now})
""");

            await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO "InvestmentTrades"
("Id","FullWorthSpaceId","PortfolioId","SecurityId","TradeType","TradeDate","Quantity","Price","Amount","Currency","Fees","Taxes","WithholdingTax","Source","CreatedAt","UpdatedAt")
VALUES ({Guid.NewGuid()},{FullWorthSpaceDefaults.LegacyId},{portfolioId},{securityId},{"sell"},{new DateOnly(2026,8,15)},{2m},{110m},{220m},{"EUR"},{0m},{0m},{0m},{"manual"},{now.AddMinutes(1)},{now.AddMinutes(1)})
"""));
        });
    }

    private static async Task SeedMember(BackendWebApplicationFactory factory, Guid userId, string role)
    {
        await factory.SeedAsync(async db =>
        {
            if (!await db.Users.AnyAsync(x => x.Id == userId))
            {
                db.Users.Add(new FullWorthUser
                {
                    Id = userId,
                    EmailNormalized = $"{userId:N}@EXAMPLE.COM",
                    DisplayName = $"Parity {userId:N}",
                    IsActive = true
                });
            }
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
