using System.Net;
using System.Net.Http.Json;
using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.BankConnections;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Backend.Tests.BankConnections;

/// <summary>
/// A FinTS depot is not reachable through <c>Accounts</c>: the snapshot endpoint writes an
/// InvestmentPortfolio keyed by <c>fints:{connectionId}:{depotKey}</c> with its positions as trades. Deleting
/// the connection together with its local data removed accounts, balances and transactions but left all of
/// that behind — so a user who asked for everything to be deleted kept a portfolio that still counted in
/// net worth.
/// </summary>
public sealed class FinTsDepotDeletionTests
{
    [Fact]
    public async Task Deleting_the_connection_removes_its_depot_positions_and_securities()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        await SnapshotAsync(client, scenario);
        Assert.Equal(1, await CountAsync(factory, "InvestmentPortfolios"));
        Assert.Equal(1, await CountAsync(factory, "InvestmentTrades"));
        Assert.Equal(1, await CountAsync(factory, "Securities"));
        Assert.Equal(1, await CountAsync(factory, "SecurityPrices"));

        var deleted = await DeleteAsync(client, scenario);

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(0, await CountAsync(factory, "InvestmentPortfolios"));
        Assert.Equal(0, await CountAsync(factory, "InvestmentTrades"));
        Assert.Equal(0, await CountAsync(factory, "Securities"));
        Assert.Equal(0, await CountAsync(factory, "SecurityPrices"));
    }

    // Securities are shared. One that another portfolio still trades must survive the delete, or removing a
    // bank connection would quietly destroy unrelated investment history.
    [Fact]
    public async Task A_security_another_portfolio_still_trades_is_kept()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        await SnapshotAsync(client, scenario);
        await AddManualTradeForTheSameSecurityAsync(factory, scenario);

        await DeleteAsync(client, scenario);

        // The FinTS depot is gone, the manual one and its security are not.
        Assert.Equal(1, await CountAsync(factory, "InvestmentPortfolios"));
        Assert.Equal(1, await CountAsync(factory, "InvestmentTrades"));
        Assert.Equal(1, await CountAsync(factory, "Securities"));
    }

    [Fact]
    public async Task Another_connections_depot_is_untouched()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        var other = await AddConnectionAsync(factory, scenario);
        using var client = factory.CreateClient();

        await SnapshotAsync(client, scenario);
        await SnapshotAsync(client, scenario with { Connection = other }, depotKey: "depot-2", isin: "IE00B4L5Y984");

        await DeleteAsync(client, scenario);

        Assert.Equal(1, await CountAsync(factory, "InvestmentPortfolios"));
        Assert.Equal(1, await CountAsync(factory, "InvestmentTrades"));
    }

    private sealed record Scenario(Guid Owner, Guid Space, Guid Connection);

    private static async Task<Scenario> SeedAsync(BackendWebApplicationFactory factory)
    {
        var scenario = new Scenario(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = scenario.Owner,
                EmailNormalized = $"{scenario.Owner:N}@EX.COM".ToUpperInvariant(),
                DisplayName = "Depot owner",
                IsActive = true
            });
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = scenario.Space, Name = "Depot", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = scenario.Space,
                UserId = scenario.Owner,
                Role = FullWorthSpaceRoles.Owner
            });
            db.BankConnections.Add(Connection(scenario.Connection, scenario.Space));
            await db.SaveChangesAsync();
        });
        return scenario;
    }

    private static async Task<Guid> AddConnectionAsync(
        BackendWebApplicationFactory factory, Scenario scenario)
    {
        var id = Guid.NewGuid();
        await factory.SeedAsync(async db =>
        {
            db.BankConnections.Add(Connection(id, scenario.Space));
            await db.SaveChangesAsync();
        });
        return id;
    }

    private static BankConnection Connection(Guid id, Guid space) => new()
    {
        Id = id,
        FullWorthSpaceId = space,
        Provider = "fints",
        InstitutionName = "ING",
        Country = "DE",
        ProviderSessionId = $"fints-{id:N}"
    };

    private static async Task SnapshotAsync(
        HttpClient client, Scenario scenario, string depotKey = "depot-1", string isin = "IE00B4L5Y983")
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post, "/internal/banking/fints/investment-snapshot")
        {
            Content = JsonContent.Create(new
            {
                connectionId = scenario.Connection,
                depotKey,
                name = "ING Direkt-Depot",
                currency = "EUR",
                asOf = DateOnly.FromDateTime(DateTime.UtcNow),
                holdings = new[]
                {
                    new
                    {
                        providerKey = $"pos-{depotKey}",
                        name = "World ETF",
                        isin,
                        wkn = (string?)null,
                        currency = "EUR",
                        quantity = 10m,
                        price = (decimal?)100m,
                        priceDate = (DateOnly?)null,
                        marketValue = (decimal?)1000m,
                        exchange = (string?)null
                    }
                }
            })
        };
        request.Headers.Add("X-FullWorth-Ingest-Key", BackendWebApplicationFactory.IngestKey);
        (await client.SendAsync(request)).EnsureSuccessStatusCode();
    }

    private static async Task AddManualTradeForTheSameSecurityAsync(
        BackendWebApplicationFactory factory, Scenario scenario)
    {
        await factory.SeedAsync(async db =>
        {
            var securityId = await db.Database
                .SqlQuery<Guid>($"""SELECT "Id" AS "Value" FROM "Securities" LIMIT 1""")
                .SingleAsync();
            var portfolioId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "InvestmentPortfolios"
                ("Id","FullWorthSpaceId","Name","Currency","IsManual","IncludeInNetWorth","IsArchived","CreatedAt","UpdatedAt")
                VALUES ({portfolioId},{scenario.Space},{"Manual"},{"EUR"},{true},{true},{false},{now},{now})
                """);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "InvestmentTrades"
                ("Id","FullWorthSpaceId","PortfolioId","SecurityId","TradeType","TradeDate","Quantity","Price","Amount","Currency","Fees","Taxes","WithholdingTax","Source","CreatedAt","UpdatedAt")
                VALUES ({Guid.NewGuid()},{scenario.Space},{portfolioId},{securityId},{"buy"},
                        {DateOnly.FromDateTime(DateTime.UtcNow)},{1m},{100m},{100m},{"EUR"},{0m},{0m},{0m},{"manual"},{now},{now})
                """);
        });
    }

    private static async Task<HttpResponseMessage> DeleteAsync(HttpClient client, Scenario scenario)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"/internal/banking/connections/{scenario.Connection}/delete")
        {
            Content = JsonContent.Create(new { fullWorthSpaceId = scenario.Space })
        };
        request.Headers.Add("X-FullWorth-Ingest-Key", BackendWebApplicationFactory.IngestKey);
        request.Headers.Add("X-FullWorth-User-Id", scenario.Owner.ToString("D"));
        return await client.SendAsync(request);
    }

    private static async Task<int> CountAsync(BackendWebApplicationFactory factory, string table)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FullWorthDbContext>();
        // The table name is a literal from this test, never user input.
        return await db.Database.SqlQueryRaw<int>($"SELECT COUNT(*)::int AS \"Value\" FROM \"{table}\"")
            .SingleAsync();
    }
}
