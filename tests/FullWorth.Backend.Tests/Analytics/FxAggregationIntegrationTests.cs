using System.Net;
using System.Text.Json;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.BankConnections;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Fx;
using FullWorth.Backend.Modules.Loans;
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

    // The unit of a balance is the balance's own currency, not the currency the account declares.
    // The dashboard used to select only the amount from the balance row and pair it with the ACCOUNT's
    // currency, so a wallet balance in another currency (PayPal, or a provider reporting in the
    // settlement currency) was converted with the wrong rate - here it would have counted 110 instead
    // of 100. WealthModule always read the balance's own currency, so the two disagreed.
    [Fact]
    public async Task ABalanceIsConvertedWithItsOwnCurrencyNotTheAccountsDeclaredOne()
    {
        using var factory = new BackendWebApplicationFactory();
        var userId = Guid.NewGuid();
        var spaceId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser { Id = userId, EmailNormalized = $"{userId:N}@EX.COM".ToUpperInvariant(), DisplayName = "FX", IsActive = true });
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = spaceId, Name = "FX Space", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember { FullWorthSpaceId = spaceId, UserId = userId, Role = FullWorthSpaceRoles.Member });
            db.BankConnections.Add(new BankConnection { Id = connectionId, FullWorthSpaceId = spaceId, Provider = "test", InstitutionName = "Bank", Country = "DE", ProviderSessionId = "mismatch-session" });
            // The account says EUR; the balance that arrived is USD.
            db.Accounts.Add(new FinanceAccount { Id = accountId, FullWorthSpaceId = spaceId, BankConnectionId = connectionId, Provider = "test", IdentificationHash = "mismatch-1", ProviderAccountId = "mismatch-1", InstitutionName = "Bank", DisplayName = "Wallet", Currency = "EUR", IsActive = true, IncludeInNetWorth = true });
            db.AccountOwners.Add(new AccountOwner { AccountId = accountId, UserId = userId, OwnershipType = AccountOwnershipTypes.Owner });
            db.BalanceSnapshots.Add(new BalanceSnapshot { AccountId = accountId, Amount = 110m, Currency = "USD", BalanceType = "closingAvailable", CapturedAt = DateTimeOffset.UtcNow });
            db.FxRates.Add(new FxRate { Date = today, Currency = "USD", Rate = 1.10m });
            await db.SaveChangesAsync();
        });

        var root = await DashboardAsync(factory, spaceId, userId);
        Assert.Equal(100m, root.GetProperty("accounts").GetDecimal());
        Assert.Equal(100m, root.GetProperty("netWorth").GetDecimal());
        Assert.False(root.GetProperty("incomplete").GetBoolean());
    }

    // The home screen computed its own net worth and never read db.Loans, so the first number the user
    // sees was overstated by every mortgage - while the net-worth history has always counted them.
    [Fact]
    public async Task TheDashboardCountsLoansAsLiabilities()
    {
        using var factory = new BackendWebApplicationFactory();
        var userId = Guid.NewGuid();
        var spaceId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        var accountId = Guid.NewGuid();

        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser { Id = userId, EmailNormalized = $"{userId:N}@EX.COM".ToUpperInvariant(), DisplayName = "Loans", IsActive = true });
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = spaceId, Name = "Loan Space", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember { FullWorthSpaceId = spaceId, UserId = userId, Role = FullWorthSpaceRoles.Member });
            db.BankConnections.Add(new BankConnection { Id = connectionId, FullWorthSpaceId = spaceId, Provider = "test", InstitutionName = "Bank", Country = "DE", ProviderSessionId = "loan-session" });
            db.Accounts.Add(new FinanceAccount { Id = accountId, FullWorthSpaceId = spaceId, BankConnectionId = connectionId, Provider = "test", IdentificationHash = "loan-1", ProviderAccountId = "loan-1", InstitutionName = "Bank", DisplayName = "Giro", Currency = "EUR", IsActive = true, IncludeInNetWorth = true });
            db.AccountOwners.Add(new AccountOwner { AccountId = accountId, UserId = userId, OwnershipType = AccountOwnershipTypes.Owner });
            db.BalanceSnapshots.Add(new BalanceSnapshot { AccountId = accountId, Amount = 1000m, Currency = "EUR", BalanceType = "closingAvailable", CapturedAt = DateTimeOffset.UtcNow });
            db.Loans.Add(new Loan
            {
                FullWorthSpaceId = spaceId,
                Name = "Mortgage",
                OriginalPrincipal = 500m,
                CurrentBalance = 300m,
                PaymentAmount = 10m,
                StartDate = DateOnly.FromDateTime(DateTime.UtcNow).AddYears(-1),
                Currency = "EUR",
                IsActive = true
            });
            // An inactive loan is settled and must NOT reduce net worth.
            db.Loans.Add(new Loan
            {
                FullWorthSpaceId = spaceId,
                Name = "Repaid",
                OriginalPrincipal = 900m,
                CurrentBalance = 900m,
                PaymentAmount = 10m,
                StartDate = DateOnly.FromDateTime(DateTime.UtcNow).AddYears(-3),
                Currency = "EUR",
                IsActive = false
            });
            await db.SaveChangesAsync();
        });

        var root = await DashboardAsync(factory, spaceId, userId);
        Assert.Equal(1000m, root.GetProperty("accounts").GetDecimal());
        Assert.Equal(300m, root.GetProperty("liabilities").GetDecimal());
        Assert.Equal(700m, root.GetProperty("netWorth").GetDecimal());
    }

    private static async Task<JsonElement> DashboardAsync(BackendWebApplicationFactory factory, Guid spaceId, Guid userId)
    {
        using var client = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/analytics/dashboard?fullWorthSpaceId={spaceId}&currency=EUR");
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.Clone();
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
