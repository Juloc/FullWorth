using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Portfolio;

/// <summary>
/// Several accounts, several currencies, and a space whose base currency is NOT the euro - read through
/// the real endpoints (account list, home screen, wealth overview).
///
/// EUR is hardcoded in enough fallbacks that a non-EUR space was the one configuration nothing verified:
/// the rate table is stored ECB-native (1 EUR in the foreign currency), so every conversion into a
/// non-EUR base is a cross-rate derived through EUR, and any place that forgot the second leg reported a
/// space's own currency label on top of euro figures - subtotals collapsing towards zero. The rules under
/// test are the same four as everywhere: keep the original, convert as a derived value, mark a missing
/// rate incomplete, and count linked money once.
/// </summary>
public sealed class NonEuroBaseCurrencyIntegrationTests
{
    private const decimal UsdPerEuro = 1.10m;
    private const decimal IdrPerEuro = 20_000m;

    // In the space's own base currency (IDR): 100 EUR = 2 000 000, 55 USD = 50 EUR = 1 000 000, and the
    // 3 000 000 IDR account is already base. The CHF account has no rate and is therefore not in here.
    private const decimal ConvertibleTotalIdr = 6_000_000m;

    /// <summary>
    /// Rule: a total is expressed in the space's OWN base currency, and every original survives beside
    /// it. Without the EUR→IDR leg of the cross-rate the euro and dollar accounts were added as if they
    /// were rupiah, so a 6 000 000 IDR net worth read as 3 000 150.
    /// </summary>
    [Fact]
    public async Task The_wealth_overview_totals_in_the_spaces_own_base_currency()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory, withUnconvertibleAccount: false);
        using var client = factory.CreateClient();

        var overview = await CurrencyScenario.GetJsonAsync(
            client, scenario.Owner, $"/api/wealth/overview?fullWorthSpaceId={scenario.Space:D}");
        var accounts = overview.GetProperty("accounts");

        Assert.Equal("IDR", overview.GetProperty("currency").GetString());
        Assert.Equal("IDR", accounts.GetProperty("currency").GetString());
        Assert.Equal(ConvertibleTotalIdr, accounts.GetProperty("amount").GetDecimal());
        Assert.Equal(ConvertibleTotalIdr, overview.GetProperty("netWorth").GetDecimal());
        Assert.True(overview.GetProperty("isComplete").GetBoolean());

        // Each account's own currency and amount, untouched by the conversion above.
        var originals = CurrencyScenario.OriginalAmounts(accounts);
        Assert.Equal(100m, originals["EUR"]);
        Assert.Equal(55m, originals["USD"]);
        Assert.Equal(3_000_000m, originals["IDR"]);
    }

    /// <summary>
    /// Rule: the conversion is a secondary figure. The row keeps its native amount and gains a base one;
    /// an account already IN the base currency needs no second figure at all, and one that could not be
    /// converted must show its native amount rather than disappear.
    /// </summary>
    [Fact]
    public async Task Each_account_row_keeps_its_own_currency_and_gains_the_base_one()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory, withUnconvertibleAccount: true);
        using var client = factory.CreateClient();

        var rows = await AccountRowsAsync(client, scenario);

        var euro = rows["Girokonto"];
        Assert.Equal("EUR", euro.GetProperty("latestBalance").GetProperty("currency").GetString());
        Assert.Equal(100m, euro.GetProperty("latestBalance").GetProperty("amount").GetDecimal());
        Assert.Equal(2_000_000m, euro.GetProperty("baseValue").GetDecimal());
        Assert.Equal("IDR", euro.GetProperty("baseCurrency").GetString());

        var dollar = rows["Dollar Wallet"];
        Assert.Equal(55m, dollar.GetProperty("latestBalance").GetProperty("amount").GetDecimal());
        Assert.Equal(1_000_000m, dollar.GetProperty("baseValue").GetDecimal());

        // Already the base currency: the native amount IS the base amount, so there is no second line.
        var rupiah = rows["Rekening Rupiah"];
        Assert.Equal("IDR", rupiah.GetProperty("latestBalance").GetProperty("currency").GetString());
        Assert.Equal(3_000_000m, rupiah.GetProperty("latestBalance").GetProperty("amount").GetDecimal());
        Assert.Equal(JsonValueKind.Null, rupiah.GetProperty("baseValue").ValueKind);

        // No CHF rate: no base figure, but the francs are still on screen.
        var franc = rows["Franken"];
        Assert.Equal("CHF", franc.GetProperty("latestBalance").GetProperty("currency").GetString());
        Assert.Equal(80m, franc.GetProperty("latestBalance").GetProperty("amount").GetDecimal());
        Assert.Equal(JsonValueKind.Null, franc.GetProperty("baseValue").ValueKind);
    }

    /// <summary>
    /// Rule: the home screen and the wealth page must not disagree about the same data. The dashboard
    /// used to fall back to a hardcoded EUR when the frontend passed no currency - so a non-EUR space's
    /// figures were converted into euros and then labelled with its own currency.
    /// </summary>
    [Fact]
    public async Task The_dashboard_reports_the_same_base_currency_and_the_same_total()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory, withUnconvertibleAccount: false);
        using var client = factory.CreateClient();

        var dashboard = await CurrencyScenario.GetJsonAsync(
            client, scenario.Owner, $"/api/analytics/dashboard?fullWorthSpaceId={scenario.Space:D}");

        Assert.Equal("IDR", dashboard.GetProperty("currency").GetString());
        Assert.Equal(ConvertibleTotalIdr, dashboard.GetProperty("accounts").GetDecimal());
        Assert.Equal(ConvertibleTotalIdr, dashboard.GetProperty("netWorth").GetDecimal());
        Assert.False(dashboard.GetProperty("incomplete").GetBoolean());
    }

    /// <summary>
    /// Rule: a missing FX rate marks the result incomplete - never 1:1, never 0. 1:1 would have added 80
    /// rupiah for 80 francs (worth about 1 300 000), and dropping the account without a word printed a
    /// total that was short real money.
    /// </summary>
    [Fact]
    public async Task An_account_in_a_currency_without_a_rate_is_named_not_guessed()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory, withUnconvertibleAccount: true);
        using var client = factory.CreateClient();

        var overview = await CurrencyScenario.GetJsonAsync(
            client, scenario.Owner, $"/api/wealth/overview?fullWorthSpaceId={scenario.Space:D}");
        var accounts = overview.GetProperty("accounts");

        Assert.False(overview.GetProperty("isComplete").GetBoolean());
        Assert.Equal(["CHF"], overview.GetProperty("missingCurrencies")
            .EnumerateArray().Select(item => item.GetString()).ToArray());
        Assert.Equal(["CHF"], CurrencyScenario.MissingCurrencies(accounts));
        // The convertible part, exactly - not 6 000 080 (1:1) and not 0.
        Assert.Equal(ConvertibleTotalIdr, accounts.GetProperty("amount").GetDecimal());
        Assert.Equal(80m, CurrencyScenario.OriginalAmounts(accounts)["CHF"]);
    }

    /// <summary>
    /// Rule: no double counting. Two accounts the owner declared the same real-world account are counted
    /// once - in a non-EUR base currency too, where the same money converted twice is a much bigger
    /// number than the discrepancy it started as. The linked account keeps its balance and stays visible;
    /// linking is a statement about counting, not a deletion.
    /// </summary>
    [Fact]
    public async Task A_linked_account_is_counted_once_and_still_shows_its_balance()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory, withUnconvertibleAccount: false, withDuplicateAccount: true);
        using var client = factory.CreateClient();

        // Before the link the same 500 000 IDR is in the total twice over, which is the complaint.
        var before = await CurrencyScenario.GetJsonAsync(
            client, scenario.Owner, $"/api/wealth/overview?fullWorthSpaceId={scenario.Space:D}");
        Assert.Equal(
            ConvertibleTotalIdr + 500_000m,
            before.GetProperty("accounts").GetProperty("amount").GetDecimal());

        using var link = CurrencyScenario.UserRequest(
            HttpMethod.Put,
            $"/api/accounts/{scenario.Duplicate!.Value:D}/link?fullWorthSpaceId={scenario.Space:D}",
            scenario.Owner);
        link.Content = JsonContent.Create(new { duplicateOfAccountId = scenario.RupiahAccount });
        using var linked = await client.SendAsync(link);
        Assert.Equal(HttpStatusCode.NoContent, linked.StatusCode);

        var after = await CurrencyScenario.GetJsonAsync(
            client, scenario.Owner, $"/api/wealth/overview?fullWorthSpaceId={scenario.Space:D}");
        Assert.Equal(ConvertibleTotalIdr, after.GetProperty("accounts").GetProperty("amount").GetDecimal());
        Assert.Equal(ConvertibleTotalIdr, after.GetProperty("netWorth").GetDecimal());

        // Still listed, still holding its money, and the row says which account carries it now.
        var rows = await AccountRowsAsync(client, scenario);
        var duplicate = rows["Rupiah zweite Verbindung"];
        Assert.Equal(500_000m, duplicate.GetProperty("latestBalance").GetProperty("amount").GetDecimal());
        Assert.False(duplicate.GetProperty("includeInNetWorth").GetBoolean());
        Assert.Equal(scenario.RupiahAccount, duplicate.GetProperty("duplicateOfAccountId").GetGuid());
        Assert.True(duplicate.GetProperty("duplicateLinkExplicit").GetBoolean());
    }

    /// <summary>
    /// Rule: a conversion is derived and never overwrites the original. Asking the same space for its
    /// figures in a DIFFERENT currency has to answer in that currency and leave the stored rows alone -
    /// the reported originals and the balance rows are identical either way.
    /// </summary>
    [Fact]
    public async Task Asking_for_another_currency_changes_the_answer_not_the_stored_data()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory, withUnconvertibleAccount: false);
        using var client = factory.CreateClient();

        var inEuro = await CurrencyScenario.GetJsonAsync(
            client, scenario.Owner, $"/api/wealth/overview?fullWorthSpaceId={scenario.Space:D}&currency=EUR");
        var accounts = inEuro.GetProperty("accounts");

        Assert.Equal("EUR", inEuro.GetProperty("currency").GetString());
        // 100 EUR + 55 USD (= 50 EUR) + 3 000 000 IDR (= 150 EUR).
        Assert.Equal(300m, accounts.GetProperty("amount").GetDecimal());
        var originals = CurrencyScenario.OriginalAmounts(accounts);
        Assert.Equal(100m, originals["EUR"]);
        Assert.Equal(55m, originals["USD"]);
        Assert.Equal(3_000_000m, originals["IDR"]);

        await factory.SeedAsync(async db =>
        {
            var stored = await db.BalanceSnapshots.AsNoTracking()
                .Where(snapshot => db.Accounts.Any(account =>
                    account.Id == snapshot.AccountId && account.FullWorthSpaceId == scenario.Space))
                .Select(snapshot => new { snapshot.Amount, snapshot.Currency })
                .OrderBy(snapshot => snapshot.Currency)
                .ToListAsync();
            Assert.Equal(
                [("EUR", 100m), ("IDR", 3_000_000m), ("USD", 55m)],
                stored.Select(row => (row.Currency, row.Amount)).ToArray());
        });
    }

    /// <summary>
    /// Rule: a transaction is converted at ITS OWN value date, into the space's own base currency. The
    /// income/expense totals used to fall back to a hardcoded EUR when the caller passed no currency -
    /// the frontend never passes one - so a non-EUR space read its own figures as euros. And a booking is
    /// converted through a cross-rate here (USD → EUR → IDR); dropping the second leg understated every
    /// foreign expense by four orders of magnitude.
    /// </summary>
    [Fact]
    public async Task Income_and_expenses_are_converted_into_the_base_currency_at_the_bookings_own_date()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory, withUnconvertibleAccount: false, withTransactions: true);
        using var client = factory.CreateClient();

        var overview = await CurrencyScenario.GetJsonAsync(
            client,
            scenario.Owner,
            $"/api/analytics/overview?fullWorthSpaceId={scenario.Space:D}" +
            $"&from={TransactionDay:yyyy-MM-dd}&to={TransactionDay:yyyy-MM-dd}");

        Assert.Equal("IDR", overview.GetProperty("currency").GetString());
        // 200 EUR income = 4 000 000 IDR; 22 USD (= 20 EUR) expense = 400 000 IDR.
        Assert.Equal(4_000_000m, overview.GetProperty("income").GetDecimal());
        Assert.Equal(400_000m, overview.GetProperty("expenses").GetDecimal());
        Assert.Equal(3_600_000m, overview.GetProperty("net").GetDecimal());
        Assert.False(overview.GetProperty("incomplete").GetBoolean());
    }

    // Far enough back that today's fixing cannot stand in for that day's, so the booking really is
    // converted at its own date.
    private static readonly DateOnly TransactionDay = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-5);

    private sealed record Scenario(Guid Owner, Guid Space, Guid RupiahAccount, Guid? Duplicate);

    private static async Task<Scenario> SeedAsync(
        BackendWebApplicationFactory factory,
        bool withUnconvertibleAccount,
        bool withDuplicateAccount = false,
        bool withTransactions = false)
    {
        var owner = Guid.NewGuid();
        var space = Guid.NewGuid();
        var rupiah = Guid.NewGuid();
        Guid? duplicate = withDuplicateAccount ? Guid.NewGuid() : null;
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var captured = DateTimeOffset.UtcNow;

        await factory.SeedAsync(async db =>
        {
            CurrencyScenario.AddOwnerAndSpace(db, owner, space, "IDR", "Jakarta");

            var euro = CurrencyScenario.AddAccount(db, space, owner, "Girokonto", "EUR");
            CurrencyScenario.AddBalance(db, euro, 100m, "EUR", captured);

            var dollar = CurrencyScenario.AddAccount(db, space, owner, "Dollar Wallet", "USD");
            CurrencyScenario.AddBalance(db, dollar, 55m, "USD", captured);

            CurrencyScenario.AddAccount(db, space, owner, "Rekening Rupiah", "IDR", accountId: rupiah);
            CurrencyScenario.AddBalance(db, rupiah, 3_000_000m, "IDR", captured);

            if (withUnconvertibleAccount)
            {
                var franc = CurrencyScenario.AddAccount(db, space, owner, "Franken", "CHF");
                CurrencyScenario.AddBalance(db, franc, 80m, "CHF", captured);
            }

            if (duplicate.HasValue)
            {
                CurrencyScenario.AddAccount(
                    db, space, owner, "Rupiah zweite Verbindung", "IDR", accountId: duplicate.Value);
                CurrencyScenario.AddBalance(db, duplicate.Value, 500_000m, "IDR", captured);
            }

            if (withTransactions)
            {
                db.Transactions.Add(NewTransaction(euro, 200m, "EUR", "Gehalt"));
                db.Transactions.Add(NewTransaction(dollar, -22m, "USD", "Hosting"));
                CurrencyScenario.AddRate(db, TransactionDay, "USD", UsdPerEuro);
                CurrencyScenario.AddRate(db, TransactionDay, "IDR", IdrPerEuro);
            }

            CurrencyScenario.AddRate(db, today, "USD", UsdPerEuro);
            CurrencyScenario.AddRate(db, today, "IDR", IdrPerEuro);
            await db.SaveChangesAsync();
        });

        return new Scenario(owner, space, rupiah, duplicate);
    }

    private static FullWorth.Backend.Modules.Transactions.FinanceTransaction NewTransaction(
        Guid accountId, decimal amount, string currency, string counterparty) => new()
    {
        AccountId = accountId,
        ExternalKey = $"currency-test:{Guid.NewGuid():N}",
        Status = "BOOK",
        BookingDate = TransactionDay,
        ValueDate = TransactionDay,
        Amount = amount,
        Currency = currency,
        Counterparty = counterparty,
        NormalizedCounterparty = counterparty.ToLowerInvariant()
    };

    private static async Task<Dictionary<string, JsonElement>> AccountRowsAsync(
        HttpClient client, Scenario scenario)
    {
        var accounts = await CurrencyScenario.GetJsonAsync(
            client, scenario.Owner, $"/api/accounts?fullWorthSpaceId={scenario.Space:D}");
        return accounts.EnumerateArray()
            .ToDictionary(account => account.GetProperty("displayName").GetString()!, account => account);
    }
}
