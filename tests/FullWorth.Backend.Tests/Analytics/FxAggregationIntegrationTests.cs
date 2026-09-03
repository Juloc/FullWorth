using System.Net;
using System.Text.Json;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.BankConnections;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Fx;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;

namespace FullWorth.Backend.Tests.Analytics;

// §18: net worth is a cross-currency total. Foreign balances must be converted into the base currency
// (not silently dropped as before), and the total must be flagged incomplete when a rate is missing —
// never assumed 1:1.
public sealed class FxAggregationIntegrationTests
{
    [Fact]
    public async Task NetWorthConvertsForeignBalancesAndFlagsIncompleteWhenARateIsMissing()
    {
        using var factory = new BackendWebApplicationFactory();
        var userId = Guid.NewGuid();
        var spaceId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        var usdAccount = Guid.NewGuid();
        var chfAccount = Guid.NewGuid();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser { Id = userId, EmailNormalized = $"{userId:N}@EX.COM".ToUpperInvariant(), DisplayName = "FX", IsActive = true });
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = spaceId, Name = "FX Space", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember { FullWorthSpaceId = spaceId, UserId = userId, Role = FullWorthSpaceRoles.Member });
            db.BankConnections.Add(new BankConnection { Id = connectionId, FullWorthSpaceId = spaceId, Provider = "test", InstitutionName = "Bank", Country = "US", ProviderSessionId = "fx-session" });
            foreach (var (id, cur) in new[] { (usdAccount, "USD"), (chfAccount, "CHF") })
            {
                db.Accounts.Add(new FinanceAccount { Id = id, FullWorthSpaceId = spaceId, BankConnectionId = connectionId, Provider = "test", IdentificationHash = $"fx-{id:N}", ProviderAccountId = $"fx-{id:N}", InstitutionName = "Bank", DisplayName = cur, Currency = cur, IsActive = true, IncludeInNetWorth = true });
                db.AccountOwners.Add(new AccountOwner { AccountId = id, UserId = userId, OwnershipType = AccountOwnershipTypes.Owner });
            }
            db.BalanceSnapshots.Add(new BalanceSnapshot { AccountId = usdAccount, Amount = 110m, Currency = "USD", BalanceType = "closingAvailable", CapturedAt = DateTimeOffset.UtcNow });
            db.BalanceSnapshots.Add(new BalanceSnapshot { AccountId = chfAccount, Amount = 100m, Currency = "CHF", BalanceType = "closingAvailable", CapturedAt = DateTimeOffset.UtcNow });
            // 1 EUR = 1.10 USD today -> 110 USD converts to exactly 100 EUR. CHF has NO rate on purpose.
            db.FxRates.Add(new FxRate { Date = today, Currency = "USD", Rate = 1.10m });
            await db.SaveChangesAsync();
        });

        using var client = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/analytics/dashboard?fullWorthSpaceId={spaceId}&currency=EUR");
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        Assert.Equal(100m, root.GetProperty("accounts").GetDecimal());   // USD converted (was silently dropped before)
        Assert.Equal(100m, root.GetProperty("netWorth").GetDecimal());
        Assert.True(root.GetProperty("incomplete").GetBoolean());        // CHF had no rate -> incomplete, not 1:1
    }

    [Fact]
    public async Task AccountsListCarriesTheConvertedBaseValueForForeignAccountsOnly()
    {
        using var factory = new BackendWebApplicationFactory();
        var userId = Guid.NewGuid();
        var spaceId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        var usdAccount = Guid.NewGuid();
        var eurAccount = Guid.NewGuid();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser { Id = userId, EmailNormalized = $"{userId:N}@EX.COM".ToUpperInvariant(), DisplayName = "FX", IsActive = true });
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = spaceId, Name = "FX Space", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember { FullWorthSpaceId = spaceId, UserId = userId, Role = FullWorthSpaceRoles.Member });
            db.BankConnections.Add(new BankConnection { Id = connectionId, FullWorthSpaceId = spaceId, Provider = "test", InstitutionName = "Bank", Country = "US", ProviderSessionId = "fx-acc-session" });
            foreach (var (id, cur) in new[] { (usdAccount, "USD"), (eurAccount, "EUR") })
            {
                db.Accounts.Add(new FinanceAccount { Id = id, FullWorthSpaceId = spaceId, BankConnectionId = connectionId, Provider = "test", IdentificationHash = $"fxa-{id:N}", ProviderAccountId = $"fxa-{id:N}", InstitutionName = "Bank", DisplayName = cur, Currency = cur, IsActive = true, IncludeInNetWorth = true });
                db.AccountOwners.Add(new AccountOwner { AccountId = id, UserId = userId, OwnershipType = AccountOwnershipTypes.Owner });
            }
            db.BalanceSnapshots.Add(new BalanceSnapshot { AccountId = usdAccount, Amount = 220m, Currency = "USD", BalanceType = "closingAvailable", CapturedAt = DateTimeOffset.UtcNow });
            db.BalanceSnapshots.Add(new BalanceSnapshot { AccountId = eurAccount, Amount = 50m, Currency = "EUR", BalanceType = "closingAvailable", CapturedAt = DateTimeOffset.UtcNow });
            db.FxRates.Add(new FxRate { Date = today, Currency = "USD", Rate = 1.10m });
            await db.SaveChangesAsync();
        });

        using var client = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/accounts?fullWorthSpaceId={spaceId}");
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var accounts = json.RootElement.EnumerateArray().ToList();
        var usd = accounts.Single(a => a.GetProperty("currency").GetString() == "USD");
        Assert.Equal(200m, usd.GetProperty("baseValue").GetDecimal());   // 220 USD / 1.10 = 200 EUR
        Assert.Equal("EUR", usd.GetProperty("baseCurrency").GetString());
        var eur = accounts.Single(a => a.GetProperty("currency").GetString() == "EUR");
        Assert.True(eur.GetProperty("baseValue").ValueKind == JsonValueKind.Null);   // base currency -> no secondary
    }
}
