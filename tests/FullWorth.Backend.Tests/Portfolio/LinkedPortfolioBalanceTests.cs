using System.Net;
using System.Text.Json;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.BankConnections;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Fx;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Portfolio;

/// <summary>
/// Linking a bank account to a depot means "this account IS the depot", so the depot's valuation replaces
/// the account balance — otherwise the same money is counted twice.
///
/// But the substitution used to be unconditional. A depot with no imported trades values at 0, and so does
/// one whose prices or FX rates are missing, and that 0 REPLACED the real balance: linking an account
/// holding 5,000 to an empty depot silently removed 5,000 from net worth. A link is a display decision and
/// must never be able to delete money.
/// </summary>
public sealed class LinkedPortfolioBalanceTests
{
    [Fact]
    public async Task An_empty_depot_cannot_delete_the_linked_accounts_balance()
    {
        using var scenario = await SeedAsync(portfolioCurrency: "EUR", deposit: null, withUsdRate: false);

        var dashboard = await DashboardAsync(scenario);

        Assert.Equal(5000m, dashboard.GetProperty("accounts").GetDecimal());
        Assert.Equal(5000m, dashboard.GetProperty("netWorth").GetDecimal());
        // The depot contributes nothing, and saying so is the point: the number is the account's, not the
        // depot's valuation.
        Assert.True(dashboard.GetProperty("incomplete").GetBoolean());
    }

    [Fact]
    public async Task A_depot_that_can_value_itself_still_replaces_the_account()
    {
        using var scenario = await SeedAsync(portfolioCurrency: "EUR", deposit: 7000m, withUsdRate: false);

        var dashboard = await DashboardAsync(scenario);

        // Counted once, as the depot: the account balance is out, the portfolio is in.
        Assert.Equal(0m, dashboard.GetProperty("accounts").GetDecimal());
        Assert.Equal(7000m, dashboard.GetProperty("netWorth").GetDecimal());
        Assert.False(dashboard.GetProperty("incomplete").GetBoolean());
    }

    // Same shape of failure through a different door: the depot has real trades, but its currency has no
    // rate, so the valuation is unusable. Substituting it removed the account's money AND added nothing.
    [Fact]
    public async Task A_missing_exchange_rate_cannot_delete_the_linked_accounts_balance()
    {
        using var scenario = await SeedAsync(portfolioCurrency: "GBP", deposit: 900m, withUsdRate: false);

        var dashboard = await DashboardAsync(scenario);

        Assert.Equal(5000m, dashboard.GetProperty("accounts").GetDecimal());
        Assert.Equal(5000m, dashboard.GetProperty("netWorth").GetDecimal());
        Assert.True(dashboard.GetProperty("incomplete").GetBoolean());
    }

    [Fact]
    public async Task An_unlinked_empty_depot_is_simply_worth_nothing()
    {
        using var scenario = await SeedAsync(portfolioCurrency: "EUR", deposit: null, withUsdRate: false, link: false);

        var dashboard = await DashboardAsync(scenario);

        // Nothing was substituted, so nothing is missing either — this must not be flagged incomplete.
        Assert.Equal(5000m, dashboard.GetProperty("accounts").GetDecimal());
        Assert.Equal(5000m, dashboard.GetProperty("netWorth").GetDecimal());
        Assert.False(dashboard.GetProperty("incomplete").GetBoolean());
    }

    // The Wealth page hid every account named by any portfolio, derived on the client. That set is no
    // longer the same as the one the total actually leaves out, so the row would disappear while the
    // headline still contained it. The API states which accounts a depot really represents.
    [Fact]
    public async Task The_overview_names_only_the_accounts_a_depot_really_represents()
    {
        using var empty = await SeedAsync(portfolioCurrency: "EUR", deposit: null, withUsdRate: false);
        using var valued = await SeedAsync(portfolioCurrency: "EUR", deposit: 7000m, withUsdRate: false);

        var emptyOverview = await WealthOverviewAsync(empty);
        var valuedOverview = await WealthOverviewAsync(valued);

        Assert.Empty(emptyOverview.GetProperty("accountsRepresentedByDepots").EnumerateArray());
        Assert.Equal(5000m, emptyOverview.GetProperty("accounts").GetProperty("amount").GetDecimal());

        Assert.Single(valuedOverview.GetProperty("accountsRepresentedByDepots").EnumerateArray());
        Assert.Equal(0m, valuedOverview.GetProperty("accounts").GetProperty("amount").GetDecimal());
    }

    private sealed record Scenario(BackendWebApplicationFactory Factory, Guid UserId, Guid SpaceId) : IDisposable
    {
        public void Dispose() => Factory.Dispose();
    }

    private static async Task<Scenario> SeedAsync(
        string portfolioCurrency, decimal? deposit, bool withUsdRate, bool link = true)
    {
        var factory = new BackendWebApplicationFactory();
        var userId = Guid.NewGuid();
        var spaceId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var portfolioId = Guid.NewGuid();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = userId,
                EmailNormalized = $"{userId:N}@EX.COM".ToUpperInvariant(),
                DisplayName = "Depot owner",
                IsActive = true
            });
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = spaceId, Name = "Depot Space", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = spaceId,
                UserId = userId,
                Role = FullWorthSpaceRoles.Member
            });
            db.BankConnections.Add(new BankConnection
            {
                Id = connectionId,
                FullWorthSpaceId = spaceId,
                Provider = "test",
                InstitutionName = "Broker",
                Country = "DE",
                ProviderSessionId = "depot-session"
            });
            db.Accounts.Add(new FinanceAccount
            {
                Id = accountId,
                FullWorthSpaceId = spaceId,
                BankConnectionId = connectionId,
                Provider = "test",
                IdentificationHash = $"depot-{accountId:N}",
                ProviderAccountId = $"depot-{accountId:N}",
                InstitutionName = "Broker",
                DisplayName = "Depot cash",
                Currency = "EUR",
                IsActive = true,
                IncludeInNetWorth = true
            });
            db.AccountOwners.Add(new AccountOwner
            {
                AccountId = accountId,
                UserId = userId,
                OwnershipType = AccountOwnershipTypes.Owner
            });
            db.BalanceSnapshots.Add(new BalanceSnapshot
            {
                AccountId = accountId,
                Amount = 5000m,
                Currency = "EUR",
                BalanceType = "closingAvailable",
                CapturedAt = DateTimeOffset.UtcNow
            });
            if (withUsdRate) db.FxRates.Add(new FxRate { Date = today, Currency = "USD", Rate = 1.10m });
            await db.SaveChangesAsync();

            var now = DateTimeOffset.UtcNow;
            var linkedAccount = link ? accountId : (Guid?)null;
            await db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO "InvestmentPortfolios"
("Id","FullWorthSpaceId","Name","Currency","AccountId","IsManual","IncludeInNetWorth","IsArchived","CreatedAt","UpdatedAt")
VALUES ({portfolioId},{spaceId},{"Depot"},{portfolioCurrency},{linkedAccount},{true},{true},{false},{now},{now})
""");

            if (deposit is not null)
                await db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO "InvestmentTrades"
("Id","FullWorthSpaceId","PortfolioId","TradeType","TradeDate","Amount","Currency","Fees","Taxes","WithholdingTax","Source","CreatedAt","UpdatedAt")
VALUES ({Guid.NewGuid()},{spaceId},{portfolioId},{"deposit"},{today},{deposit.Value},{portfolioCurrency},{0m},{0m},{0m},{"manual"},{now},{now})
""");
        });

        return new Scenario(factory, userId, spaceId);
    }

    private static Task<JsonElement> DashboardAsync(Scenario scenario) =>
        GetAsync(scenario, $"/api/analytics/dashboard?fullWorthSpaceId={scenario.SpaceId}&currency=EUR");

    private static Task<JsonElement> WealthOverviewAsync(Scenario scenario) =>
        GetAsync(scenario, $"/api/wealth/overview?fullWorthSpaceId={scenario.SpaceId}&currency=EUR");

    private static async Task<JsonElement> GetAsync(Scenario scenario, string path)
    {
        using var client = scenario.Factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", scenario.UserId.ToString("D"));
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }
}
