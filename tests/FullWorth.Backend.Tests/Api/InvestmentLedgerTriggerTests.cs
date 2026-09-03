using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Api;

public sealed class InvestmentLedgerTriggerTests
{
    [Fact]
    public async Task RemovingEarlierBuyIsRejectedWhenItWouldOversellLaterHistory()
    {
        using var factory = new BackendWebApplicationFactory();
        var portfolioId = Guid.NewGuid();
        var securityId = Guid.NewGuid();
        var buyId = Guid.NewGuid();
        var sellId = Guid.NewGuid();

        await factory.SeedAsync(async db =>
        {
            var now = DateTimeOffset.UtcNow;
            await db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO "Securities" ("Id","FullWorthSpaceId","Name","AssetType","Currency","IsActive","CreatedAt","UpdatedAt")
VALUES ({securityId},{FullWorthSpaceDefaults.LegacyId},{"Ledger ETF"},{"etf"},{"EUR"},{true},{now},{now})
""");
            await db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO "InvestmentPortfolios" ("Id","FullWorthSpaceId","Name","Currency","IsManual","IncludeInNetWorth","IsArchived","CreatedAt","UpdatedAt")
VALUES ({portfolioId},{FullWorthSpaceDefaults.LegacyId},{"Ledger Depot"},{"EUR"},{true},{true},{false},{now},{now})
""");
            await db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO "InvestmentTrades"
("Id","FullWorthSpaceId","PortfolioId","SecurityId","TradeType","TradeDate","Quantity","Price","Amount","Currency","Fees","Taxes","WithholdingTax","Source","CreatedAt","UpdatedAt")
VALUES ({buyId},{FullWorthSpaceDefaults.LegacyId},{portfolioId},{securityId},{"buy"},{new DateOnly(2026,8,1)},{2m},{100m},{200m},{"EUR"},{0m},{0m},{0m},{"manual"},{now},{now})
""");
            await db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO "InvestmentTrades"
("Id","FullWorthSpaceId","PortfolioId","SecurityId","TradeType","TradeDate","Quantity","Price","Amount","Currency","Fees","Taxes","WithholdingTax","Source","CreatedAt","UpdatedAt")
VALUES ({sellId},{FullWorthSpaceDefaults.LegacyId},{portfolioId},{securityId},{"sell"},{new DateOnly(2026,8,15)},{2m},{110m},{220m},{"EUR"},{0m},{0m},{0m},{"manual"},{now.AddMinutes(1)},{now.AddMinutes(1)})
""");

            var exception = await Assert.ThrowsAnyAsync<Exception>(() =>
                db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"InvestmentTrades\" WHERE \"Id\"={buyId}"));
            Assert.Contains("oversold", exception.ToString(), StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task ShrinkingEarlierBuyIsRejectedWhenItWouldOversellLaterHistory()
    {
        using var factory = new BackendWebApplicationFactory();
        var portfolioId = Guid.NewGuid();
        var securityId = Guid.NewGuid();
        var buyId = Guid.NewGuid();

        await factory.SeedAsync(async db =>
        {
            var now = DateTimeOffset.UtcNow;
            await db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO "Securities" ("Id","FullWorthSpaceId","Name","AssetType","Currency","IsActive","CreatedAt","UpdatedAt")
VALUES ({securityId},{FullWorthSpaceDefaults.LegacyId},{"Update ETF"},{"etf"},{"EUR"},{true},{now},{now})
""");
            await db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO "InvestmentPortfolios" ("Id","FullWorthSpaceId","Name","Currency","IsManual","IncludeInNetWorth","IsArchived","CreatedAt","UpdatedAt")
VALUES ({portfolioId},{FullWorthSpaceDefaults.LegacyId},{"Update Depot"},{"EUR"},{true},{true},{false},{now},{now})
""");
            await db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO "InvestmentTrades"
("Id","FullWorthSpaceId","PortfolioId","SecurityId","TradeType","TradeDate","Quantity","Price","Amount","Currency","Fees","Taxes","WithholdingTax","Source","CreatedAt","UpdatedAt")
VALUES ({buyId},{FullWorthSpaceDefaults.LegacyId},{portfolioId},{securityId},{"buy"},{new DateOnly(2026,8,1)},{3m},{100m},{300m},{"EUR"},{0m},{0m},{0m},{"manual"},{now},{now})
""");
            await db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO "InvestmentTrades"
("Id","FullWorthSpaceId","PortfolioId","SecurityId","TradeType","TradeDate","Quantity","Price","Amount","Currency","Fees","Taxes","WithholdingTax","Source","CreatedAt","UpdatedAt")
VALUES ({Guid.NewGuid()},{FullWorthSpaceDefaults.LegacyId},{portfolioId},{securityId},{"sell"},{new DateOnly(2026,8,15)},{2m},{110m},{220m},{"EUR"},{0m},{0m},{0m},{"manual"},{now.AddMinutes(1)},{now.AddMinutes(1)})
""");

            var exception = await Assert.ThrowsAnyAsync<Exception>(() =>
                db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"InvestmentTrades\" SET \"Quantity\"={1m} WHERE \"Id\"={buyId}"));
            Assert.Contains("oversold", exception.ToString(), StringComparison.OrdinalIgnoreCase);
        });
    }
}
