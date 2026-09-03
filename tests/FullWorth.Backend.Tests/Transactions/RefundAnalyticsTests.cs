using System.Net;
using System.Net.Http.Json;
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

namespace FullWorth.Backend.Tests.Transactions;

// End-to-end proof that a linked refund (UI_UX_SPEC §9.6) reduces the original expense's category and
// is not counted as income. Postgres (analytics allocation query uses SQL APPLY).
public sealed class RefundAnalyticsTests
{
    [Fact]
    public async Task LinkedRefundReducesCategoryAndIsNotIncome()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SetupAsync(factory, (db, s) =>
        {
            db.Transactions.Add(Tx(s, s.Expense, -50m, new DateOnly(2026, 6, 15), s.CatA));
            db.Transactions.Add(Tx(s, s.Refund, 20m, new DateOnly(2026, 6, 20)));
        });

        using var client = factory.CreateClient();
        using var link = await client.SendAsync(Request(HttpMethod.Patch, $"/api/transactions/{s.Refund}/refund?fullWorthSpaceId={s.Space}", s.Owner, new { originalTransactionId = s.Expense }));
        Assert.Equal(HttpStatusCode.NoContent, link.StatusCode);

        var body = await OverviewAsync(client, s);
        Assert.Equal(0m, body.GetProperty("income").GetDecimal());        // refund is not income
        Assert.Equal(30m, body.GetProperty("expenses").GetDecimal());     // 50 expense reduced by 20 refund
        Assert.Equal(30m, CategoryAmount(body, s.CatA));

        // byMonth must reconcile with the headline: the refund nets against June's expense there too.
        var months = body.GetProperty("byMonth").EnumerateArray().ToList();
        Assert.Equal(30m, months.Sum(m => m.GetProperty("expenses").GetDecimal()));
        Assert.Equal(-30m, months.Sum(m => m.GetProperty("net").GetDecimal()));
    }

    // §9.6: a refund targeting ONE split-line category reduces only that category — the sibling line is
    // untouched.
    [Fact]
    public async Task TargetedRefundReducesOnlyNamedCategory()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SetupAsync(factory, (db, s) =>
        {
            db.Transactions.Add(Tx(s, s.Expense, -100m, new DateOnly(2026, 6, 15), s.CatA));
            db.TransactionAllocations.Add(new TransactionAllocation { TransactionId = s.Expense, CategoryId = s.CatA, Amount = -60m });
            db.TransactionAllocations.Add(new TransactionAllocation { TransactionId = s.Expense, CategoryId = s.CatB, Amount = -40m });
            db.Transactions.Add(Tx(s, s.Refund, 40m, new DateOnly(2026, 6, 20)));
        });

        using var client = factory.CreateClient();
        using var link = await client.SendAsync(Request(HttpMethod.Patch, $"/api/transactions/{s.Refund}/refund?fullWorthSpaceId={s.Space}", s.Owner, new { originalTransactionId = s.Expense, refundCategoryId = s.CatB }));
        Assert.Equal(HttpStatusCode.NoContent, link.StatusCode);

        var body = await OverviewAsync(client, s);
        Assert.Equal(0m, body.GetProperty("income").GetDecimal());
        Assert.Equal(60m, body.GetProperty("expenses").GetDecimal());   // only B's 40 came back
        Assert.Equal(60m, CategoryAmount(body, s.CatA));                 // A untouched
        Assert.Equal(0m, CategoryAmount(body, s.CatB));                  // B fully refunded
    }

    // Regression guard: an UNtargeted refund of a split expense still nets proportionally across lines.
    [Fact]
    public async Task UntargetedRefundStaysProportional()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SetupAsync(factory, (db, s) =>
        {
            db.Transactions.Add(Tx(s, s.Expense, -100m, new DateOnly(2026, 6, 15), s.CatA));
            db.TransactionAllocations.Add(new TransactionAllocation { TransactionId = s.Expense, CategoryId = s.CatA, Amount = -60m });
            db.TransactionAllocations.Add(new TransactionAllocation { TransactionId = s.Expense, CategoryId = s.CatB, Amount = -40m });
            db.Transactions.Add(Tx(s, s.Refund, 50m, new DateOnly(2026, 6, 20)));
        });

        using var client = factory.CreateClient();
        using var link = await client.SendAsync(Request(HttpMethod.Patch, $"/api/transactions/{s.Refund}/refund?fullWorthSpaceId={s.Space}", s.Owner, new { originalTransactionId = s.Expense }));
        Assert.Equal(HttpStatusCode.NoContent, link.StatusCode);

        var body = await OverviewAsync(client, s);
        Assert.Equal(50m, body.GetProperty("expenses").GetDecimal());   // 100 - 50 refund
        Assert.Equal(30m, CategoryAmount(body, s.CatA));                 // 60 - 60/100*50
        Assert.Equal(20m, CategoryAmount(body, s.CatB));                 // 40 - 40/100*50
    }

    // A targeted refund converts to base at ITS OWN value date, then reduces only the named category.
    // Original spent on 2026-06-15 @1.25; refunded on 2026-06-20 @2.00.
    [Fact]
    public async Task TargetedRefundFxCorrectAtOwnValueDate()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SetupAsync(factory, (db, s) =>
        {
            db.FxRates.Add(new FxRate { Date = new DateOnly(2026, 6, 15), Currency = "USD", Rate = 1.25m });
            db.FxRates.Add(new FxRate { Date = new DateOnly(2026, 6, 20), Currency = "USD", Rate = 2.00m });
            db.Transactions.Add(Tx(s, s.Expense, -100m, new DateOnly(2026, 6, 15), s.CatA, currency: "USD"));   // 100 USD /1.25 = 80 EUR
            db.TransactionAllocations.Add(new TransactionAllocation { TransactionId = s.Expense, CategoryId = s.CatA, Amount = -60m });
            db.TransactionAllocations.Add(new TransactionAllocation { TransactionId = s.Expense, CategoryId = s.CatB, Amount = -40m });
            db.Transactions.Add(Tx(s, s.Refund, 20m, new DateOnly(2026, 6, 20), currency: "USD"));             // 20 USD /2.00 = 10 EUR
        });

        using var client = factory.CreateClient();
        using var link = await client.SendAsync(Request(HttpMethod.Patch, $"/api/transactions/{s.Refund}/refund?fullWorthSpaceId={s.Space}", s.Owner, new { originalTransactionId = s.Expense, refundCategoryId = s.CatB }));
        Assert.Equal(HttpStatusCode.NoContent, link.StatusCode);

        var body = await OverviewAsync(client, s);
        // A = 60 USD /1.25 = 48 EUR (untouched). B = 40 USD /1.25 = 32 EUR, minus the refund 20 USD /2.00 = 10 EUR -> 22 EUR.
        Assert.Equal(48m, CategoryAmount(body, s.CatA));
        Assert.Equal(22m, CategoryAmount(body, s.CatB));
        Assert.Equal(70m, body.GetProperty("expenses").GetDecimal());
    }

    // A FULL targeted refund (category driven to 0) plus an untargeted goodwill credit must still net:
    // the goodwill 10 cannot vanish just because the post-targeted line total reached 0.
    [Fact]
    public async Task FullTargetedRefundPlusUntargetedGoodwillStillNets()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SetupAsync(factory, (db, s) =>
        {
            db.Transactions.Add(Tx(s, s.Expense, -100m, new DateOnly(2026, 6, 15), s.CatA));   // single category
            db.Transactions.Add(Tx(s, s.Refund, 100m, new DateOnly(2026, 6, 20)));             // full targeted refund
            db.Transactions.Add(Tx(s, s.Refund2, 10m, new DateOnly(2026, 6, 21)));             // untargeted goodwill
        });

        using var client = factory.CreateClient();
        using var link1 = await client.SendAsync(Request(HttpMethod.Patch, $"/api/transactions/{s.Refund}/refund?fullWorthSpaceId={s.Space}", s.Owner, new { originalTransactionId = s.Expense, refundCategoryId = s.CatA }));
        Assert.Equal(HttpStatusCode.NoContent, link1.StatusCode);
        using var link2 = await client.SendAsync(Request(HttpMethod.Patch, $"/api/transactions/{s.Refund2}/refund?fullWorthSpaceId={s.Space}", s.Owner, new { originalTransactionId = s.Expense }));
        Assert.Equal(HttpStatusCode.NoContent, link2.StatusCode);

        var body = await OverviewAsync(client, s);
        Assert.Equal(0m, body.GetProperty("income").GetDecimal());
        // 100 spend - 100 targeted - 10 goodwill = -10; the goodwill must not vanish.
        Assert.Equal(-10m, body.GetProperty("expenses").GetDecimal());
        Assert.Equal(-10m, CategoryAmount(body, s.CatA));
    }

    // Safety-net: if the targeted category is edited out of the split after linking, the refund still
    // nets (proportionally) rather than vanishing — the builder's fallback.
    [Fact]
    public async Task TargetedRefundFallsBackToProportionalWhenCategoryRemoved()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SetupAsync(factory, (db, s) =>
        {
            db.Transactions.Add(Tx(s, s.Expense, -100m, new DateOnly(2026, 6, 15), s.CatA));
            db.TransactionAllocations.Add(new TransactionAllocation { TransactionId = s.Expense, CategoryId = s.CatA, Amount = -60m });
            db.TransactionAllocations.Add(new TransactionAllocation { TransactionId = s.Expense, CategoryId = s.CatB, Amount = -40m });
            db.Transactions.Add(Tx(s, s.Refund, 30m, new DateOnly(2026, 6, 20)));
        });

        using var client = factory.CreateClient();
        using var link = await client.SendAsync(Request(HttpMethod.Patch, $"/api/transactions/{s.Refund}/refund?fullWorthSpaceId={s.Space}", s.Owner, new { originalTransactionId = s.Expense, refundCategoryId = s.CatB }));
        Assert.Equal(HttpStatusCode.NoContent, link.StatusCode);

        // Re-split the original so category B no longer exists on it (single line, all CatA).
        using var resplit = await client.SendAsync(Request(HttpMethod.Put, $"/api/transactions/{s.Expense}/allocations?fullWorthSpaceId={s.Space}", s.Owner,
            new[] { new { categoryId = s.CatA, amount = -100m } }));
        Assert.Equal(HttpStatusCode.NoContent, resplit.StatusCode);

        var body = await OverviewAsync(client, s);
        // B is gone, so the 30 refund falls back to proportional over the single remaining line -> 100 - 30 = 70 on A.
        Assert.Equal(70m, body.GetProperty("expenses").GetDecimal());
        Assert.Equal(70m, CategoryAmount(body, s.CatA));
    }

    // ---- helpers ----

    private static async Task<Scenario> SetupAsync(BackendWebApplicationFactory factory, Action<FullWorthDbContext, Scenario> seedTx)
    {
        var s = new Scenario(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser { Id = s.Owner, EmailNormalized = $"{s.Owner:N}@EX.COM".ToUpperInvariant(), DisplayName = "Owner", IsActive = true });
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = s.Space, Name = "Refund", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember { FullWorthSpaceId = s.Space, UserId = s.Owner, Role = FullWorthSpaceRoles.Owner });
            db.BankConnections.Add(new BankConnection { Id = s.Connection, FullWorthSpaceId = s.Space, Provider = "test", InstitutionName = "Bank", Country = "DE", ProviderSessionId = $"refund-{s.Connection:N}", Status = "AUTHORIZED" });
            db.Accounts.Add(new FinanceAccount { Id = s.Account, FullWorthSpaceId = s.Space, BankConnectionId = s.Connection, Provider = "test", IdentificationHash = $"refund-{s.Account:N}", ProviderAccountId = $"refund-{s.Account:N}", InstitutionName = "Bank", DisplayName = "Acc", Currency = "EUR" });
            db.AccountOwners.Add(new AccountOwner { AccountId = s.Account, UserId = s.Owner, OwnershipType = AccountOwnershipTypes.Owner });
            db.Categories.Add(new FinanceCategory { Id = s.CatA, FullWorthSpaceId = s.Space, Key = $"a-{s.CatA:N}", Name = "A" });
            db.Categories.Add(new FinanceCategory { Id = s.CatB, FullWorthSpaceId = s.Space, Key = $"b-{s.CatB:N}", Name = "B" });
            seedTx(db, s);
            await db.SaveChangesAsync();
        });
        return s;
    }

    private static FinanceTransaction Tx(Scenario s, Guid id, decimal amount, DateOnly date, Guid? categoryId = null, string currency = "EUR") => new()
    {
        Id = id,
        AccountId = s.Account,
        ExternalKey = $"refund-{id:N}",
        Amount = amount,
        Currency = currency,
        CategoryId = categoryId,
        BookingDate = date,
        Status = "BOOK"
    };

    private static async Task<JsonElement> OverviewAsync(HttpClient client, Scenario s)
    {
        using var response = await client.SendAsync(Request(HttpMethod.Get, $"/api/analytics/overview?fullWorthSpaceId={s.Space}", s.Owner));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static decimal CategoryAmount(JsonElement body, Guid categoryId) =>
        body.GetProperty("byCategory").EnumerateArray()
            .Single(x => x.GetProperty("categoryId").ValueKind != JsonValueKind.Null && x.GetProperty("categoryId").GetGuid() == categoryId)
            .GetProperty("amount").GetDecimal();

    private static HttpRequestMessage Request(HttpMethod method, string path, Guid userId, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        if (body is not null) request.Content = JsonContent.Create(body);
        return request;
    }

    private sealed record Scenario(Guid Owner, Guid Space, Guid Connection, Guid Account, Guid CatA, Guid CatB, Guid Expense, Guid Refund, Guid Refund2);
}
