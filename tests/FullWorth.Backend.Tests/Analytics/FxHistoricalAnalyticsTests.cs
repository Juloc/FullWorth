using System.Net;
using System.Text.Json;
using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.BankConnections;
using FullWorth.Backend.Modules.Budgets;
using FullWorth.Backend.Modules.Categories;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Fx;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Xunit;

namespace FullWorth.Backend.Tests.Analytics;

// §18: transaction analytics convert every foreign amount to the base currency at the FX rate effective
// on its value date (historical, not today's), instead of dropping foreign rows. A missing rate marks
// the result incomplete and excludes that amount — never 1:1.
public sealed class FxHistoricalAnalyticsTests
{
    private static readonly Guid Category = Guid.NewGuid();

    [Fact]
    public async Task Overview_ConvertsForeignExpense_AndFlagsIncompleteOnMissingRate()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory, seed =>
        {
            AddTx(seed.Db, s: seed, amount: -110m, currency: "USD", date: new DateOnly(2026, 8, 5));
            AddTx(seed.Db, s: seed, amount: -50m, currency: "CHF", date: new DateOnly(2026, 8, 6));  // no CHF rate → dropped
            AddRate(seed.Db, "USD", new DateOnly(2026, 8, 5), 1.10m);
        });
        using var client = factory.CreateClient();

        var root = await GetAsync(client, s, "/api/analytics/overview?from=2026-08-01&to=2026-08-31");
        Assert.Equal(100m, root.GetProperty("expenses").GetDecimal());   // 110 USD / 1.10 = 100 EUR
        Assert.True(root.GetProperty("incomplete").GetBoolean());        // CHF had no rate
    }

    [Fact]
    public async Task Overview_UsesHistoricalRatePerValueDate()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory, seed =>
        {
            AddTx(seed.Db, seed, -110m, "USD", new DateOnly(2026, 8, 5));
            AddTx(seed.Db, seed, -110m, "USD", new DateOnly(2026, 8, 20));
            AddRate(seed.Db, "USD", new DateOnly(2026, 8, 5), 1.10m);   // → 100 EUR
            AddRate(seed.Db, "USD", new DateOnly(2026, 8, 20), 2.00m);  // → 55 EUR
        });
        using var client = factory.CreateClient();

        var root = await GetAsync(client, s, "/api/analytics/overview?from=2026-08-01&to=2026-08-31");
        // 100 + 55 = 155. Converting both at a single latest rate would give 110 — so this proves per-date.
        Assert.Equal(155m, root.GetProperty("expenses").GetDecimal());
        Assert.False(root.GetProperty("incomplete").GetBoolean());
    }

    [Fact]
    public async Task Overview_BaseCurrencyOnly_IsNeverIncomplete()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory, seed => AddTx(seed.Db, seed, -100m, "EUR", new DateOnly(2026, 8, 5)));
        using var client = factory.CreateClient();

        var root = await GetAsync(client, s, "/api/analytics/overview?from=2026-08-01&to=2026-08-31");
        Assert.Equal(100m, root.GetProperty("expenses").GetDecimal());
        Assert.False(root.GetProperty("incomplete").GetBoolean());   // base==base short-circuits, no rate needed
    }

    [Fact]
    public async Task Overview_RefundOfForeignExpense_ConvertsBothAtTheirOwnValueDate()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory, seed =>
        {
            // Expense -110 USD on Aug 5 @1.10 → 100 EUR. Refund +110 USD on Aug 20 @2.00 → 55 EUR.
            // Correct net expense = 100 - 55 = 45 EUR. Converting the refund at the ORIGINAL's Aug-5 rate
            // (the bug) would net 0 USD → 0 EUR; a single-rate conversion would give a different wrong number.
            var original = AddTx(seed.Db, seed, -110m, "USD", new DateOnly(2026, 8, 5));
            AddTx(seed.Db, seed, 110m, "USD", new DateOnly(2026, 8, 20), refundOf: original);
            AddRate(seed.Db, "USD", new DateOnly(2026, 8, 5), 1.10m);
            AddRate(seed.Db, "USD", new DateOnly(2026, 8, 20), 2.00m);
        });
        using var client = factory.CreateClient();

        var root = await GetAsync(client, s, "/api/analytics/overview?from=2026-08-01&to=2026-08-31");
        Assert.Equal(45m, root.GetProperty("expenses").GetDecimal());
        Assert.False(root.GetProperty("incomplete").GetBoolean());
    }

    [Fact]
    public async Task Category_ConvertsForeignExpenseToBase()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory, seed =>
        {
            seed.Db.Categories.Add(new FinanceCategory { Id = Category, FullWorthSpaceId = seed.Space, Key = $"c-{Category:N}", Name = "Groceries" });
            AddTx(seed.Db, seed, -110m, "USD", new DateOnly(2026, 8, 5), Category);
            AddRate(seed.Db, "USD", new DateOnly(2026, 8, 5), 1.10m);
        });
        using var client = factory.CreateClient();

        var root = await GetAsync(client, s, "/api/analytics/categories?year=2026&month=8");
        var groceries = root.GetProperty("categories").EnumerateArray().Single(c => c.GetProperty("name").GetString() == "Groceries");
        Assert.Equal(100m, groceries.GetProperty("current").GetDecimal());
        Assert.False(root.GetProperty("incomplete").GetBoolean());
    }

    [Fact]
    public async Task Merchant_ConvertsForeignSpendToBase()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory, seed =>
        {
            AddTx(seed.Db, seed, -110m, "USD", new DateOnly(2026, 8, 5), counterparty: "Rewe");
            AddRate(seed.Db, "USD", new DateOnly(2026, 8, 5), 1.10m);
        });
        using var client = factory.CreateClient();

        var root = await GetAsync(client, s, "/api/analytics/merchants?year=2026&month=8&top=10");
        var rewe = root.GetProperty("merchants").EnumerateArray().Single(m => m.GetProperty("merchant").GetString() == "REWE");
        Assert.Equal(100m, rewe.GetProperty("currentSpend").GetDecimal());
        Assert.False(root.GetProperty("incomplete").GetBoolean());
    }

    [Fact]
    public async Task BudgetStatus_ForeignSpendCountedInBudgetCurrency()
    {
        using var factory = new BackendWebApplicationFactory();
        var budgetId = Guid.NewGuid();
        var s = await SeedAsync(factory, seed =>
        {
            seed.Db.Set<Budget>().Add(new Budget { Id = budgetId, FullWorthSpaceId = seed.Space, Name = "Everything", Amount = 500m, Currency = "EUR", Period = "monthly", IsActive = true });
            AddTx(seed.Db, seed, -110m, "USD", new DateOnly(2026, 8, 5));
            AddRate(seed.Db, "USD", new DateOnly(2026, 8, 5), 1.10m);
        });
        using var client = factory.CreateClient();

        var root = await GetAsync(client, s, "/api/analytics/budget-status?year=2026&month=8");
        var budget = root.GetProperty("items").EnumerateArray().Single();
        Assert.Equal(100m, budget.GetProperty("spent").GetDecimal());   // 110 USD converted into the EUR budget
        Assert.False(root.GetProperty("incomplete").GetBoolean());
    }

    // ---- seeding helpers ----

    private sealed record Ctx(FullWorthDbContext Db, Guid Space, Guid Owner, Guid Account);
    private sealed record Scenario(Guid Space, Guid Owner, Guid Account);

    private static async Task<Scenario> SeedAsync(BackendWebApplicationFactory factory, Action<Ctx> extra)
    {
        var space = Guid.NewGuid();
        var owner = Guid.NewGuid();
        var connection = Guid.NewGuid();
        var account = Guid.NewGuid();
        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser { Id = owner, EmailNormalized = $"{owner:N}@EX.COM".ToUpperInvariant(), DisplayName = "FX", IsActive = true });
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = space, Name = "FX Analytics", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember { FullWorthSpaceId = space, UserId = owner, Role = FullWorthSpaceRoles.Owner });
            db.BankConnections.Add(new BankConnection { Id = connection, FullWorthSpaceId = space, Provider = "test", InstitutionName = "Bank", Country = "DE", ProviderSessionId = $"fxh-{connection:N}", Status = "AUTHORIZED" });
            db.Accounts.Add(new FinanceAccount { Id = account, FullWorthSpaceId = space, BankConnectionId = connection, Provider = "test", IdentificationHash = $"fxh-{account:N}", ProviderAccountId = $"prov-{account:N}", InstitutionName = "Bank", DisplayName = "Multi", Currency = "EUR" });
            db.AccountOwners.Add(new AccountOwner { AccountId = account, UserId = owner, OwnershipType = AccountOwnershipTypes.Owner });
            extra(new Ctx(db, space, owner, account));
            await db.SaveChangesAsync();
        });
        return new Scenario(space, owner, account);
    }

    private static Guid AddTx(FullWorthDbContext db, Ctx s, decimal amount, string currency, DateOnly date, Guid? categoryId = null, string? counterparty = null, Guid? refundOf = null)
    {
        var id = Guid.NewGuid();
        db.Transactions.Add(new FinanceTransaction
        {
            Id = id,
            AccountId = s.Account,
            CategoryId = categoryId,
            ExternalKey = $"FXH-{id:N}",
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

    private static void AddRate(FullWorthDbContext db, string currency, DateOnly date, decimal rate) =>
        db.FxRates.Add(new FxRate { Date = date, Currency = currency, Rate = rate });

    private static async Task<JsonElement> GetAsync(HttpClient client, Scenario s, string path)
    {
        var join = path.Contains('?') ? "&" : "?";
        var request = new HttpRequestMessage(HttpMethod.Get, $"{path}{join}fullWorthSpaceId={s.Space}");
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", s.Owner.ToString("D"));
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }
}
