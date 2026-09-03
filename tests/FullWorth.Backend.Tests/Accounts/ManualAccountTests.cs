using System.Net;
using System.Net.Http.Json;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.BankConnections;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;

namespace FullWorth.Backend.Tests.Accounts;

/// <summary>
/// Manual accounts (e.g. cash) exist without a bank connection: members create them directly in a
/// space, and owners maintain the balance through the manual-balance endpoint. Synced accounts stay
/// read-only — their balances come from the bank connection.
/// </summary>
public sealed class ManualAccountTests
{
    [Fact]
    public async Task MemberCreatesManualAccountWithoutConnectionAndInitialBalance()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(UserRequest(HttpMethod.Post, "/api/accounts", scenario.Member,
            new AccountCreateRequest(scenario.Space, null, "Bargeld", "EUR", true, 0, "Portemonnaie", 51.20m)));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var created = await response.Content.ReadFromJsonAsync<AccountListItem>();
        Assert.NotNull(created);
        Assert.Null(created!.BankConnectionId);
        Assert.Equal("manual", created.Provider);
        Assert.Equal("Portemonnaie", created.InstitutionName);
        Assert.Equal(51.20m, created.LatestBalance?.Amount);

        using var list = await client.SendAsync(UserRequest(HttpMethod.Get, "/api/accounts", scenario.Member));
        var accounts = await list.Content.ReadFromJsonAsync<List<AccountListItem>>() ?? [];
        var cash = Assert.Single(accounts, account => account.Id == created.Id);
        Assert.Equal(51.20m, cash.LatestBalance?.Amount);
        Assert.Equal("manual", cash.LatestBalance?.BalanceType);
    }

    [Fact]
    public async Task ManualAccountWithoutInstitutionFallsBackToDefaultName()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(UserRequest(HttpMethod.Post, "/api/accounts", scenario.Member,
            new AccountCreateRequest(scenario.Space, null, "Sparstrumpf", "EUR", true, 0)));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<AccountListItem>();
        Assert.Equal("Manual", created!.InstitutionName);
        Assert.Null(created.LatestBalance);
    }

    [Fact]
    public async Task NonMemberCannotCreateManualAccountInForeignSpace()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(UserRequest(HttpMethod.Post, "/api/accounts", scenario.Outsider,
            new AccountCreateRequest(scenario.Space, null, "Fremd", "EUR", true, 0)));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task EmptyBankConnectionIdIsRejected()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(UserRequest(HttpMethod.Post, "/api/accounts", scenario.Member,
            new AccountCreateRequest(scenario.Space, Guid.Empty, "Kaputt", "EUR", true, 0)));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task OwnerUpdatesManualBalanceAndLatestBalanceFollows()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        using var create = await client.SendAsync(UserRequest(HttpMethod.Post, "/api/accounts", scenario.Member,
            new AccountCreateRequest(scenario.Space, null, "Bargeld", "EUR", true, 0, null, 10m)));
        var account = await create.Content.ReadFromJsonAsync<AccountListItem>();

        using var update = await client.SendAsync(UserRequest(HttpMethod.Put,
            $"/api/accounts/{account!.Id}/balance?fullWorthSpaceId={scenario.Space}", scenario.Member,
            new ManualBalanceRequest(123.45m, null)));
        Assert.Equal(HttpStatusCode.NoContent, update.StatusCode);

        using var list = await client.SendAsync(UserRequest(HttpMethod.Get, "/api/accounts", scenario.Member));
        var accounts = await list.Content.ReadFromJsonAsync<List<AccountListItem>>() ?? [];
        Assert.Equal(123.45m, accounts.Single(x => x.Id == account.Id).LatestBalance?.Amount);
    }

    [Fact]
    public async Task ViewerCannotUpdateBalanceAndStrangerGetsNotFound()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        using var create = await client.SendAsync(UserRequest(HttpMethod.Post, "/api/accounts", scenario.Member,
            new AccountCreateRequest(scenario.Space, null, "Bargeld", "EUR", true, 0)));
        var account = await create.Content.ReadFromJsonAsync<AccountListItem>();

        // Grant the second member VIEWER access — may read, must not set balances.
        using var share = await client.SendAsync(UserRequest(HttpMethod.Post,
            $"/api/accounts/{account!.Id}/owners?fullWorthSpaceId={scenario.Space}", scenario.Member,
            new AddAccountOwnerRequest(scenario.SecondMember, AccountOwnershipTypes.Viewer)));
        Assert.Equal(HttpStatusCode.NoContent, share.StatusCode);

        using var viewer = await client.SendAsync(UserRequest(HttpMethod.Put,
            $"/api/accounts/{account.Id}/balance?fullWorthSpaceId={scenario.Space}", scenario.SecondMember,
            new ManualBalanceRequest(1m, null)));
        Assert.Equal(HttpStatusCode.Forbidden, viewer.StatusCode);

        using var stranger = await client.SendAsync(UserRequest(HttpMethod.Put,
            $"/api/accounts/{account.Id}/balance?fullWorthSpaceId={scenario.Space}", scenario.Outsider,
            new ManualBalanceRequest(1m, null)));
        Assert.Equal(HttpStatusCode.NotFound, stranger.StatusCode);
    }

    [Fact]
    public async Task SyncedAccountBalanceCannotBeSetManually()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(UserRequest(HttpMethod.Put,
            $"/api/accounts/{scenario.SyncedAccount}/balance?fullWorthSpaceId={scenario.Space}", scenario.Member,
            new ManualBalanceRequest(999m, null)));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task ConnectionAttachedAccountBalanceIsAlsoReadOnly()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        // Created through the API but tied to a bank connection: balances stay sync-owned.
        using var create = await client.SendAsync(UserRequest(HttpMethod.Post, "/api/accounts", scenario.Member,
            new AccountCreateRequest(scenario.Space, scenario.Connection, "Unterkonto", "EUR", true, 0)));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var account = await create.Content.ReadFromJsonAsync<AccountListItem>();

        using var update = await client.SendAsync(UserRequest(HttpMethod.Put,
            $"/api/accounts/{account!.Id}/balance?fullWorthSpaceId={scenario.Space}", scenario.Member,
            new ManualBalanceRequest(1m, null)));
        Assert.Equal(HttpStatusCode.Conflict, update.StatusCode);
    }

    [Fact]
    public async Task BalanceCurrencyMustMatchAccountCurrency()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        using var create = await client.SendAsync(UserRequest(HttpMethod.Post, "/api/accounts", scenario.Member,
            new AccountCreateRequest(scenario.Space, null, "Bargeld", "EUR", true, 0)));
        var account = await create.Content.ReadFromJsonAsync<AccountListItem>();

        using var mismatch = await client.SendAsync(UserRequest(HttpMethod.Put,
            $"/api/accounts/{account!.Id}/balance?fullWorthSpaceId={scenario.Space}", scenario.Member,
            new ManualBalanceRequest(5m, "USD")));
        Assert.Equal(HttpStatusCode.BadRequest, mismatch.StatusCode);
    }

    [Fact]
    public async Task OverflowingAmountsAreRejectedWith400()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        using var create = await client.SendAsync(UserRequest(HttpMethod.Post, "/api/accounts", scenario.Member,
            new AccountCreateRequest(scenario.Space, null, "Bargeld", "EUR", true, 0, null, 1_000_000_000_000m)));
        Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode);

        using var ok = await client.SendAsync(UserRequest(HttpMethod.Post, "/api/accounts", scenario.Member,
            new AccountCreateRequest(scenario.Space, null, "Bargeld", "EUR", true, 0)));
        var account = await ok.Content.ReadFromJsonAsync<AccountListItem>();
        using var update = await client.SendAsync(UserRequest(HttpMethod.Put,
            $"/api/accounts/{account!.Id}/balance?fullWorthSpaceId={scenario.Space}", scenario.Member,
            new ManualBalanceRequest(-1_000_000_000_000m, null)));
        Assert.Equal(HttpStatusCode.BadRequest, update.StatusCode);
    }

    [Fact]
    public async Task ViewerOfSyncedAccountGets403NotConflict()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        // Grant the second member VIEWER access on the synced account: the ownership check must win
        // over the it-is-not-manual conflict, mirroring PATCH/DELETE ordering.
        using var share = await client.SendAsync(UserRequest(HttpMethod.Post,
            $"/api/accounts/{scenario.SyncedAccount}/owners?fullWorthSpaceId={scenario.Space}", scenario.Member,
            new AddAccountOwnerRequest(scenario.SecondMember, AccountOwnershipTypes.Viewer)));
        Assert.Equal(HttpStatusCode.NoContent, share.StatusCode);

        using var response = await client.SendAsync(UserRequest(HttpMethod.Put,
            $"/api/accounts/{scenario.SyncedAccount}/balance?fullWorthSpaceId={scenario.Space}", scenario.SecondMember,
            new ManualBalanceRequest(1m, null)));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task FullWorthSpacesEndpointListsOnlyCallersSpaces()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        using var member = await client.SendAsync(UserRequest(HttpMethod.Get, "/api/fullworth-spaces", scenario.Member));
        Assert.Equal(HttpStatusCode.OK, member.StatusCode);
        var memberSpaces = await member.Content.ReadFromJsonAsync<List<FullWorthSpaceDto>>() ?? [];
        Assert.Contains(memberSpaces, space => space.Id == scenario.Space);
        Assert.DoesNotContain(memberSpaces, space => space.Id == scenario.OtherSpace);

        using var outsider = await client.SendAsync(UserRequest(HttpMethod.Get, "/api/fullworth-spaces", scenario.Outsider));
        var outsiderSpaces = await outsider.Content.ReadFromJsonAsync<List<FullWorthSpaceDto>>() ?? [];
        Assert.Contains(outsiderSpaces, space => space.Id == scenario.OtherSpace);
        Assert.DoesNotContain(outsiderSpaces, space => space.Id == scenario.Space);
    }

    private sealed record ManualScenario(Guid Space, Guid OtherSpace, Guid Member, Guid SecondMember, Guid Outsider, Guid Connection, Guid SyncedAccount);

    private static async Task<ManualScenario> SeedAsync(BackendWebApplicationFactory factory)
    {
        var scenario = new ManualScenario(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        await factory.SeedAsync(async db =>
        {
            foreach (var userId in new[] { scenario.Member, scenario.SecondMember, scenario.Outsider })
            {
                db.Users.Add(new FullWorthUser
                {
                    Id = userId,
                    EmailNormalized = $"{userId:N}@EXAMPLE.COM".ToUpperInvariant(),
                    DisplayName = $"User {userId:N}",
                    IsActive = true
                });
            }

            db.FullWorthSpaces.AddRange(
                new FullWorthSpace { Id = scenario.Space, Name = "Manual space", BaseCurrency = "EUR" },
                new FullWorthSpace { Id = scenario.OtherSpace, Name = "Other space", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.AddRange(
                new FullWorthSpaceMember { FullWorthSpaceId = scenario.Space, UserId = scenario.Member, Role = FullWorthSpaceRoles.Member },
                new FullWorthSpaceMember { FullWorthSpaceId = scenario.Space, UserId = scenario.SecondMember, Role = FullWorthSpaceRoles.Member },
                new FullWorthSpaceMember { FullWorthSpaceId = scenario.OtherSpace, UserId = scenario.Outsider, Role = FullWorthSpaceRoles.Member });

            db.BankConnections.Add(new BankConnection
            {
                Id = scenario.Connection,
                FullWorthSpaceId = scenario.Space,
                Provider = "enable-banking",
                InstitutionName = "Sync Bank",
                Country = "DE",
                ProviderSessionId = $"s-{scenario.Connection:N}",
                Status = "AUTHORIZED"
            });
            db.Accounts.Add(new FinanceAccount
            {
                Id = scenario.SyncedAccount,
                FullWorthSpaceId = scenario.Space,
                BankConnectionId = scenario.Connection,
                Provider = "enable-banking",
                IdentificationHash = $"h-{scenario.SyncedAccount:N}",
                ProviderAccountId = $"p-{scenario.SyncedAccount:N}",
                InstitutionName = "Sync Bank",
                DisplayName = "Giro",
                Currency = "EUR"
            });
            db.AccountOwners.Add(new AccountOwner
            {
                AccountId = scenario.SyncedAccount,
                UserId = scenario.Member,
                OwnershipType = AccountOwnershipTypes.Owner
            });

            await db.SaveChangesAsync();
        });

        return scenario;
    }

    private static HttpRequestMessage UserRequest(HttpMethod method, string path, Guid userId, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        if (body is not null) request.Content = JsonContent.Create(body);
        return request;
    }
}
