using System.Net;
using System.Text.Json;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.BankConnections;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Fx;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;

namespace FullWorth.Backend.Tests.Accounts;

/// <summary>
/// PayPal, Wise and Revolut report a wallet per currency, and a sync stores one balance row for each. Every
/// surface reduced that to a single row per account, so the money in every other currency was invisible in
/// the account list, on the dashboard, in net worth and in the export — and because a sync stamps every row
/// with the same CapturedAt and those wallet rows share a balance type, the pick was not even stable: the
/// displayed balance could flip between wallets from one sync to the next.
/// </summary>
public sealed class MultiCurrencyAccountTests
{
    // 1 EUR = 1.10 USD and 1 EUR = 20 000 IDR, so 55 USD = 50 EUR and 2 000 000 IDR = 100 EUR.
    // With the 100 EUR wallet the account is worth 250 EUR in total.
    private const decimal ExpectedTotalInEur = 250m;

    [Fact]
    public async Task Every_wallet_is_listed_and_the_headline_is_the_declared_currency()
    {
        using var factory = new BackendWebApplicationFactory();
        var (owner, space) = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var accounts = await GetAsync(client, owner, "/api/accounts?fullWorthSpaceId=" + space);
        var account = Assert.Single(accounts.EnumerateArray().ToArray());

        Assert.Equal("EUR", account.GetProperty("latestBalance").GetProperty("currency").GetString());
        Assert.Equal(100m, account.GetProperty("latestBalance").GetProperty("amount").GetDecimal());

        var wallets = account.GetProperty("balances").EnumerateArray()
            .ToDictionary(x => x.GetProperty("currency").GetString()!, x => x.GetProperty("amount").GetDecimal());
        Assert.Equal(3, wallets.Count);
        Assert.Equal(100m, wallets["EUR"]);
        Assert.Equal(55m, wallets["USD"]);
        Assert.Equal(2_000_000m, wallets["IDR"]);

        // The row's base-currency line is the WHOLE account, not just the headline wallet.
        Assert.Equal(ExpectedTotalInEur, account.GetProperty("baseValue").GetDecimal());
        Assert.Equal("EUR", account.GetProperty("baseCurrency").GetString());
    }

    [Fact]
    public async Task The_dashboard_counts_every_wallet()
    {
        using var factory = new BackendWebApplicationFactory();
        var (owner, space) = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var dashboard = await GetAsync(client, owner, $"/api/analytics/dashboard?fullWorthSpaceId={space}");

        Assert.Equal(ExpectedTotalInEur, dashboard.GetProperty("accounts").GetDecimal());
        Assert.Equal(ExpectedTotalInEur, dashboard.GetProperty("netWorth").GetDecimal());
        Assert.False(dashboard.GetProperty("incomplete").GetBoolean());
    }

    [Fact]
    public async Task The_wealth_overview_counts_every_wallet_and_keeps_each_original_amount()
    {
        using var factory = new BackendWebApplicationFactory();
        var (owner, space) = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var overview = await GetAsync(client, owner, $"/api/wealth/overview?fullWorthSpaceId={space}&currency=EUR");
        var accounts = overview.GetProperty("accounts");

        Assert.Equal(ExpectedTotalInEur, accounts.GetProperty("amount").GetDecimal());
        // The original currency and amount survive next to the converted figure.
        var originals = accounts.GetProperty("originalAmounts").EnumerateArray()
            .ToDictionary(x => x.GetProperty("currency").GetString()!, x => x.GetProperty("amount").GetDecimal());
        Assert.Equal(100m, originals["EUR"]);
        Assert.Equal(55m, originals["USD"]);
        Assert.Equal(2_000_000m, originals["IDR"]);
    }

    // The headline must be the same balance for the same data. Wallet rows share a balance type and a
    // CapturedAt, so an ordering without a currency rule returned whichever row the database felt like.
    [Fact]
    public async Task The_headline_wallet_is_stable_across_requests()
    {
        using var factory = new BackendWebApplicationFactory();
        var (owner, space) = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var seen = new List<string?>();
        for (var i = 0; i < 5; i++)
        {
            var accounts = await GetAsync(client, owner, "/api/accounts?fullWorthSpaceId=" + space);
            seen.Add(accounts.EnumerateArray().Single()
                .GetProperty("latestBalance").GetProperty("currency").GetString());
        }

        Assert.Single(seen.Distinct());
    }

    // The declared currency wins even when another wallet holds more, so the headline cannot jump because
    // a transfer briefly made a foreign wallet the largest one.
    [Fact]
    public async Task A_bigger_foreign_wallet_does_not_take_over_the_headline()
    {
        using var factory = new BackendWebApplicationFactory();
        var (owner, space) = await SeedAsync(factory, eurAmount: 1m);
        using var client = factory.CreateClient();

        var accounts = await GetAsync(client, owner, "/api/accounts?fullWorthSpaceId=" + space);

        Assert.Equal(
            "EUR",
            accounts.EnumerateArray().Single().GetProperty("latestBalance").GetProperty("currency").GetString());
    }

    private static async Task<(Guid Owner, Guid Space)> SeedAsync(
        BackendWebApplicationFactory factory, decimal eurAmount = 100m)
    {
        var owner = Guid.NewGuid();
        var space = Guid.NewGuid();
        var connection = Guid.NewGuid();
        var account = Guid.NewGuid();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        // One capture, several currencies, all with the same balance type - exactly what PayPal returns.
        var captured = DateTimeOffset.UtcNow;

        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = owner,
                EmailNormalized = $"{owner:N}@EX.COM".ToUpperInvariant(),
                DisplayName = "Wallet owner",
                IsActive = true
            });
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = space, Name = "Wallets", BaseCurrency = "EUR" });
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
                InstitutionName = "PayPal",
                Country = "DE",
                ProviderSessionId = "wallet-session"
            });
            db.Accounts.Add(new FinanceAccount
            {
                Id = account,
                FullWorthSpaceId = space,
                BankConnectionId = connection,
                Provider = "test",
                IdentificationHash = $"wallet-{account:N}",
                ProviderAccountId = $"wallet-{account:N}",
                InstitutionName = "PayPal",
                DisplayName = "PayPal",
                Currency = "EUR",
                IsActive = true,
                IncludeInNetWorth = true
            });
            db.AccountOwners.Add(new AccountOwner
            {
                AccountId = account,
                UserId = owner,
                OwnershipType = AccountOwnershipTypes.Owner
            });
            foreach (var (currency, amount) in new[] { ("EUR", eurAmount), ("USD", 55m), ("IDR", 2_000_000m) })
                db.BalanceSnapshots.Add(new BalanceSnapshot
                {
                    AccountId = account,
                    Amount = amount,
                    Currency = currency,
                    BalanceType = "closingBooked",
                    CapturedAt = captured
                });
            db.FxRates.Add(new FxRate { Date = today, Currency = "USD", Rate = 1.10m });
            db.FxRates.Add(new FxRate { Date = today, Currency = "IDR", Rate = 20_000m });
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
