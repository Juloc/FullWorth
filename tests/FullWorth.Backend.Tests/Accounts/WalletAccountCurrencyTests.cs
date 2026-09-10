using System.Text.Json;
using FullWorth.Backend.Tests.Infrastructure;

namespace FullWorth.Backend.Tests.Accounts;

/// <summary>
/// A PayPal-shaped account: ONE account, a balance per wallet currency, every wallet reported in the same
/// sync with the same capture time and the same balance type.
///
/// MultiCurrencyAccountTests pins a single sync. What is pinned here is what happens over TIME and with an
/// incomplete rate table, which is where the remaining damage was:
/// <list type="bullet">
///   <item>the headline wallet must be the same one after the next sync - a display pick that reorders
///         itself because a transfer changed which wallet is largest reads as money appearing and
///         disappearing;</item>
///   <item>a wallet whose currency has no rate must stay VISIBLE and the total must say it is incomplete.
///         Dropping it silently printed a confident figure that was missing real money, and assuming 1:1
///         added two million euros to an account holding a hundred.</item>
/// </list>
/// </summary>
public sealed class WalletAccountCurrencyTests
{
    // 1 EUR = 1.10 USD, 1 EUR = 20 000 IDR.
    private const decimal UsdPerEuro = 1.10m;
    private const decimal IdrPerEuro = 20_000m;

    /// <summary>
    /// Rule: the headline is presentation and must be deterministic; it must never decide which money
    /// counts. A second sync where a foreign wallet has become the largest must not move it.
    /// </summary>
    [Fact]
    public async Task A_later_sync_does_not_move_the_headline_wallet_or_lose_one()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory, withIdrRate: true);
        using var client = factory.CreateClient();

        var before = await WalletAccountAsync(client, scenario);
        Assert.Equal("EUR", before.GetProperty("latestBalance").GetProperty("currency").GetString());

        // The next sync: same three wallets, a new capture, and USD is now by far the largest holding.
        await factory.SeedAsync(async db =>
        {
            var captured = DateTimeOffset.UtcNow.AddMinutes(5);
            CurrencyScenario.AddBalance(db, scenario.Account, 90m, "EUR", captured);
            CurrencyScenario.AddBalance(db, scenario.Account, 9_900m, "USD", captured);
            CurrencyScenario.AddBalance(db, scenario.Account, 2_000_000m, "IDR", captured);
            await db.SaveChangesAsync();
        });

        var after = await WalletAccountAsync(client, scenario);

        // The account's declared currency still leads, even though USD now holds a hundred times more.
        Assert.Equal("EUR", after.GetProperty("latestBalance").GetProperty("currency").GetString());
        Assert.Equal(90m, after.GetProperty("latestBalance").GetProperty("amount").GetDecimal());
        // Every wallet is still there, at the NEW figures - one row per currency, not one per sync.
        var wallets = CurrencyScenario.Wallets(after);
        Assert.Equal(3, wallets.Count);
        Assert.Equal(90m, wallets["EUR"]);
        Assert.Equal(9_900m, wallets["USD"]);
        Assert.Equal(2_000_000m, wallets["IDR"]);
        // 90 EUR + 9 900 USD (= 9 000 EUR) + 2 000 000 IDR (= 100 EUR).
        Assert.Equal(9_190m, after.GetProperty("baseValue").GetDecimal());
    }

    /// <summary>
    /// Rule: the headline pick is deterministic. An account whose declared currency it does not actually
    /// hold has no obvious wallet to lead with, and "whichever row the database returned first" is not an
    /// answer - the displayed balance flipped between wallets from one request to the next.
    /// </summary>
    [Fact]
    public async Task An_account_that_holds_none_of_its_declared_currency_still_picks_the_same_wallet()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory, withIdrRate: true, withEuroWallet: false);
        using var client = factory.CreateClient();

        var seen = new List<string?>();
        for (var attempt = 0; attempt < 5; attempt++)
            seen.Add((await WalletAccountAsync(client, scenario))
                .GetProperty("latestBalance").GetProperty("currency").GetString());

        Assert.Single(seen.Distinct());
        // Both remaining wallets are still counted whichever one leads.
        var wallets = CurrencyScenario.Wallets(await WalletAccountAsync(client, scenario));
        Assert.Equal(2, wallets.Count);
        Assert.Equal(55m, wallets["USD"]);
        Assert.Equal(2_000_000m, wallets["IDR"]);
    }

    /// <summary>
    /// Rule: a missing FX rate marks the result incomplete - never 1:1, never 0 - and the original amount
    /// stays on screen. The row falls back to its native amounts (exactly as before conversion existed)
    /// rather than showing a base figure that is missing a wallet.
    /// </summary>
    [Fact]
    public async Task A_wallet_without_a_rate_stays_visible_and_makes_the_total_say_so()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory, withIdrRate: false);
        using var client = factory.CreateClient();

        var account = await WalletAccountAsync(client, scenario);
        var wallets = CurrencyScenario.Wallets(account);
        Assert.Equal(3, wallets.Count);
        Assert.Equal(2_000_000m, wallets["IDR"]);
        // No partial base figure: 150 EUR would read as the whole account and be short 100 EUR of rupiah.
        Assert.Equal(JsonValueKind.Null, account.GetProperty("baseValue").ValueKind);

        var overview = await CurrencyScenario.GetJsonAsync(
            client, scenario.Owner, $"/api/wealth/overview?fullWorthSpaceId={scenario.Space:D}");
        var component = overview.GetProperty("accounts");
        Assert.False(overview.GetProperty("isComplete").GetBoolean());
        Assert.Equal(["IDR"], CurrencyScenario.MissingCurrencies(component));
        // The convertible wallets are counted; the rupiah are neither taken as euros nor treated as zero
        // without a word - they are reported as the reason the figure is incomplete.
        Assert.Equal(150m, component.GetProperty("amount").GetDecimal());
        var originals = CurrencyScenario.OriginalAmounts(component);
        Assert.Equal(100m, originals["EUR"]);
        Assert.Equal(55m, originals["USD"]);
        Assert.Equal(2_000_000m, originals["IDR"]);

        var dashboard = await CurrencyScenario.GetJsonAsync(
            client, scenario.Owner, $"/api/analytics/dashboard?fullWorthSpaceId={scenario.Space:D}");
        Assert.True(dashboard.GetProperty("incomplete").GetBoolean());
        Assert.Equal(150m, dashboard.GetProperty("accounts").GetDecimal());
    }

    private sealed record Scenario(Guid Owner, Guid Space, Guid Account);

    private static async Task<Scenario> SeedAsync(
        BackendWebApplicationFactory factory, bool withIdrRate, bool withEuroWallet = true)
    {
        var owner = Guid.NewGuid();
        var space = Guid.NewGuid();
        var account = Guid.NewGuid();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        // One capture, several currencies, one shared balance type - exactly what a wallet provider returns.
        var captured = DateTimeOffset.UtcNow;

        await factory.SeedAsync(async db =>
        {
            CurrencyScenario.AddOwnerAndSpace(db, owner, space, "EUR", "Wallets");
            CurrencyScenario.AddAccount(
                db, space, owner, "PayPal", "EUR", accountId: account, institutionName: "PayPal");
            if (withEuroWallet) CurrencyScenario.AddBalance(db, account, 100m, "EUR", captured);
            CurrencyScenario.AddBalance(db, account, 55m, "USD", captured);
            CurrencyScenario.AddBalance(db, account, 2_000_000m, "IDR", captured);
            CurrencyScenario.AddRate(db, today, "USD", UsdPerEuro);
            if (withIdrRate) CurrencyScenario.AddRate(db, today, "IDR", IdrPerEuro);
            await db.SaveChangesAsync();
        });

        return new Scenario(owner, space, account);
    }

    private static async Task<JsonElement> WalletAccountAsync(HttpClient client, Scenario scenario)
    {
        var accounts = await CurrencyScenario.GetJsonAsync(
            client, scenario.Owner, $"/api/accounts?fullWorthSpaceId={scenario.Space:D}");
        return Assert.Single(accounts.EnumerateArray().ToArray());
    }
}
