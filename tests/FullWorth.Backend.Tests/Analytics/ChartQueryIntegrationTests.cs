using System.Net;
using System.Text.Json;
using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.BankConnections;
using FullWorth.Backend.Modules.Categories;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Fx;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Xunit;

namespace FullWorth.Backend.Tests.Analytics;

// Guided chart builder (§15.2): the bounded measure×dimension /api/analytics/chart endpoint reuses the
// same FX-aware, refund-netted aggregation as Overview. These lock the key measure/dimension combos.
public sealed class ChartQueryIntegrationTests
{
    private static readonly Guid Food = Guid.NewGuid();

    [Fact]
    public async Task SpendByMonth_SumsRefundNettedAllocationsPerMonth()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory, seed =>
        {
            AddTx(seed.Db, seed, -100m, new DateOnly(2026, 8, 5));
            AddTx(seed.Db, seed, -60m, new DateOnly(2026, 7, 5));
        });
        using var client = factory.CreateClient();
        var root = await GetAsync(client, s, "measure=spend&dimension=month&from=2026-07-01&to=2026-08-31");
        var byLabel = Series(root);
        Assert.Equal(60m, byLabel["2026-07"]);
        Assert.Equal(100m, byLabel["2026-08"]);
    }

    [Fact]
    public async Task SpendByCategory_LabelsByCategory()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory, seed =>
        {
            seed.Db.Categories.Add(new FinanceCategory { Id = Food, FullWorthSpaceId = seed.Space, Key = $"f-{Food:N}", Name = "Food" });
            AddTx(seed.Db, seed, -100m, new DateOnly(2026, 8, 5), Food);
        });
        using var client = factory.CreateClient();
        var root = await GetAsync(client, s, "measure=spend&dimension=category&from=2026-08-01&to=2026-08-31");
        Assert.Equal(100m, Series(root)["Food"]);
    }

    [Fact]
    public async Task IncomeByNone_ExcludesLinkedRefunds()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory, seed =>
        {
            var original = AddTx(seed.Db, seed, -50m, new DateOnly(2026, 8, 2));
            AddTx(seed.Db, seed, 200m, new DateOnly(2026, 8, 5));               // real income
            AddTx(seed.Db, seed, 30m, new DateOnly(2026, 8, 6), refundOf: original); // refund, not income
        });
        using var client = factory.CreateClient();
        var root = await GetAsync(client, s, "measure=income&dimension=none&from=2026-08-01&to=2026-08-31");
        Assert.Equal(200m, Series(root)["Total"]);
    }

    [Fact]
    public async Task NetByNone_EqualsIncomeMinusExpenses()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory, seed =>
        {
            AddTx(seed.Db, seed, 200m, new DateOnly(2026, 8, 5));
            AddTx(seed.Db, seed, -80m, new DateOnly(2026, 8, 6));
        });
        using var client = factory.CreateClient();
        var root = await GetAsync(client, s, "measure=net&dimension=none&from=2026-08-01&to=2026-08-31");
        Assert.Equal(120m, Series(root)["Total"]);
    }

    [Fact]
    public async Task CountByMerchant_IsTransactionLevel()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory, seed =>
        {
            AddTx(seed.Db, seed, -10m, new DateOnly(2026, 8, 5), counterparty: "Rewe");
            AddTx(seed.Db, seed, -20m, new DateOnly(2026, 8, 6), counterparty: "Rewe");
        });
        using var client = factory.CreateClient();
        var root = await GetAsync(client, s, "measure=count&dimension=merchant&from=2026-08-01&to=2026-08-31");
        Assert.Equal(2m, Series(root)["REWE"]);
    }

    [Fact]
    public async Task ForeignSpend_ConvertedAtValueDate_MissingRateMarksIncomplete()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory, seed =>
        {
            AddTx(seed.Db, seed, -110m, new DateOnly(2026, 8, 5), currency: "USD");
            AddTx(seed.Db, seed, -50m, new DateOnly(2026, 8, 6), currency: "CHF");   // no CHF rate → dropped
            seed.Db.FxRates.Add(new FxRate { Date = new DateOnly(2026, 8, 5), Currency = "USD", Rate = 1.10m });
        });
        using var client = factory.CreateClient();
        var root = await GetAsync(client, s, "measure=spend&dimension=none&from=2026-08-01&to=2026-08-31");
        Assert.Equal(100m, Series(root)["Total"]);              // 110 USD / 1.10
        Assert.True(root.GetProperty("incomplete").GetBoolean()); // CHF had no rate
    }

    [Fact]
    public async Task NetByCategory_OrdersByMagnitude_SoBigSpendSurfaces()
    {
        var spendCat = Guid.NewGuid();
        var incomeCat = Guid.NewGuid();
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory, seed =>
        {
            seed.Db.Categories.Add(new FinanceCategory { Id = spendCat, FullWorthSpaceId = seed.Space, Key = $"s-{spendCat:N}", Name = "Rent" });
            seed.Db.Categories.Add(new FinanceCategory { Id = incomeCat, FullWorthSpaceId = seed.Space, Key = $"i-{incomeCat:N}", Name = "Bonus" });
            AddTx(seed.Db, seed, -500m, new DateOnly(2026, 8, 5), spendCat);   // net -500
            AddTx(seed.Db, seed, 100m, new DateOnly(2026, 8, 6), incomeCat);    // net +100
        });
        using var client = factory.CreateClient();
        var root = await GetAsync(client, s, "measure=net&dimension=category&from=2026-08-01&to=2026-08-31");
        var series = root.GetProperty("series").EnumerateArray().ToList();
        // Ordered by |value| desc, the big spend category (-500) must come before the small income (+100).
        Assert.Equal("Rent", series[0].GetProperty("label").GetString());
        Assert.Equal(-500m, series[0].GetProperty("value").GetDecimal());
    }

    [Fact]
    public async Task InvalidMeasure_DefaultsToSpend()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory, seed => AddTx(seed.Db, seed, -40m, new DateOnly(2026, 8, 5)));
        using var client = factory.CreateClient();
        var root = await GetAsync(client, s, "measure=bogus&dimension=none&from=2026-08-01&to=2026-08-31");
        Assert.Equal("spend", root.GetProperty("measure").GetString());
        Assert.Equal(40m, Series(root)["Total"]);
    }

    [Fact]
    public async Task Outsider_Gets404()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory, _ => { });
        await factory.SeedFullWorthUserAsync(s.Outsider);
        using var client = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/analytics/chart?fullWorthSpaceId={s.Space}&measure=spend&dimension=none");
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", s.Outsider.ToString("D"));
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- helpers ----

    private sealed record Ctx(FullWorthDbContext Db, Guid Space, Guid Owner, Guid Account);
    private sealed record Scenario(Guid Space, Guid Owner, Guid Account, Guid Outsider);

    private static Dictionary<string, decimal> Series(JsonElement root) =>
        root.GetProperty("series").EnumerateArray().ToDictionary(p => p.GetProperty("label").GetString()!, p => p.GetProperty("value").GetDecimal());

    private static async Task<Scenario> SeedAsync(BackendWebApplicationFactory factory, Action<Ctx> extra)
    {
        var space = Guid.NewGuid();
        var owner = Guid.NewGuid();
        var connection = Guid.NewGuid();
        var account = Guid.NewGuid();
        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser { Id = owner, EmailNormalized = $"{owner:N}@EX.COM".ToUpperInvariant(), DisplayName = "C", IsActive = true });
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = space, Name = "Chart", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember { FullWorthSpaceId = space, UserId = owner, Role = FullWorthSpaceRoles.Owner });
            db.BankConnections.Add(new BankConnection { Id = connection, FullWorthSpaceId = space, Provider = "test", InstitutionName = "Bank", Country = "DE", ProviderSessionId = $"cq-{connection:N}", Status = "AUTHORIZED" });
            db.Accounts.Add(new FinanceAccount { Id = account, FullWorthSpaceId = space, BankConnectionId = connection, Provider = "test", IdentificationHash = $"cq-{account:N}", ProviderAccountId = $"cq-{account:N}", InstitutionName = "Bank", DisplayName = "Acc", Currency = "EUR" });
            db.AccountOwners.Add(new AccountOwner { AccountId = account, UserId = owner, OwnershipType = AccountOwnershipTypes.Owner });
            extra(new Ctx(db, space, owner, account));
            await db.SaveChangesAsync();
        });
        return new Scenario(space, owner, account, Guid.NewGuid());
    }

    private static Guid AddTx(FullWorthDbContext db, Ctx s, decimal amount, DateOnly date, Guid? categoryId = null, string? counterparty = null, string currency = "EUR", Guid? refundOf = null)
    {
        var id = Guid.NewGuid();
        db.Transactions.Add(new FinanceTransaction
        {
            Id = id,
            AccountId = s.Account,
            CategoryId = categoryId,
            ExternalKey = $"CQ-{id:N}",
            Amount = amount,
            Currency = currency,
            BookingDate = date,
            Counterparty = counterparty,
            NormalizedCounterparty = counterparty?.ToUpperInvariant(),
            RefundOfTransactionId = refundOf,
            RawJson = "{}"
        });
        return id;
    }

    private static async Task<JsonElement> GetAsync(HttpClient client, Scenario s, string query)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/analytics/chart?fullWorthSpaceId={s.Space}&{query}");
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", s.Owner.ToString("D"));
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }
}
