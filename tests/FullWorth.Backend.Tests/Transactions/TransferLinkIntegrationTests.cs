using System.Net;
using System.Net.Http.Json;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.BankConnections;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;

namespace FullWorth.Backend.Tests.Transactions;

// HTTP-level coverage for §9.7 Flow D: the /api/transfers/candidates review endpoint and the manual
// transfer-link/unlink endpoints, including ownership enforcement (store-level validation logic is
// covered by the SQLite-based TransferLinkTests).
public sealed class TransferLinkIntegrationTests
{
    private static readonly DateOnly Day = new(2026, 7, 1);

    [Fact]
    public async Task CandidatesListsWithinWindowPairsWithDetail()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(Get($"/api/transfers/candidates?fullWorthSpaceId={s.Space}", s.Owner));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var pairs = await response.Content.ReadFromJsonAsync<List<PairView>>();
        var pair = Assert.Single(pairs!);
        Assert.Equal("EUR", pair.First.Currency);
        Assert.True(pair.First.Amount == -pair.Second.Amount);
    }

    [Fact]
    public async Task ManualLinkWorksOutsideTheAutoDetectionWindowThenUnlinkReleasesBothLegs()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory);
        using var client = factory.CreateClient();

        // The far-apart pair is outside the 3-day auto-detection window, so it must NOT show up as a candidate...
        using var candidates = await client.SendAsync(Get($"/api/transfers/candidates?fullWorthSpaceId={s.Space}", s.Owner));
        var pairs = await candidates.Content.ReadFromJsonAsync<List<PairView>>();
        Assert.DoesNotContain(pairs!, p => (p.First.Id == s.FarOut || p.Second.Id == s.FarOut));

        // ...but a user can still link it manually.
        using var link = await client.SendAsync(Post($"/api/transactions/{s.FarOut}/transfer-link?fullWorthSpaceId={s.Space}", s.Owner, new { otherTransactionId = s.FarIn }));
        Assert.Equal(HttpStatusCode.NoContent, link.StatusCode);

        using var unlink = await client.SendAsync(Delete($"/api/transactions/{s.FarOut}/transfer-link?fullWorthSpaceId={s.Space}", s.Owner));
        Assert.Equal(HttpStatusCode.NoContent, unlink.StatusCode);

        using var second = await client.SendAsync(Delete($"/api/transactions/{s.FarOut}/transfer-link?fullWorthSpaceId={s.Space}", s.Owner));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task NonMemberCannotLinkTransactions()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(Post($"/api/transactions/{s.FarOut}/transfer-link?fullWorthSpaceId={s.Space}", s.Outsider, new { otherTransactionId = s.FarIn }));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static async Task<Scenario> SeedAsync(BackendWebApplicationFactory factory)
    {
        var s = new Scenario(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await factory.SeedFullWorthUserAsync(s.Owner);
        await factory.SeedFullWorthUserAsync(s.Outsider);
        await factory.SeedAsync(async db =>
        {
            var connectionId = Guid.NewGuid();
            var acctA = Guid.NewGuid();
            var acctB = Guid.NewGuid();

            db.FullWorthSpaces.Add(new FullWorthSpace { Id = s.Space, Name = "Space", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember { FullWorthSpaceId = s.Space, UserId = s.Owner, Role = FullWorthSpaceRoles.Owner });

            db.BankConnections.Add(new BankConnection { Id = connectionId, FullWorthSpaceId = s.Space, Provider = "test", InstitutionName = "Bank", Country = "DE", ProviderSessionId = "link-session" });
            db.Accounts.Add(new FinanceAccount { Id = acctA, FullWorthSpaceId = s.Space, BankConnectionId = connectionId, Provider = "test", IdentificationHash = "lA", ProviderAccountId = "lA", InstitutionName = "Bank", DisplayName = "A", Currency = "EUR" });
            db.Accounts.Add(new FinanceAccount { Id = acctB, FullWorthSpaceId = s.Space, BankConnectionId = connectionId, Provider = "test", IdentificationHash = "lB", ProviderAccountId = "lB", InstitutionName = "Bank", DisplayName = "B", Currency = "EUR" });
            db.AccountOwners.Add(new AccountOwner { AccountId = acctA, UserId = s.Owner, OwnershipType = AccountOwnershipTypes.Owner });
            db.AccountOwners.Add(new AccountOwner { AccountId = acctB, UserId = s.Owner, OwnershipType = AccountOwnershipTypes.Owner });

            // Within the 3-day auto-detection window -> a real candidate pair.
            db.Transactions.Add(new FinanceTransaction { AccountId = acctA, ExternalKey = "near-out", Amount = -75m, Currency = "EUR", BookingDate = Day, Status = "BOOK" });
            db.Transactions.Add(new FinanceTransaction { AccountId = acctB, ExternalKey = "near-in", Amount = 75m, Currency = "EUR", BookingDate = Day.AddDays(1), Status = "BOOK" });
            // 10 days apart -> outside the window, only linkable manually.
            db.Transactions.Add(new FinanceTransaction { Id = s.FarOut, AccountId = acctA, ExternalKey = "far-out", Amount = -600m, Currency = "EUR", BookingDate = Day, Status = "BOOK" });
            db.Transactions.Add(new FinanceTransaction { Id = s.FarIn, AccountId = acctB, ExternalKey = "far-in", Amount = 600m, Currency = "EUR", BookingDate = Day.AddDays(10), Status = "BOOK" });
            await db.SaveChangesAsync();
        });
        return s;
    }

    private static HttpRequestMessage Get(string path, Guid userId) => Request(HttpMethod.Get, path, userId);
    private static HttpRequestMessage Delete(string path, Guid userId) => Request(HttpMethod.Delete, path, userId);
    private static HttpRequestMessage Post(string path, Guid userId, object body)
    {
        var request = Request(HttpMethod.Post, path, userId);
        request.Content = JsonContent.Create(body);
        return request;
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, Guid userId)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        return request;
    }

    private sealed record Scenario(Guid Space, Guid Owner, Guid Outsider, Guid FarOut, Guid FarIn);
    private sealed record LegView(Guid Id, Guid AccountId, string Account, decimal Amount, string Currency, DateOnly? BookingDate, string? Counterparty);
    private sealed record PairView(LegView First, LegView Second);
}
