using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.BankConnections;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Accounts;

public sealed class AccountAuthorizationIntegrationTests
{
    [Fact]
    public async Task ListUsesCurrentUserAndReturnsOnlyExplicitlyOwnedAccounts()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();

        using var responseA = await client.SendAsync(UserRequest(HttpMethod.Get, "/api/accounts", scenario.UserA));
        Assert.Equal(HttpStatusCode.OK, responseA.StatusCode);
        var accountsA = await ReadAccountsAsync(responseA);
        Assert.Contains(accountsA, account => account.Id == scenario.AccountA);
        Assert.DoesNotContain(accountsA, account => account.Id == scenario.AccountB);
        Assert.DoesNotContain(accountsA, account => account.Id == scenario.AccountOutsideMembership);

        using var responseB = await client.SendAsync(UserRequest(HttpMethod.Get, "/api/accounts", scenario.UserB));
        Assert.Equal(HttpStatusCode.OK, responseB.StatusCode);
        var accountsB = await ReadAccountsAsync(responseB);
        Assert.Contains(accountsB, account => account.Id == scenario.AccountB);
        Assert.DoesNotContain(accountsB, account => account.Id == scenario.AccountA);
    }

    [Fact]
    public async Task OwnerAndViewerCanReadWhileSameSpaceMemberCannotReadPrivateAccount()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();

        using var owner = await client.SendAsync(UserRequest(HttpMethod.Get,
            $"/api/accounts/{scenario.SharedAccount}?fullWorthSpaceId={scenario.SpaceA}", scenario.UserA));
        using var viewer = await client.SendAsync(UserRequest(HttpMethod.Get,
            $"/api/accounts/{scenario.SharedAccount}?fullWorthSpaceId={scenario.SpaceA}", scenario.Viewer));
        using var memberOnly = await client.SendAsync(UserRequest(HttpMethod.Get,
            $"/api/accounts/{scenario.AccountA}?fullWorthSpaceId={scenario.SpaceA}", scenario.MemberOnly));

        Assert.Equal(HttpStatusCode.OK, owner.StatusCode);
        Assert.Equal(HttpStatusCode.OK, viewer.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, memberOnly.StatusCode);
    }

    [Fact]
    public async Task InaccessibleAndNonexistentAccountHaveEquivalentPublicBehavior()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();

        using var inaccessible = await client.SendAsync(UserRequest(HttpMethod.Get,
            $"/api/accounts/{scenario.AccountB}?fullWorthSpaceId={scenario.SpaceA}", scenario.UserA));
        using var missing = await client.SendAsync(UserRequest(HttpMethod.Get,
            $"/api/accounts/{Guid.NewGuid():D}?fullWorthSpaceId={scenario.SpaceA}", scenario.UserA));

        Assert.Equal(HttpStatusCode.NotFound, inaccessible.StatusCode);
        Assert.Equal(missing.StatusCode, inaccessible.StatusCode);
        Assert.Equal(await missing.Content.ReadAsStringAsync(), await inaccessible.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task CrossSpaceUuidIsDeniedEvenWhenUserOwnsAccountOutsideSelectedSpace()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(UserRequest(HttpMethod.Get,
            $"/api/accounts/{scenario.AccountInSpaceB}?fullWorthSpaceId={scenario.SpaceA}", scenario.UserA));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ViewerMayReadButCannotMutateSettingsArchiveOrOwnership()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();
        var path = $"/api/accounts/{scenario.SharedAccount}?fullWorthSpaceId={scenario.SpaceA}";

        using var patch = await client.SendAsync(UserRequest(HttpMethod.Patch, path, scenario.Viewer,
            new AccountSettingsRequest("viewer-change", null, false, null)));
        using var delete = await client.SendAsync(UserRequest(HttpMethod.Delete, path, scenario.Viewer));
        using var share = await client.SendAsync(UserRequest(HttpMethod.Post,
            $"/api/accounts/{scenario.SharedAccount}/owners?fullWorthSpaceId={scenario.SpaceA}", scenario.Viewer,
            new AddAccountOwnerRequest(scenario.MemberOnly, AccountOwnershipTypes.Viewer)));

        Assert.Equal(HttpStatusCode.Forbidden, patch.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, share.StatusCode);
    }

    [Fact]
    public async Task OwnerCanUpdateSettings()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(UserRequest(HttpMethod.Patch,
            $"/api/accounts/{scenario.AccountA}?fullWorthSpaceId={scenario.SpaceA}", scenario.UserA,
            new AccountSettingsRequest("Renamed account", null, false, 42)));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await factory.SeedAsync(async db =>
        {
            var account = await db.Accounts.AsNoTracking().SingleAsync(x => x.Id == scenario.AccountA);
            Assert.Equal("Renamed account", account.DisplayName);
            Assert.False(account.IncludeInNetWorth);
            Assert.Equal(42, account.SortOrder);
        });
    }

    [Fact]
    public async Task CreateRequiresMembershipAndSameSpaceConnectionAndMakesCallerOwner()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();

        using var success = await client.SendAsync(UserRequest(HttpMethod.Post, "/api/accounts", scenario.MemberOnly,
            new
            {
                fullWorthSpaceId = scenario.SpaceA,
                bankConnectionId = scenario.ConnectionA,
                displayName = "Manual account",
                currency = "eur",
                includeInNetWorth = true,
                sortOrder = 7,
                userId = scenario.UserB
            }));
        Assert.Equal(HttpStatusCode.Created, success.StatusCode);
        var created = await success.Content.ReadFromJsonAsync<AccountListItem>();
        Assert.NotNull(created);
        Assert.Equal(scenario.SpaceA, created.FullWorthSpaceId);
        Assert.Equal("EUR", created.Currency);

        await factory.SeedAsync(async db =>
        {
            var owner = await db.AccountOwners.AsNoTracking().SingleAsync(x => x.AccountId == created.Id);
            Assert.Equal(scenario.MemberOnly, owner.UserId);
            Assert.Equal(AccountOwnershipTypes.Owner, owner.OwnershipType);
        });

        using var notMember = await client.SendAsync(UserRequest(HttpMethod.Post, "/api/accounts", scenario.UserOutside,
            new AccountCreateRequest(scenario.SpaceA, scenario.ConnectionA, "Denied", "EUR", true, 0)));
        Assert.Equal(HttpStatusCode.NotFound, notMember.StatusCode);

        using var wrongConnectionSpace = await client.SendAsync(UserRequest(HttpMethod.Post, "/api/accounts", scenario.UserA,
            new AccountCreateRequest(scenario.SpaceA, scenario.ConnectionB, "Denied", "EUR", true, 0)));
        Assert.Equal(HttpStatusCode.NotFound, wrongConnectionSpace.StatusCode);
    }

    [Fact]
    public async Task OwnerCanShareWithSameSpaceMemberButNotCrossSpaceUser()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();
        var ownersPath = $"/api/accounts/{scenario.AccountA}/owners?fullWorthSpaceId={scenario.SpaceA}";

        using var addViewer = await client.SendAsync(UserRequest(HttpMethod.Post, ownersPath, scenario.UserA,
            new AddAccountOwnerRequest(scenario.MemberOnly, AccountOwnershipTypes.Viewer)));
        Assert.Equal(HttpStatusCode.NoContent, addViewer.StatusCode);

        using var viewerRead = await client.SendAsync(UserRequest(HttpMethod.Get,
            $"/api/accounts/{scenario.AccountA}?fullWorthSpaceId={scenario.SpaceA}", scenario.MemberOnly));
        Assert.Equal(HttpStatusCode.OK, viewerRead.StatusCode);

        using var crossSpace = await client.SendAsync(UserRequest(HttpMethod.Post, ownersPath, scenario.UserA,
            new AddAccountOwnerRequest(scenario.UserOutside, AccountOwnershipTypes.Owner)));
        Assert.Equal(HttpStatusCode.NotFound, crossSpace.StatusCode);
    }

    [Fact]
    public async Task LastOwnerCannotBeRemoved()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(UserRequest(HttpMethod.Delete,
            $"/api/accounts/{scenario.AccountA}/owners/{scenario.UserA}?fullWorthSpaceId={scenario.SpaceA}", scenario.UserA));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await factory.SeedAsync(async db =>
            Assert.True(await db.AccountOwners.AnyAsync(x => x.AccountId == scenario.AccountA && x.UserId == scenario.UserA)));
    }

    [Fact]
    public async Task DeleteArchivesAccountWithoutDeletingHistory()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(UserRequest(HttpMethod.Delete,
            $"/api/accounts/{scenario.AccountA}?fullWorthSpaceId={scenario.SpaceA}", scenario.UserA));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await factory.SeedAsync(async db =>
        {
            var account = await db.Accounts.AsNoTracking().SingleAsync(x => x.Id == scenario.AccountA);
            Assert.False(account.IsActive);
            Assert.True(await db.BalanceSnapshots.AnyAsync(x => x.AccountId == scenario.AccountA));
            Assert.True(await db.Transactions.AnyAsync(x => x.AccountId == scenario.AccountA));
        });
    }

    [Fact]
    public async Task UnrelatedUserCannotMutateKnownAccountUuid()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();

        using var patch = await client.SendAsync(UserRequest(HttpMethod.Patch,
            $"/api/accounts/{scenario.AccountA}?fullWorthSpaceId={scenario.SpaceA}", scenario.UserB,
            new AccountSettingsRequest("attack", null, null, null)));
        using var delete = await client.SendAsync(UserRequest(HttpMethod.Delete,
            $"/api/accounts/{scenario.AccountA}?fullWorthSpaceId={scenario.SpaceA}", scenario.UserB));

        Assert.Equal(HttpStatusCode.NotFound, patch.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
    }

    private static HttpRequestMessage UserRequest(HttpMethod method, string path, Guid userId, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        if (body is not null) request.Content = JsonContent.Create(body);
        return request;
    }

    private static async Task<List<AccountListItem>> ReadAccountsAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<List<AccountListItem>>() ?? [];

    private static async Task<Scenario> SeedScenarioAsync(BackendWebApplicationFactory factory)
    {
        var scenario = new Scenario(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        await factory.SeedAsync(async db =>
        {
            foreach (var userId in new[] { scenario.UserA, scenario.UserB, scenario.Viewer, scenario.MemberOnly, scenario.UserOutside })
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
                new FullWorthSpace { Id = scenario.SpaceA, Name = "Space A", BaseCurrency = "EUR" },
                new FullWorthSpace { Id = scenario.SpaceB, Name = "Space B", BaseCurrency = "EUR" });

            db.FullWorthSpaceMembers.AddRange(
                Member(scenario.SpaceA, scenario.UserA),
                Member(scenario.SpaceA, scenario.UserB),
                Member(scenario.SpaceA, scenario.Viewer),
                Member(scenario.SpaceA, scenario.MemberOnly),
                Member(scenario.SpaceB, scenario.UserA),
                Member(scenario.SpaceB, scenario.UserOutside));

            db.BankConnections.AddRange(
                Connection(scenario.ConnectionA, scenario.SpaceA, "Bank A"),
                Connection(scenario.ConnectionB, scenario.SpaceB, "Bank B"));

            var accountA = Account(scenario.AccountA, scenario.SpaceA, scenario.ConnectionA, "Account A");
            var accountB = Account(scenario.AccountB, scenario.SpaceA, scenario.ConnectionA, "Account B");
            var shared = Account(scenario.SharedAccount, scenario.SpaceA, scenario.ConnectionA, "Shared");
            var spaceB = Account(scenario.AccountInSpaceB, scenario.SpaceB, scenario.ConnectionB, "Space B own");
            var outsideMembership = Account(scenario.AccountOutsideMembership, scenario.SpaceB, scenario.ConnectionB, "No membership");
            db.Accounts.AddRange(accountA, accountB, shared, spaceB, outsideMembership);

            db.AccountOwners.AddRange(
                Owner(scenario.AccountA, scenario.UserA, AccountOwnershipTypes.Owner),
                Owner(scenario.AccountB, scenario.UserB, AccountOwnershipTypes.Owner),
                Owner(scenario.SharedAccount, scenario.UserA, AccountOwnershipTypes.Owner),
                Owner(scenario.SharedAccount, scenario.Viewer, AccountOwnershipTypes.Viewer),
                Owner(scenario.AccountInSpaceB, scenario.UserA, AccountOwnershipTypes.Owner),
                Owner(scenario.AccountOutsideMembership, scenario.UserB, AccountOwnershipTypes.Owner));

            db.BalanceSnapshots.Add(new BalanceSnapshot
            {
                AccountId = scenario.AccountA,
                Amount = 100m,
                Currency = "EUR",
                BalanceType = "closingBooked"
            });
            db.Transactions.Add(new FinanceTransaction
            {
                AccountId = scenario.AccountA,
                ExternalKey = $"e2-{Guid.NewGuid():N}",
                Amount = -5m,
                Currency = "EUR"
            });

            await db.SaveChangesAsync();
        });

        return scenario;
    }

    private static FullWorthSpaceMember Member(Guid spaceId, Guid userId) => new()
    {
        FullWorthSpaceId = spaceId,
        UserId = userId,
        Role = FullWorthSpaceRoles.Member
    };

    private static BankConnection Connection(Guid id, Guid spaceId, string name) => new()
    {
        Id = id,
        FullWorthSpaceId = spaceId,
        Provider = "test",
        InstitutionName = name,
        Country = "DE",
        ProviderSessionId = $"e2-{id:N}",
        Status = "AUTHORIZED"
    };

    private static FinanceAccount Account(Guid id, Guid spaceId, Guid connectionId, string name) => new()
    {
        Id = id,
        FullWorthSpaceId = spaceId,
        BankConnectionId = connectionId,
        Provider = "test",
        IdentificationHash = $"e2-{id:N}",
        ProviderAccountId = $"provider-{id:N}",
        InstitutionName = name.Contains("Space B", StringComparison.Ordinal) ? "Bank B" : "Bank A",
        DisplayName = name,
        Currency = "EUR"
    };

    private static AccountOwner Owner(Guid accountId, Guid userId, string type) => new()
    {
        AccountId = accountId,
        UserId = userId,
        OwnershipType = type
    };

    private sealed record Scenario(
        Guid UserA,
        Guid UserB,
        Guid Viewer,
        Guid MemberOnly,
        Guid UserOutside,
        Guid SpaceA,
        Guid SpaceB,
        Guid ConnectionA,
        Guid ConnectionB,
        Guid AccountA,
        Guid AccountB,
        Guid SharedAccount,
        Guid AccountInSpaceB,
        Guid AccountOutsideMembership);
}
