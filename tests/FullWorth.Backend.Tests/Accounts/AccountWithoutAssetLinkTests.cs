using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Accounts;

/// <summary>
/// An account with NO asset link of any kind: no Asset row, no depot, no duplicate link. Its own balance
/// is the only record of that money.
///
/// This is the plainest case in the product and it had no test, which is why it kept breaking: value
/// surfaces were built around the asset and portfolio entities, and an account that owned its balance
/// outright fell through - it showed up in one list and was missing from the next, and the advice on
/// screen was to create an asset or link a depot by hand. An account's own balance must reach the user
/// everywhere it appears, and correct display must never depend on a manual step afterwards.
/// </summary>
public sealed class AccountWithoutAssetLinkTests
{
    /// <summary>
    /// Rule: an account's own balance reaches the user without a link to a separate entity. Created
    /// through the real endpoint with an opening balance, then read from every surface that shows a
    /// value - with no further call in between, because a value that needs a follow-up step is the bug.
    /// </summary>
    [Fact]
    public async Task A_bare_account_shows_its_value_on_every_surface_with_no_asset_anywhere()
    {
        using var factory = new BackendWebApplicationFactory();
        var (owner, space) = await SeedSpaceAsync(factory);
        using var client = factory.CreateClient();

        var accountId = await CreateAccountAsync(client, owner, space, "Bargeld", "EUR", 1_250m);

        // 1. the account list
        var list = await CurrencyScenario.GetJsonAsync(client, owner, $"/api/accounts?fullWorthSpaceId={space:D}");
        var row = Assert.Single(list.EnumerateArray().ToArray());
        Assert.Equal(1_250m, row.GetProperty("latestBalance").GetProperty("amount").GetDecimal());
        Assert.Equal("EUR", row.GetProperty("latestBalance").GetProperty("currency").GetString());

        // 2. the single-account read the drill-down uses
        var single = await CurrencyScenario.GetJsonAsync(
            client, owner, $"/api/accounts/{accountId:D}?fullWorthSpaceId={space:D}");
        Assert.Equal(1_250m, single.GetProperty("latestBalance").GetProperty("amount").GetDecimal());

        // 3. the home screen
        var dashboard = await CurrencyScenario.GetJsonAsync(
            client, owner, $"/api/analytics/dashboard?fullWorthSpaceId={space:D}");
        Assert.Equal(1_250m, dashboard.GetProperty("accounts").GetDecimal());
        Assert.Equal(1_250m, dashboard.GetProperty("netWorth").GetDecimal());
        Assert.False(dashboard.GetProperty("incomplete").GetBoolean());

        // 4. the wealth overview
        var overview = await CurrencyScenario.GetJsonAsync(
            client, owner, $"/api/wealth/overview?fullWorthSpaceId={space:D}");
        Assert.Equal(1_250m, overview.GetProperty("accounts").GetProperty("amount").GetDecimal());
        Assert.Equal(1_250m, overview.GetProperty("netWorth").GetDecimal());
        Assert.True(overview.GetProperty("isComplete").GetBoolean());
        // Nothing represents this account but itself: a client that hid rows it believed a depot covered
        // must not hide this one.
        Assert.Empty(overview.GetProperty("accountsRepresentedByDepots").EnumerateArray().ToArray());
        Assert.Equal(0m, overview.GetProperty("manualAssets").GetProperty("amount").GetDecimal());

        // 5. the trend's point for today
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var history = await CurrencyScenario.GetJsonAsync(
            client, owner,
            $"/api/wealth/history?fullWorthSpaceId={space:D}&from={today:yyyy-MM-dd}&to={today:yyyy-MM-dd}");
        var point = Assert.Single(history.EnumerateArray().ToArray());
        Assert.Equal(1_250m, point.GetProperty("accounts").GetDecimal());
        Assert.Equal(1_250m, point.GetProperty("netWorth").GetDecimal());

        // And there really is no asset entity behind any of it.
        await factory.SeedAsync(async db =>
        {
            Assert.Equal(0, await db.Assets.AsNoTracking().CountAsync(asset => asset.FullWorthSpaceId == space));
            Assert.Null(await db.Accounts.AsNoTracking()
                .Where(account => account.Id == accountId)
                .Select(account => account.DuplicateOfAccountId)
                .SingleAsync());
        });
    }

    /// <summary>
    /// Rule: the same, in a currency that is not the space's base and that has no rate. The account must
    /// still show ITS OWN amount in ITS OWN currency; only the cross-currency total may be marked
    /// incomplete. The failure mode was an account that vanished from the list because it could not be
    /// converted - the money was gone from the screen, not just from the total.
    /// </summary>
    [Fact]
    public async Task A_foreign_bare_account_is_never_hidden_by_a_conversion_it_cannot_make()
    {
        using var factory = new BackendWebApplicationFactory();
        var (owner, space) = await SeedSpaceAsync(factory);
        using var client = factory.CreateClient();

        await CreateAccountAsync(client, owner, space, "Rekening Rupiah", "IDR", 5_000_000m);

        var list = await CurrencyScenario.GetJsonAsync(client, owner, $"/api/accounts?fullWorthSpaceId={space:D}");
        var row = Assert.Single(list.EnumerateArray().ToArray());
        Assert.Equal(5_000_000m, row.GetProperty("latestBalance").GetProperty("amount").GetDecimal());
        Assert.Equal("IDR", row.GetProperty("latestBalance").GetProperty("currency").GetString());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("baseValue").ValueKind);

        var overview = await CurrencyScenario.GetJsonAsync(
            client, owner, $"/api/wealth/overview?fullWorthSpaceId={space:D}");
        var component = overview.GetProperty("accounts");
        Assert.False(overview.GetProperty("isComplete").GetBoolean());
        Assert.Equal(["IDR"], CurrencyScenario.MissingCurrencies(component));
        // The original is still reported, so the screen can name the money it could not convert.
        Assert.Equal(5_000_000m, CurrencyScenario.OriginalAmounts(component)["IDR"]);
    }

    /// <summary>
    /// Rule: store and keep the original currency. A balance the owner types is the account's own balance,
    /// so it is stored in the account's currency - a snapshot in a different one would silently corrupt
    /// every total, because an aggregation adds the amount with the currency that travelled with it. The
    /// mismatch is refused outright rather than converted or coerced, since nobody can tell afterwards
    /// whether the owner meant euros or rupiah.
    /// </summary>
    [Fact]
    public async Task A_hand_entered_balance_may_not_arrive_in_another_currency()
    {
        using var factory = new BackendWebApplicationFactory();
        var (owner, space) = await SeedSpaceAsync(factory);
        using var client = factory.CreateClient();
        var accountId = await CreateAccountAsync(client, owner, space, "Rekening Rupiah", "IDR", 5_000_000m);

        var refused = await SetBalanceAsync(client, owner, space, accountId, 4_000m, "EUR");
        Assert.Equal(HttpStatusCode.BadRequest, refused);

        var accepted = await SetBalanceAsync(client, owner, space, accountId, 6_000_000m, currency: null);
        Assert.Equal(HttpStatusCode.NoContent, accepted);

        await factory.SeedAsync(async db =>
        {
            var newest = await db.BalanceSnapshots.AsNoTracking()
                .Where(snapshot => snapshot.AccountId == accountId)
                .OrderByDescending(snapshot => snapshot.CapturedAt)
                .FirstAsync();
            Assert.Equal(6_000_000m, newest.Amount);
            Assert.Equal("IDR", newest.Currency);
            // Nothing was stored in euros at any point.
            Assert.DoesNotContain(
                "EUR",
                await db.BalanceSnapshots.AsNoTracking()
                    .Where(snapshot => snapshot.AccountId == accountId)
                    .Select(snapshot => snapshot.Currency)
                    .ToListAsync());
        });
    }

    private static async Task<HttpStatusCode> SetBalanceAsync(
        HttpClient client, Guid owner, Guid space, Guid accountId, decimal amount, string? currency)
    {
        using var request = CurrencyScenario.UserRequest(
            HttpMethod.Put, $"/api/accounts/{accountId:D}/balance?fullWorthSpaceId={space:D}", owner);
        request.Content = JsonContent.Create(new { amount, currency });
        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    private static async Task<(Guid Owner, Guid Space)> SeedSpaceAsync(BackendWebApplicationFactory factory)
    {
        var owner = Guid.NewGuid();
        var space = Guid.NewGuid();
        await factory.SeedAsync(async db =>
        {
            CurrencyScenario.AddOwnerAndSpace(db, owner, space, "EUR", "No assets");
            await db.SaveChangesAsync();
        });
        return (owner, space);
    }

    private static async Task<Guid> CreateAccountAsync(
        HttpClient client, Guid owner, Guid space, string displayName, string currency, decimal initialBalance)
    {
        using var request = CurrencyScenario.UserRequest(HttpMethod.Post, "/api/accounts", owner);
        request.Content = JsonContent.Create(new
        {
            fullWorthSpaceId = space,
            displayName,
            currency,
            initialBalance
        });
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("id").GetGuid();
    }
}
