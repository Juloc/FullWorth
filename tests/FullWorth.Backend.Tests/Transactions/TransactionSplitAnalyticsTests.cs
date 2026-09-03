using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.BankConnections;
using FullWorth.Backend.Modules.Categories;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;

namespace FullWorth.Backend.Tests.Transactions;

public sealed class TransactionSplitAnalyticsTests
{
    [Fact]
    public async Task SplitLinesReplaceParentCategoryInOverview()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedScenarioAsync(factory, -50m);
        using var client = factory.CreateClient();

        using var put = await client.SendAsync(Request(HttpMethod.Put, $"/api/transactions/{s.Tx}/allocations?fullWorthSpaceId={s.Space}", s.Owner,
            new[] { new { categoryId = s.CatB, amount = -30m, note = "groceries", purchaseItemId = (Guid?)null }, new { categoryId = s.CatA, amount = -20m, note = (string?)null, purchaseItemId = (Guid?)null } }));
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);

        var body = await OverviewAsync(client, s);
        var map = CategoryMap(body);
        Assert.Equal(30m, map[s.CatB]);
        Assert.Equal(20m, map[s.CatA]);
        Assert.Equal(50m, body.GetProperty("expenses").GetDecimal());
    }

    [Fact]
    public async Task CouponAdjustmentNetsCategoryAnalyticsWithoutInflatingHeadlineExpense()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedScenarioAsync(factory, -50m);
        using var client = factory.CreateClient();

        using var put = await client.SendAsync(Request(HttpMethod.Put, $"/api/transactions/{s.Tx}/allocations?fullWorthSpaceId={s.Space}", s.Owner,
            new[]
            {
                new { categoryId = s.CatB, amount = -60m, note = "gross items", purchaseItemId = (Guid?)null },
                new { categoryId = s.CatA, amount = 10m, note = "coupon", purchaseItemId = (Guid?)null }
            }));
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);

        var body = await OverviewAsync(client, s);
        var map = CategoryMap(body);
        Assert.Equal(60m, map[s.CatB]);
        Assert.Equal(-10m, map[s.CatA]);
        Assert.Equal(50m, body.GetProperty("expenses").GetDecimal());
    }

    private static async Task<Scenario> SeedScenarioAsync(BackendWebApplicationFactory factory, decimal amount)
    {
        var s = new Scenario(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser { Id = s.Owner, EmailNormalized = $"{s.Owner:N}@EX.COM".ToUpperInvariant(), DisplayName = "Owner", IsActive = true });
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = s.Space, Name = "Split", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember { FullWorthSpaceId = s.Space, UserId = s.Owner, Role = FullWorthSpaceRoles.Owner });
            db.BankConnections.Add(new BankConnection { Id = s.Connection, FullWorthSpaceId = s.Space, Provider = "test", InstitutionName = "Bank", Country = "DE", ProviderSessionId = $"split-{s.Connection:N}", Status = "AUTHORIZED" });
            db.Accounts.Add(new FinanceAccount { Id = s.Account, FullWorthSpaceId = s.Space, BankConnectionId = s.Connection, Provider = "test", IdentificationHash = $"split-{s.Account:N}", ProviderAccountId = $"split-{s.Account:N}", InstitutionName = "Bank", DisplayName = "Acc", Currency = "EUR" });
            db.AccountOwners.Add(new AccountOwner { AccountId = s.Account, UserId = s.Owner, OwnershipType = AccountOwnershipTypes.Owner });
            db.Categories.Add(new FinanceCategory { Id = s.CatA, FullWorthSpaceId = s.Space, Key = $"split-a-{s.CatA:N}", Name = "A" });
            db.Categories.Add(new FinanceCategory { Id = s.CatB, FullWorthSpaceId = s.Space, Key = $"split-b-{s.CatB:N}", Name = "B" });
            db.Transactions.Add(new FinanceTransaction { Id = s.Tx, AccountId = s.Account, ExternalKey = $"split-{s.Tx:N}", Amount = amount, Currency = "EUR", CategoryId = s.CatA, BookingDate = new DateOnly(2026, 6, 15), Status = "BOOK" });
            await db.SaveChangesAsync();
        });
        return s;
    }

    private static async Task<JsonElement> OverviewAsync(HttpClient client, Scenario s)
    {
        using var response = await client.SendAsync(Request(HttpMethod.Get, $"/api/analytics/overview?fullWorthSpaceId={s.Space}", s.Owner));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static Dictionary<Guid, decimal> CategoryMap(JsonElement body) => body.GetProperty("byCategory").EnumerateArray()
        .Where(x => x.GetProperty("categoryId").ValueKind != JsonValueKind.Null)
        .ToDictionary(x => x.GetProperty("categoryId").GetGuid(), x => x.GetProperty("amount").GetDecimal());

    private static HttpRequestMessage Request(HttpMethod method, string path, Guid userId, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        if (body is not null) request.Content = JsonContent.Create(body);
        return request;
    }

    private sealed record Scenario(Guid Owner, Guid Space, Guid Connection, Guid Account, Guid CatA, Guid CatB, Guid Tx);
}
