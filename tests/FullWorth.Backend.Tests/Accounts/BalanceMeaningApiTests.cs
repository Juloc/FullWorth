using System.Globalization;
using System.Net;
using System.Text.Json;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.BankConnections;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;

namespace FullWorth.Backend.Tests.Accounts;

/// <summary>
/// A balance row carried an amount and a date and never said WHAT the amount is. "Available" and
/// "booked" differ by exactly the pending authorisations the reader is trying to account for, so the
/// figure could not be checked against anything — and a real bank sends several types in ONE response
/// stamped with ONE CapturedAt, which is also how the displayed figure used to flip between them.
///
/// These tests go through the HTTP surface on purpose: the pick must survive the query, and the
/// accounts list is where the hand-written copy of the preference used to live.
/// </summary>
public sealed class BalanceMeaningApiTests
{
    private sealed record Seeded(Guid Owner, Guid Space, Guid Account);

    // What a bank that reports everything looks like: one capture, four types, one currency.
    private static readonly (string Currency, string Type, decimal Amount)[] EveryProviderType =
    [
        ("EUR", "expected", 1_200m),
        ("EUR", "interimBooked", 1_050m),
        ("EUR", "closingBooked", 1_000m),
        ("EUR", "interimAvailable", 970m)
    ];

    [Fact]
    public async Task Several_types_at_one_capture_produce_the_available_figure_and_say_so()
    {
        using var factory = new BackendWebApplicationFactory();
        var seeded = await SeedAsync(factory, EveryProviderType);
        using var client = factory.CreateClient();

        var balance = (await RowAsync(client, seeded)).GetProperty("latestBalance");

        Assert.Equal("interimAvailable", balance.GetProperty("balanceType").GetString());
        Assert.Equal(BalanceMeanings.Available, balance.GetProperty("meaning").GetString());
        Assert.Equal(970m, balance.GetProperty("amount").GetDecimal());
    }

    // The case that matters most: the same data must never describe itself differently twice. This is
    // what a hand-written ordering per surface could not guarantee.
    [Fact]
    public async Task The_same_account_never_flips_type_or_wallet_between_requests()
    {
        using var factory = new BackendWebApplicationFactory();
        var seeded = await SeedAsync(factory,
        [
            ("EUR", "interimAvailable", 970m),
            ("EUR", "closingBooked", 1_000m),
            ("USD", "closingBooked", 300m),
            ("USD", "interimAvailable", 290m)
        ]);
        using var client = factory.CreateClient();

        var seen = new List<string>();
        for (var attempt = 0; attempt < 5; attempt++)
            seen.Add(Describe((await RowAsync(client, seeded)).GetProperty("latestBalance")));

        Assert.Equal(["EUR/interimAvailable/970"], seen.Distinct().ToArray());
    }

    // A second sync writes the same set of types again with a newer capture. The row has to move to the
    // new figure and keep describing itself the same way; it must not switch to a different type just
    // because the rows landed in the table in a different order.
    [Fact]
    public async Task A_second_sync_keeps_the_type_and_moves_to_the_new_figure()
    {
        using var factory = new BackendWebApplicationFactory();
        var seeded = await SeedAsync(factory, EveryProviderType);
        using var client = factory.CreateClient();

        var first = (await RowAsync(client, seeded)).GetProperty("latestBalance");
        Assert.Equal("interimAvailable", first.GetProperty("balanceType").GetString());

        // Inserted in the reverse order, an hour later - the second sync of the same account.
        await AddCaptureAsync(factory, seeded,
        [
            ("EUR", "interimAvailable", 880m),
            ("EUR", "closingBooked", 900m),
            ("EUR", "interimBooked", 950m),
            ("EUR", "expected", 1_100m)
        ], hoursLater: 1);

        var second = (await RowAsync(client, seeded)).GetProperty("latestBalance");
        Assert.Equal("interimAvailable", second.GetProperty("balanceType").GetString());
        Assert.Equal(BalanceMeanings.Available, second.GetProperty("meaning").GetString());
        Assert.Equal(880m, second.GetProperty("amount").GetDecimal());
    }

    // Every wallet answers for itself: one currency can be available while another is only booked, and
    // each has to say which. Reducing the account to one row hid that entirely.
    [Fact]
    public async Task Each_wallet_reports_its_own_type_and_meaning()
    {
        using var factory = new BackendWebApplicationFactory();
        var seeded = await SeedAsync(factory,
        [
            ("EUR", "interimAvailable", 970m),
            ("USD", "closingBooked", 300m),
            ("GBP", "expected", 40m)
        ]);
        using var client = factory.CreateClient();

        var wallets = (await RowAsync(client, seeded)).GetProperty("balances").EnumerateArray()
            .ToDictionary(
                wallet => wallet.GetProperty("currency").GetString()!,
                wallet => (wallet.GetProperty("balanceType").GetString(), wallet.GetProperty("meaning").GetString()));

        Assert.Equal(3, wallets.Count);
        Assert.Equal(("interimAvailable", BalanceMeanings.Available), wallets["EUR"]);
        Assert.Equal(("closingBooked", BalanceMeanings.Booked), wallets["USD"]);
        Assert.Equal(("expected", BalanceMeanings.Expected), wallets["GBP"]);
    }

    // A type nobody had ever tested must still reach the user rather than leaving the row empty.
    [Theory]
    [InlineData("interimBooked", BalanceMeanings.Booked)]
    [InlineData("interimAvailable", BalanceMeanings.Available)]
    [InlineData("closingAvailable", BalanceMeanings.Available)]
    [InlineData("expected", BalanceMeanings.Expected)]
    public async Task A_lone_balance_of_any_type_reaches_the_row(string balanceType, string meaning)
    {
        using var factory = new BackendWebApplicationFactory();
        var seeded = await SeedAsync(factory, [("EUR", balanceType, 77m)]);
        using var client = factory.CreateClient();

        var balance = (await RowAsync(client, seeded)).GetProperty("latestBalance");

        Assert.Equal(balanceType, balance.GetProperty("balanceType").GetString());
        Assert.Equal(meaning, balance.GetProperty("meaning").GetString());
        Assert.Equal(77m, balance.GetProperty("amount").GetDecimal());
    }

    // A figure the owner typed and a figure read off a statement have no booked/available claim to make.
    // The row says where it came from instead; inventing "booked" for a hand-entered number would be
    // asserting something nobody checked.
    [Theory]
    [InlineData("manual", BalanceSources.Manual, BalanceMeanings.Recorded)]
    [InlineData("manualCurrent", BalanceSources.Manual, BalanceMeanings.Recorded)]
    [InlineData("closingBooked", BalanceSources.Import, BalanceMeanings.Booked)]
    public async Task A_recorded_balance_keeps_its_source_and_claims_only_what_it_knows(
        string balanceType, string source, string meaning)
    {
        using var factory = new BackendWebApplicationFactory();
        var seeded = await SeedAsync(factory, [("EUR", balanceType, 240m)], source: source);
        using var client = factory.CreateClient();

        var balance = (await RowAsync(client, seeded)).GetProperty("latestBalance");

        Assert.Equal(source, balance.GetProperty("source").GetString());
        Assert.Equal(meaning, balance.GetProperty("meaning").GetString());
    }

    // A manual anchor and a provider balance at the same capture: the known provider type wins, because
    // an unknown type must never beat one FullWorth understands.
    [Fact]
    public async Task A_provider_type_beats_a_manual_one_at_the_same_capture()
    {
        using var factory = new BackendWebApplicationFactory();
        var seeded = await SeedAsync(factory, [("EUR", "manual", 500m), ("EUR", "interimBooked", 512m)]);
        using var client = factory.CreateClient();

        var balance = (await RowAsync(client, seeded)).GetProperty("latestBalance");

        Assert.Equal("interimBooked", balance.GetProperty("balanceType").GetString());
        Assert.Equal(512m, balance.GetProperty("amount").GetDecimal());
    }

    // The accounts list must not carry its own ordering. It used to hold a copy of the preference
    // inlined into the EF projection, which is exactly how two screens could disagree; this asserts the
    // row equals what the one rule picks from the raw snapshot rows.
    [Fact]
    public async Task The_row_is_exactly_what_the_one_rule_picks_from_the_raw_rows()
    {
        using var factory = new BackendWebApplicationFactory();
        var seeded = await SeedAsync(factory,
        [
            ("EUR", "expected", 1_200m),
            ("EUR", "closingAvailable", 990m),
            ("EUR", "interimAvailable", 970m),
            ("USD", "interimBooked", 310m),
            ("USD", "closingBooked", 300m)
        ]);
        using var client = factory.CreateClient();

        var wallets = (await RowAsync(client, seeded)).GetProperty("balances").EnumerateArray()
            .Select(Describe)
            .OrderBy(text => text, StringComparer.Ordinal)
            .ToArray();

        var expected = Array.Empty<string>();
        await factory.SeedAsync(async db =>
        {
            var rows = await CurrentBalances.LoadAsync(db, [seeded.Account], default);
            expected = rows
                .Select(balance => Describe(balance.Currency, balance.BalanceType, balance.Amount))
                .OrderBy(text => text, StringComparer.Ordinal)
                .ToArray();
        });

        Assert.Equal(expected, wallets);
    }

    // numeric(20,8) round-trips as 970.00000000, so compare the figure by value, not by its text.
    private static string Describe(JsonElement balance) => Describe(
        balance.GetProperty("currency").GetString()!,
        balance.GetProperty("balanceType").GetString()!,
        balance.GetProperty("amount").GetDecimal());

    private static string Describe(string currency, string balanceType, decimal amount) =>
        string.Create(CultureInfo.InvariantCulture, $"{currency}/{balanceType}/{amount:0.##}");

    private static async Task<Seeded> SeedAsync(
        BackendWebApplicationFactory factory,
        (string Currency, string Type, decimal Amount)[] balances,
        string source = BalanceSources.Provider)
    {
        var owner = Guid.NewGuid();
        var space = Guid.NewGuid();
        var connection = Guid.NewGuid();
        var account = Guid.NewGuid();
        // ONE capture for every row: that is what a sync writes, and it is the whole reason a tiebreak
        // has to exist.
        var captured = DateTimeOffset.UtcNow;

        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = owner,
                EmailNormalized = $"{owner:N}@EX.COM".ToUpperInvariant(),
                DisplayName = "Balance owner",
                IsActive = true
            });
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = space, Name = "Balances", BaseCurrency = "EUR" });
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
                InstitutionName = "Testbank",
                Country = "DE",
                ProviderSessionId = $"balance-session-{account:N}"
            });
            db.Accounts.Add(new FinanceAccount
            {
                Id = account,
                FullWorthSpaceId = space,
                BankConnectionId = connection,
                Provider = "test",
                IdentificationHash = $"balance-{account:N}",
                ProviderAccountId = $"balance-{account:N}",
                InstitutionName = "Testbank",
                DisplayName = "Girokonto",
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
            foreach (var (currency, type, amount) in balances)
                db.BalanceSnapshots.Add(new BalanceSnapshot
                {
                    AccountId = account,
                    Amount = amount,
                    Currency = currency,
                    BalanceType = type,
                    Source = source,
                    CapturedAt = captured
                });
            await db.SaveChangesAsync();
        });

        return new Seeded(owner, space, account);
    }

    private static async Task AddCaptureAsync(
        BackendWebApplicationFactory factory,
        Seeded seeded,
        (string Currency, string Type, decimal Amount)[] balances,
        int hoursLater)
    {
        var captured = DateTimeOffset.UtcNow.AddHours(hoursLater);
        await factory.SeedAsync(async db =>
        {
            foreach (var (currency, type, amount) in balances)
                db.BalanceSnapshots.Add(new BalanceSnapshot
                {
                    AccountId = seeded.Account,
                    Amount = amount,
                    Currency = currency,
                    BalanceType = type,
                    Source = BalanceSources.Provider,
                    CapturedAt = captured
                });
            await db.SaveChangesAsync();
        });
    }

    private static async Task<JsonElement> RowAsync(HttpClient client, Seeded seeded)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"/api/accounts?fullWorthSpaceId={seeded.Space}");
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", seeded.Owner.ToString("D"));
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.EnumerateArray().Single().Clone();
    }
}
