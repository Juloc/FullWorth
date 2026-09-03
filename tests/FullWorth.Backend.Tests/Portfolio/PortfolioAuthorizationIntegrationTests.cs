using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.BankConnections;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Fx;
using FullWorth.Backend.Modules.Parity;
using FullWorth.Backend.Modules.Portfolio;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Portfolio;

public sealed class PortfolioAuthorizationIntegrationTests
{
    [Fact]
    public async Task MemberCanReadSharedPortfolioButCannotWrite()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();

        using var assetRead = await client.SendAsync(UserRequest(HttpMethod.Get,
            $"/api/assets/{scenario.AssetA}?fullWorthSpaceId={scenario.SpaceA}", scenario.Member));
        using var liabilityRead = await client.SendAsync(UserRequest(HttpMethod.Get,
            $"/api/liabilities/{scenario.LiabilityA}?fullWorthSpaceId={scenario.SpaceA}", scenario.Member));
        Assert.Equal(HttpStatusCode.OK, assetRead.StatusCode);
        Assert.Equal(HttpStatusCode.OK, liabilityRead.StatusCode);

        using var assetWrite = await client.SendAsync(UserRequest(HttpMethod.Put,
            $"/api/assets/{scenario.AssetA}?fullWorthSpaceId={scenario.SpaceA}", scenario.Member,
            AssetPayload("Member attempt")));
        using var liabilityWrite = await client.SendAsync(UserRequest(HttpMethod.Put,
            $"/api/liabilities/{scenario.LiabilityA}?fullWorthSpaceId={scenario.SpaceA}", scenario.Member,
            LiabilityPayload("Member attempt")));
        Assert.Equal(HttpStatusCode.Forbidden, assetWrite.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, liabilityWrite.StatusCode);
    }

    [Fact]
    public async Task OwnerCanCreateAndUpdateSharedPortfolioResources()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();

        using var assetCreate = await client.SendAsync(UserRequest(HttpMethod.Post,
            $"/api/assets?fullWorthSpaceId={scenario.SpaceA}", scenario.Owner, AssetPayload("New asset")));
        Assert.Equal(HttpStatusCode.OK, assetCreate.StatusCode);

        using var liabilityUpdate = await client.SendAsync(UserRequest(HttpMethod.Put,
            $"/api/liabilities/{scenario.LiabilityA}?fullWorthSpaceId={scenario.SpaceA}", scenario.Owner,
            LiabilityPayload("Updated liability")));
        Assert.Equal(HttpStatusCode.OK, liabilityUpdate.StatusCode);
    }

    [Fact]
    public async Task CrossSpaceAndOutsidePortfolioIdsAreNotVisible()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();

        using var crossAsset = await client.SendAsync(UserRequest(HttpMethod.Get,
            $"/api/assets/{scenario.AssetB}?fullWorthSpaceId={scenario.SpaceA}", scenario.Owner));
        using var missingAsset = await client.SendAsync(UserRequest(HttpMethod.Get,
            $"/api/assets/{Guid.NewGuid():D}?fullWorthSpaceId={scenario.SpaceA}", scenario.Owner));
        using var outsideLiability = await client.SendAsync(UserRequest(HttpMethod.Get,
            $"/api/liabilities/{scenario.LiabilityA}?fullWorthSpaceId={scenario.SpaceA}", scenario.Outside));

        Assert.Equal(HttpStatusCode.NotFound, crossAsset.StatusCode);
        Assert.Equal(missingAsset.StatusCode, crossAsset.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, outsideLiability.StatusCode);
    }

    [Fact]
    public async Task NetWorthHistoryReturnsOnlyCurrentUsersSnapshots()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();

        using var ownerResponse = await client.SendAsync(UserRequest(HttpMethod.Get,
            $"/api/net-worth/history?fullWorthSpaceId={scenario.SpaceA}", scenario.Owner));
        Assert.Equal(HttpStatusCode.OK, ownerResponse.StatusCode);
        using var json = JsonDocument.Parse(await ownerResponse.Content.ReadAsStringAsync());
        Assert.Single(json.RootElement.EnumerateArray());
        Assert.Equal(111m, json.RootElement[0].GetProperty("netWorth").GetDecimal());

        var body = await ownerResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain(scenario.Member.ToString("D"), body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("userId", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RemovedMemberCannotReadPreviouslyCreatedSnapshot()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedScenarioAsync(factory);
        await factory.SeedAsync(async db =>
        {
            var membership = await db.FullWorthSpaceMembers.SingleAsync(member =>
                member.FullWorthSpaceId == scenario.SpaceA && member.UserId == scenario.Member);
            db.FullWorthSpaceMembers.Remove(membership);
            await db.SaveChangesAsync();
        });
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(UserRequest(HttpMethod.Get,
            $"/api/net-worth/history?fullWorthSpaceId={scenario.SpaceA}", scenario.Member));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Empty(json.RootElement.EnumerateArray());
    }

    [Fact]
    public async Task SnapshotCalculationExcludesOtherMembersPrivateAccounts()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedScenarioAsync(factory);

        await factory.SeedAsync(async db =>
        {
            var service = new NetWorthSnapshotService(db, new InvestmentNetWorthService(db, new CurrencyConverter(db)));
            var snapshots = await service.CaptureForUserAsync(scenario.SpaceA, scenario.Owner, CancellationToken.None);
            var eur = Assert.Single(snapshots.Where(snapshot => snapshot.Currency == "EUR"));

            Assert.Equal(100m, eur.Accounts);
            Assert.Equal(50m, eur.Assets);
            Assert.Equal(10m, eur.Liabilities);
            Assert.Equal(140m, eur.NetWorth);
        });
    }

    [Fact]
    public async Task PortfolioListsAreScopedToSelectedFullWorthSpaceMembership()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();

        using var assets = await client.SendAsync(UserRequest(HttpMethod.Get,
            $"/api/assets?fullWorthSpaceId={scenario.SpaceA}", scenario.Member));
        Assert.Equal(HttpStatusCode.OK, assets.StatusCode);
        using var assetsJson = JsonDocument.Parse(await assets.Content.ReadAsStringAsync());
        Assert.Single(assetsJson.RootElement.EnumerateArray());
        Assert.Equal(scenario.AssetA, assetsJson.RootElement[0].GetProperty("id").GetGuid());

        using var outside = await client.SendAsync(UserRequest(HttpMethod.Get,
            $"/api/assets?fullWorthSpaceId={scenario.SpaceA}", scenario.Outside));
        Assert.Equal(HttpStatusCode.OK, outside.StatusCode);
        using var outsideJson = JsonDocument.Parse(await outside.Content.ReadAsStringAsync());
        Assert.Empty(outsideJson.RootElement.EnumerateArray());
    }

    private static object AssetPayload(string name) => new
    {
        name,
        kind = "cash",
        currentValue = 75m,
        currency = "EUR",
        valuedAt = "2026-08-20",
        annualGrowthRate = (decimal?)null,
        includeInNetWorth = true,
        notes = "E7 test"
    };

    private static object LiabilityPayload(string name) => new
    {
        name,
        kind = "loan",
        currentBalance = 25m,
        currency = "EUR",
        interestRate = (decimal?)2.5m,
        regularPayment = (decimal?)5m,
        paymentCycle = "monthly",
        nextDueDate = "2026-09-01",
        endDate = (string?)null,
        includeInNetWorth = true,
        notes = "E7 test"
    };

    private static HttpRequestMessage UserRequest(HttpMethod method, string path, Guid userId, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        if (body is not null) request.Content = JsonContent.Create(body);
        return request;
    }

    private static async Task<Scenario> SeedScenarioAsync(BackendWebApplicationFactory factory)
    {
        var scenario = new Scenario(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        await factory.SeedAsync(async db =>
        {
            foreach (var userId in new[] { scenario.Owner, scenario.Member, scenario.PrivateOwner, scenario.Outside })
            {
                db.Users.Add(new FullWorthUser
                {
                    Id = userId,
                    EmailNormalized = $"{userId:N}@EXAMPLE.COM".ToUpperInvariant(),
                    DisplayName = $"E7 {userId:N}",
                    IsActive = true
                });
            }

            db.FullWorthSpaces.AddRange(
                new FullWorthSpace { Id = scenario.SpaceA, Name = "E7 Space A", BaseCurrency = "EUR" },
                new FullWorthSpace { Id = scenario.SpaceB, Name = "E7 Space B", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.AddRange(
                Member(scenario.SpaceA, scenario.Owner, FullWorthSpaceRoles.Owner),
                Member(scenario.SpaceA, scenario.Member, FullWorthSpaceRoles.Member),
                Member(scenario.SpaceA, scenario.PrivateOwner, FullWorthSpaceRoles.Member),
                Member(scenario.SpaceB, scenario.Owner, FullWorthSpaceRoles.Owner));

            db.BankConnections.AddRange(
                Connection(scenario.ConnectionA, scenario.SpaceA),
                Connection(scenario.ConnectionB, scenario.SpaceB));
            db.Accounts.AddRange(
                Account(scenario.AccountA, scenario.SpaceA, scenario.ConnectionA, "Visible account"),
                Account(scenario.PrivateAccount, scenario.SpaceA, scenario.ConnectionA, "Private account"),
                Account(scenario.AccountB, scenario.SpaceB, scenario.ConnectionB, "Space B account"));
            db.AccountOwners.AddRange(
                Owner(scenario.AccountA, scenario.Owner),
                Owner(scenario.PrivateAccount, scenario.PrivateOwner),
                Owner(scenario.AccountB, scenario.Owner));

            db.BalanceSnapshots.AddRange(
                new BalanceSnapshot { AccountId = scenario.AccountA, Amount = 100m, Currency = "EUR", BalanceType = "closingAvailable", CapturedAt = DateTimeOffset.UtcNow },
                new BalanceSnapshot { AccountId = scenario.PrivateAccount, Amount = 900m, Currency = "EUR", BalanceType = "closingAvailable", CapturedAt = DateTimeOffset.UtcNow });

            db.Assets.AddRange(
                new Asset { Id = scenario.AssetA, FullWorthSpaceId = scenario.SpaceA, Name = "Shared asset", CurrentValue = 50m, Currency = "EUR", IncludeInNetWorth = true },
                new Asset { Id = scenario.AssetB, FullWorthSpaceId = scenario.SpaceB, Name = "Other asset", CurrentValue = 500m, Currency = "EUR", IncludeInNetWorth = true });
            db.Liabilities.AddRange(
                new Liability { Id = scenario.LiabilityA, FullWorthSpaceId = scenario.SpaceA, Name = "Shared debt", CurrentBalance = 10m, Currency = "EUR", IncludeInNetWorth = true },
                new Liability { Id = scenario.LiabilityB, FullWorthSpaceId = scenario.SpaceB, Name = "Other debt", CurrentBalance = 100m, Currency = "EUR", IncludeInNetWorth = true });

            db.NetWorthSnapshots.AddRange(
                new NetWorthSnapshot { FullWorthSpaceId = scenario.SpaceA, UserId = scenario.Owner, Date = new DateOnly(2026, 8, 19), Currency = "EUR", Accounts = 100m, Assets = 20m, Liabilities = 9m, NetWorth = 111m },
                new NetWorthSnapshot { FullWorthSpaceId = scenario.SpaceA, UserId = scenario.Member, Date = new DateOnly(2026, 8, 19), Currency = "EUR", Accounts = 0m, Assets = 20m, Liabilities = 9m, NetWorth = 11m });

            await db.SaveChangesAsync();
        });

        return scenario;
    }

    private static FullWorthSpaceMember Member(Guid spaceId, Guid userId, string role) => new()
    {
        FullWorthSpaceId = spaceId,
        UserId = userId,
        Role = role
    };

    private static BankConnection Connection(Guid id, Guid spaceId) => new()
    {
        Id = id,
        FullWorthSpaceId = spaceId,
        Provider = "test",
        InstitutionName = "E7 Bank",
        Country = "DE",
        ProviderSessionId = $"e7-{id:N}",
        Status = "AUTHORIZED"
    };

    private static FinanceAccount Account(Guid id, Guid spaceId, Guid connectionId, string name) => new()
    {
        Id = id,
        FullWorthSpaceId = spaceId,
        BankConnectionId = connectionId,
        Provider = "test",
        IdentificationHash = $"e7-{id:N}",
        ProviderAccountId = $"provider-{id:N}",
        InstitutionName = "E7 Bank",
        DisplayName = name,
        Currency = "EUR",
        IncludeInNetWorth = true
    };

    private static AccountOwner Owner(Guid accountId, Guid userId) => new()
    {
        AccountId = accountId,
        UserId = userId,
        OwnershipType = AccountOwnershipTypes.Owner
    };

    private sealed record Scenario(
        Guid Owner,
        Guid Member,
        Guid PrivateOwner,
        Guid Outside,
        Guid SpaceA,
        Guid SpaceB,
        Guid ConnectionA,
        Guid ConnectionB,
        Guid AccountA,
        Guid PrivateAccount,
        Guid AccountB,
        Guid AssetA,
        Guid AssetB,
        Guid LiabilityA,
        Guid LiabilityB);
}
