using System.Net;
using System.Text.Json;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.BankConnections;
using FullWorth.Backend.Modules.Budgets;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Fx;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;

namespace FullWorth.Backend.Tests.Analytics;

/// <summary>
/// Every analytics answer is expressed in a currency, and an omitted one used to fall back to a hardcoded
/// EUR while the frontend never passed one. A space whose base currency is not EUR therefore had its home
/// screen converted INTO EUR while being labelled with its own currency, so its subtotals collapsed
/// towards 0.
/// </summary>
public sealed class NonEurBaseCurrencyTests
{
    [Fact]
    public async Task The_dashboard_answers_in_the_spaces_own_base_currency()
    {
        using var factory = new BackendWebApplicationFactory();
        var (owner, space) = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var dashboard = await GetAsync(client, owner, $"/api/analytics/dashboard?fullWorthSpaceId={space}");

        Assert.Equal("USD", dashboard.GetProperty("currency").GetString());
        // 200 EUR at 2 USD per EUR plus 50 USD held natively = 450 USD.
        Assert.Equal(450m, dashboard.GetProperty("accounts").GetDecimal());
        Assert.Equal(450m, dashboard.GetProperty("netWorth").GetDecimal());
        Assert.False(dashboard.GetProperty("incomplete").GetBoolean());
    }

    [Fact]
    public async Task An_explicit_currency_still_wins()
    {
        using var factory = new BackendWebApplicationFactory();
        var (owner, space) = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var dashboard = await GetAsync(
            client, owner, $"/api/analytics/dashboard?fullWorthSpaceId={space}&currency=EUR");

        Assert.Equal("EUR", dashboard.GetProperty("currency").GetString());
        Assert.Equal(225m, dashboard.GetProperty("accounts").GetDecimal());
    }

    // Budget status LOOKS like it is served by AnalyticsModule, but BudgetReconciliationCompatibility
    // is a middleware in front of that path and answers it instead - and it already resolved the base
    // currency. Pinned here because nothing in the code makes that shadowing visible.
    [Fact]
    public async Task Budget_status_answers_in_the_spaces_own_currency_too()
    {
        using var factory = new BackendWebApplicationFactory();
        var (owner, space) = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var status = await GetAsync(client, owner, $"/api/analytics/budget-status?fullWorthSpaceId={space}");

        Assert.Equal("USD", status.GetProperty("currency").GetString());
        var item = Assert.Single(status.GetProperty("items").EnumerateArray().ToArray());
        Assert.Equal(500m, item.GetProperty("amount").GetDecimal());
    }

    private static async Task<(Guid Owner, Guid Space)> SeedAsync(BackendWebApplicationFactory factory)
    {
        var owner = Guid.NewGuid();
        var space = Guid.NewGuid();
        var connection = Guid.NewGuid();
        var eurAccount = Guid.NewGuid();
        var usdAccount = Guid.NewGuid();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = owner,
                EmailNormalized = $"{owner:N}@EX.COM".ToUpperInvariant(),
                DisplayName = "US owner",
                IsActive = true
            });
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = space, Name = "US household", BaseCurrency = "USD" });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = space,
                UserId = owner,
                Role = FullWorthSpaceRoles.Owner
            });
            db.BankConnections.Add(new BankConnection
            {
                Id = connection,
                FullWorthSpaceId = space,
                Provider = "test",
                InstitutionName = "Bank",
                Country = "US",
                ProviderSessionId = "usd-session"
            });
            foreach (var (id, currency, amount) in new[] { (eurAccount, "EUR", 200m), (usdAccount, "USD", 50m) })
            {
                db.Accounts.Add(new FinanceAccount
                {
                    Id = id,
                    FullWorthSpaceId = space,
                    BankConnectionId = connection,
                    Provider = "test",
                    IdentificationHash = $"usd-{id:N}",
                    ProviderAccountId = $"usd-{id:N}",
                    InstitutionName = "Bank",
                    DisplayName = currency,
                    Currency = currency,
                    IsActive = true,
                    IncludeInNetWorth = true
                });
                db.AccountOwners.Add(new AccountOwner
                {
                    AccountId = id,
                    UserId = owner,
                    OwnershipType = AccountOwnershipTypes.Owner
                });
                db.BalanceSnapshots.Add(new BalanceSnapshot
                {
                    AccountId = id,
                    Amount = amount,
                    Currency = currency,
                    BalanceType = "closingAvailable",
                    CapturedAt = DateTimeOffset.UtcNow
                });
            }
            // ECB-native: 1 EUR = 2 USD.
            db.FxRates.Add(new FxRate { Date = today, Currency = "USD", Rate = 2m });
            db.Budgets.Add(new Budget
            {
                FullWorthSpaceId = space,
                Name = "Groceries",
                Amount = 500m,
                Currency = "USD",
                Period = "monthly",
                IsActive = true
            });
            await db.SaveChangesAsync();
        });

        return (owner, space);
    }

    private static async Task<JsonElement> GetAsync(HttpClient client, Guid owner, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", owner.ToString("D"));
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }
}
