using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Import;

/// <summary>
/// An import of a NON-EUR account, end to end through the real endpoints. IDR is the owner's own case:
/// an Indonesian account whose statement states millions of rupiah, imported into a space whose base
/// currency is the euro.
///
/// Four money rules meet here, and none of them was covered by a test:
/// <list type="bullet">
///   <item>The stored amount and its currency are the ones the file stated. A base-currency conversion
///         is derived and must never be written over the original.</item>
///   <item>The account shows its OWN balance afterwards, with no asset entity and no manual linking
///         step in between.</item>
///   <item>With no IDR rate, net worth says it is incomplete and names IDR. It never assumes 1:1 (which
///         would have added five million euros) and never assumes 0.</item>
///   <item>Imported numbers go through ImportNumber, not through a culture: "1234.56" in a foreign CSV
///         is one thousand two hundred, not one hundred and twenty-three thousand.</item>
/// </list>
/// </summary>
public sealed class NonEuroAccountImportIntegrationTests
{
    // A rupiah statement. 3 500 000 opening, one debit, one credit, 5 000 000 closing on 2026-09-05.
    private const string IdrStatement = """
        :20:STARTUMS
        :25:ID0212030000000020205/IDR
        :28C:00012/001
        :60F:C260901IDR3500000,00
        :61:2609020902D1500000,00NMSCNONREF//IDR-BREF-1
        :86:?00KARTENZAHLUNG?20Warung?32Toko Sederhana
        :61:2609050905C3000000,00NTRFGaji//IDR-BREF-2
        :86:?00GUTSCHRIFT?20Gaji September?32Pemberi Kerja
        :62F:C260905IDR5000000,00
        -
        """;

    private static readonly DateOnly StatementDate = new(2026, 9, 5);

    // 1 EUR = 20 000 IDR, so the 5 000 000 IDR closing balance is 250 EUR.
    private const decimal IdrPerEuro = 20_000m;
    private const decimal ClosingBalanceIdr = 5_000_000m;
    private const decimal ClosingBalanceInEuro = 250m;

    /// <summary>
    /// Rule: store and keep the ORIGINAL currency and amount. Without this the import wrote a
    /// base-currency figure (or the account's declared currency next to a foreign amount), and the
    /// rupiah amounts the file actually stated were gone for good - there is no way back from a
    /// converted number to the original.
    /// </summary>
    [Fact]
    public async Task Every_imported_amount_stays_in_rupiah()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var scenario = await SeedAsync(factory, withIdrRate: false);

        var result = await UploadAndCommitAsync(client, scenario, IdrStatement);

        Assert.Equal(2, result.GetProperty("imported").GetInt32());
        Assert.True(result.GetProperty("balanceApplied").GetBoolean());

        await factory.SeedAsync(async db =>
        {
            var transactions = await db.Transactions.AsNoTracking()
                .Where(transaction => transaction.AccountId == scenario.Account)
                .OrderBy(transaction => transaction.Amount)
                .ToListAsync();
            Assert.Equal([-1_500_000m, 3_000_000m], transactions.Select(transaction => transaction.Amount).ToArray());
            Assert.All(transactions, transaction => Assert.Equal("IDR", transaction.Currency));

            var balance = await db.BalanceSnapshots.AsNoTracking()
                .SingleAsync(snapshot => snapshot.AccountId == scenario.Account);
            Assert.Equal(ClosingBalanceIdr, balance.Amount);
            Assert.Equal("IDR", balance.Currency);
            Assert.Equal(BalanceSources.Import, balance.Source);
            // A historical value is stored AS OF its date, not as of the day it was imported.
            Assert.Equal(StatementDate, balance.ReferenceDate);
        });
    }

    /// <summary>
    /// Rule: an account's own balance must reach the user without a link to a separate asset entity, and
    /// correct display must never depend on a manual post-import step. The import account is created
    /// archived and out of the totals because a booking-only export carries no balance; the moment a
    /// statement states one, the account is a real account again - previously the only route to a value
    /// was linking it to a different, live account by hand, and the next sync undid that.
    /// </summary>
    [Fact]
    public async Task The_imported_account_shows_its_own_rupiah_balance_with_no_asset_and_no_linking_step()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var scenario = await SeedAsync(factory, withIdrRate: false);

        await UploadAndCommitAsync(client, scenario, IdrStatement);

        var accounts = await CurrencyScenario.GetJsonAsync(
            client, scenario.Owner, $"/api/accounts?fullWorthSpaceId={scenario.Space:D}");
        var account = Assert.Single(accounts.EnumerateArray().ToArray());

        Assert.True(account.GetProperty("isActive").GetBoolean());
        Assert.True(account.GetProperty("includeInNetWorth").GetBoolean());
        Assert.Equal("IDR", account.GetProperty("latestBalance").GetProperty("currency").GetString());
        Assert.Equal(ClosingBalanceIdr, account.GetProperty("latestBalance").GetProperty("amount").GetDecimal());
        // Nothing was linked and nothing had to be: no asset entity exists at all.
        Assert.Equal(JsonValueKind.Null, account.GetProperty("duplicateOfAccountId").ValueKind);
        await factory.SeedAsync(async db =>
            Assert.Equal(0, await db.Assets.AsNoTracking()
                .CountAsync(asset => asset.FullWorthSpaceId == scenario.Space)));

        // No IDR rate, so there is no converted secondary figure - and the row still shows the rupiah.
        // A conversion that cannot be made must leave the original alone, not blank it out.
        Assert.Equal(JsonValueKind.Null, account.GetProperty("baseValue").ValueKind);
    }

    /// <summary>
    /// Rule: a missing FX rate marks the result incomplete - never 1:1, never 0. Assuming 1:1 would have
    /// put five million euros on the net worth of an account holding 250 EUR worth of rupiah; silently
    /// dropping the account printed a confident total that was missing real money.
    /// </summary>
    [Fact]
    public async Task Without_a_rupiah_rate_net_worth_says_it_is_incomplete_instead_of_guessing()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var scenario = await SeedAsync(factory, withIdrRate: false);

        await UploadAndCommitAsync(client, scenario, IdrStatement);

        var overview = await CurrencyScenario.GetJsonAsync(
            client, scenario.Owner, $"/api/wealth/overview?fullWorthSpaceId={scenario.Space:D}");
        var accounts = overview.GetProperty("accounts");

        Assert.False(overview.GetProperty("isComplete").GetBoolean());
        Assert.Contains("IDR", overview.GetProperty("missingCurrencies")
            .EnumerateArray().Select(item => item.GetString()));
        // Per component, so the screen can say WHICH figure the missing rate made incomplete.
        Assert.Equal(["IDR"], CurrencyScenario.MissingCurrencies(accounts));
        Assert.False(accounts.GetProperty("isComplete").GetBoolean());
        // Neither the rupiah amount taken as euros (1:1) nor a converted guess.
        Assert.Equal(0m, accounts.GetProperty("amount").GetDecimal());
        Assert.NotEqual(ClosingBalanceIdr, overview.GetProperty("netWorth").GetDecimal());
        // The original survives the failed conversion untouched.
        Assert.Equal(ClosingBalanceIdr, CurrencyScenario.OriginalAmounts(accounts)["IDR"]);

        // The home screen must not disagree with the wealth page about the same data.
        var dashboard = await CurrencyScenario.GetJsonAsync(
            client, scenario.Owner, $"/api/analytics/dashboard?fullWorthSpaceId={scenario.Space:D}");
        Assert.True(dashboard.GetProperty("incomplete").GetBoolean());
        Assert.Equal(0m, dashboard.GetProperty("accounts").GetDecimal());
    }

    /// <summary>
    /// Rule: a base-currency conversion is a DERIVED value. Once the rate exists the euro figure appears
    /// beside the rupiah amount; the stored balance, the stored transactions and the reported original
    /// are still rupiah.
    /// </summary>
    [Fact]
    public async Task With_a_rate_the_euro_figure_is_added_beside_the_rupiah_never_instead_of_it()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var scenario = await SeedAsync(factory, withIdrRate: true);

        await UploadAndCommitAsync(client, scenario, IdrStatement);

        var accounts = await CurrencyScenario.GetJsonAsync(
            client, scenario.Owner, $"/api/accounts?fullWorthSpaceId={scenario.Space:D}");
        var account = Assert.Single(accounts.EnumerateArray().ToArray());
        Assert.Equal("IDR", account.GetProperty("latestBalance").GetProperty("currency").GetString());
        Assert.Equal(ClosingBalanceIdr, account.GetProperty("latestBalance").GetProperty("amount").GetDecimal());
        Assert.Equal(ClosingBalanceInEuro, account.GetProperty("baseValue").GetDecimal());
        Assert.Equal("EUR", account.GetProperty("baseCurrency").GetString());

        var overview = await CurrencyScenario.GetJsonAsync(
            client, scenario.Owner, $"/api/wealth/overview?fullWorthSpaceId={scenario.Space:D}");
        var component = overview.GetProperty("accounts");
        Assert.True(overview.GetProperty("isComplete").GetBoolean());
        Assert.Equal(ClosingBalanceInEuro, component.GetProperty("amount").GetDecimal());
        Assert.Equal("EUR", component.GetProperty("currency").GetString());
        Assert.Equal(ClosingBalanceIdr, CurrencyScenario.OriginalAmounts(component)["IDR"]);
        Assert.Empty(CurrencyScenario.MissingCurrencies(component));

        // The stored rows are untouched by the conversion that was shown.
        await factory.SeedAsync(async db =>
        {
            var balance = await db.BalanceSnapshots.AsNoTracking()
                .SingleAsync(snapshot => snapshot.AccountId == scenario.Account);
            Assert.Equal(ClosingBalanceIdr, balance.Amount);
            Assert.Equal("IDR", balance.Currency);
            Assert.All(
                await db.Transactions.AsNoTracking()
                    .Where(transaction => transaction.AccountId == scenario.Account).ToListAsync(),
                transaction => Assert.Equal("IDR", transaction.Currency));
        });
    }

    /// <summary>
    /// Rule: imported numbers are parsed with ImportNumber, never with a culture. A German culture with
    /// AllowThousands accepts "1234.56" as 123 456 because .NET does not validate group sizes, so every
    /// dot-decimal amount in a foreign export was committed a hundred times too large - and an .xlsx is
    /// guaranteed to be dot-decimal because OOXML always stores cell values invariant.
    /// </summary>
    [Fact]
    public async Task A_dot_decimal_amount_in_a_foreign_export_is_not_multiplied_by_a_hundred()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var scenario = await SeedAsync(factory, withIdrRate: false);
        const string csv = """
            Date,Amount,Currency,Counterparty,Description
            2026-09-02,1234.56,IDR,Toko Sederhana,Belanja
            2026-09-03,-0.99,IDR,Warung Kopi,Kopi
            """;

        var result = await UploadAndCommitAsync(client, scenario, csv, "mutasi.csv", expectedAdapter: "generic_csv");

        Assert.Equal(2, result.GetProperty("imported").GetInt32());
        await factory.SeedAsync(async db =>
        {
            var amounts = await db.Transactions.AsNoTracking()
                .Where(transaction => transaction.AccountId == scenario.Account)
                .OrderBy(transaction => transaction.Amount)
                .Select(transaction => transaction.Amount)
                .ToListAsync();
            Assert.Equal([-0.99m, 1234.56m], amounts);
            Assert.DoesNotContain(123_456m, amounts);
        });
    }

    private sealed record Scenario(Guid Owner, Guid Space, Guid Account);

    private static async Task<Scenario> SeedAsync(BackendWebApplicationFactory factory, bool withIdrRate)
    {
        var owner = Guid.NewGuid();
        var space = Guid.NewGuid();
        var account = Guid.NewGuid();

        await factory.SeedAsync(async db =>
        {
            CurrencyScenario.AddOwnerAndSpace(db, owner, space, "EUR", "Rupiah import");
            // The shape a booking-only import leaves behind: archived, out of the totals, no balance.
            CurrencyScenario.AddAccount(
                db, space, owner, "Rekening Rupiah", "IDR",
                includeInNetWorth: false, isActive: false, accountId: account,
                institutionName: "Bank Indonesia Test");
            if (withIdrRate)
                CurrencyScenario.AddRate(db, DateOnly.FromDateTime(DateTime.UtcNow), "IDR", IdrPerEuro);
            await db.SaveChangesAsync();
        });

        return new Scenario(owner, space, account);
    }

    private static async Task<JsonElement> UploadAndCommitAsync(
        HttpClient client,
        Scenario scenario,
        string payload,
        string fileName = "mutasi.sta",
        string expectedAdapter = "mt940")
    {
        using var upload = CurrencyScenario.UserRequest(
            HttpMethod.Post,
            $"/api/import-jobs/upload?fullWorthSpaceId={scenario.Space:D}",
            scenario.Owner);
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(payload));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        content.Add(file, "file", fileName);
        upload.Content = content;
        using var uploaded = await client.SendAsync(upload);
        Assert.Equal(HttpStatusCode.OK, uploaded.StatusCode);
        using var uploadedJson = JsonDocument.Parse(await uploaded.Content.ReadAsStringAsync());
        Assert.Equal(expectedAdapter, uploadedJson.RootElement.GetProperty("adapter").GetString());
        var jobId = uploadedJson.RootElement.GetProperty("jobId").GetGuid();

        using var commit = CurrencyScenario.UserRequest(
            HttpMethod.Post,
            $"/api/import-jobs/{jobId:D}/commit?fullWorthSpaceId={scenario.Space:D}",
            scenario.Owner);
        commit.Content = JsonContent.Create(new { accountId = scenario.Account, candidateIds = (Guid[]?)null });
        using var response = await client.SendAsync(commit);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }
}
