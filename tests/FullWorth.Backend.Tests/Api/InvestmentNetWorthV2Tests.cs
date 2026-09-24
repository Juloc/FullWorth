using System.Net;
using System.Text.Json;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Fx;
using FullWorth.Backend.Modules.Portfolio;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

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

        var contribution = await ContributionAsync(factory, owner, day);

        // Die Waehrung IST die Basiswaehrung des Bereichs - fruehere Fassungen lieferten ein
        // zusaetzliches "currencyMode"-Feld, das genau das noch einmal sagte.
        Assert.Equal("EUR", contribution.BaseCurrency);
        Assert.Equal(100m, contribution.Amount);
        Assert.False(contribution.Incomplete);
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

        var contribution = await ContributionAsync(factory, owner, day);

        Assert.True(contribution.Incomplete);
        Assert.Equal(0m, contribution.Amount);
    }

    /// <summary>
    /// Der Beitrag der Depots, direkt aus <see cref="InvestmentNetWorthService"/>.
    ///
    /// Bis #177 ging das ueber <c>GET /api/investments/net-worth-contribution</c>. Die Route war das
    /// Fenster dieser Tests und sonst nichts - im Frontend rief sie niemand, und in der Anwendung
    /// liest den Dienst laengst jemand anderes (Vermoegensuebersicht, Schnappschuesse, Analytics).
    /// Sie ist geloescht; die Rechnung, um die es hier geht, wird jetzt direkt gefragt.
    /// </summary>
    private static async Task<InvestmentNetWorthContribution> ContributionAsync(
        BackendWebApplicationFactory factory, Guid owner, DateOnly asOf)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<InvestmentNetWorthService>();
        return await service.CalculateAsync(FullWorthSpaceDefaults.LegacyId, owner, asOf, CancellationToken.None);
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
