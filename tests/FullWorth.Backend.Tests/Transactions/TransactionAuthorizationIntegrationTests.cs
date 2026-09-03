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
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Transactions;

public sealed class TransactionAuthorizationIntegrationTests
{
    [Fact]
    public async Task ListReturnsOnlyTransactionsFromAccessibleAccounts()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();

        using var ownerResponse = await client.SendAsync(UserRequest(
            HttpMethod.Get,
            $"/api/transactions?fullWorthSpaceId={scenario.SpaceA}",
            scenario.OwnerA));
        Assert.Equal(HttpStatusCode.OK, ownerResponse.StatusCode);
        using var ownerJson = JsonDocument.Parse(await ownerResponse.Content.ReadAsStringAsync());
        Assert.Equal(1, ownerJson.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(scenario.TransactionA, ownerJson.RootElement.GetProperty("items")[0].GetProperty("id").GetGuid());

        using var otherOwnerResponse = await client.SendAsync(UserRequest(
            HttpMethod.Get,
            $"/api/transactions?fullWorthSpaceId={scenario.SpaceA}",
            scenario.OwnerB));
        Assert.Equal(HttpStatusCode.OK, otherOwnerResponse.StatusCode);
        using var otherJson = JsonDocument.Parse(await otherOwnerResponse.Content.ReadAsStringAsync());
        Assert.Equal(1, otherJson.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(scenario.TransactionB, otherJson.RootElement.GetProperty("items")[0].GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task ViewerCanReadButSameSpaceMemberWithoutAccountOwnerCannot()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();
        var path = $"/api/transactions/{scenario.TransactionA}?fullWorthSpaceId={scenario.SpaceA}";

        using var viewer = await client.SendAsync(UserRequest(HttpMethod.Get, path, scenario.ViewerA));
        using var memberOnly = await client.SendAsync(UserRequest(HttpMethod.Get, path, scenario.MemberOnlyA));

        Assert.Equal(HttpStatusCode.OK, viewer.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, memberOnly.StatusCode);
    }

    [Fact]
    public async Task InaccessibleAndNonexistentTransactionHaveEquivalentPublicBehavior()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();

        using var inaccessible = await client.SendAsync(UserRequest(
            HttpMethod.Get,
            $"/api/transactions/{scenario.TransactionB}?fullWorthSpaceId={scenario.SpaceA}",
            scenario.OwnerA));
        using var missing = await client.SendAsync(UserRequest(
            HttpMethod.Get,
            $"/api/transactions/{Guid.NewGuid():D}?fullWorthSpaceId={scenario.SpaceA}",
            scenario.OwnerA));

        Assert.Equal(HttpStatusCode.NotFound, inaccessible.StatusCode);
        Assert.Equal(missing.StatusCode, inaccessible.StatusCode);
        Assert.Equal(await missing.Content.ReadAsStringAsync(), await inaccessible.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task CrossSpaceUuidIsDeniedForWrongSelectedSpace()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(UserRequest(
            HttpMethod.Get,
            $"/api/transactions/{scenario.TransactionSpaceB}?fullWorthSpaceId={scenario.SpaceA}",
            scenario.OwnerA));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AccountFilterCannotWidenAuthorizationScope()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(UserRequest(
            HttpMethod.Get,
            $"/api/transactions?fullWorthSpaceId={scenario.SpaceA}&accountId={scenario.AccountB}&includeIgnored=true&limit=5000&sort=amount&order=asc",
            scenario.OwnerA));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(0, json.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(0, json.RootElement.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task OwnerCanClassifyTransactionWithSameSpaceCategory()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(UserRequest(
            HttpMethod.Patch,
            $"/api/transactions/{scenario.TransactionA}/classification?fullWorthSpaceId={scenario.SpaceA}",
            scenario.OwnerA,
            new TransactionClassification(scenario.CategoryA, true, true)));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await factory.SeedAsync(async db =>
        {
            var transaction = await db.Transactions.AsNoTracking().SingleAsync(x => x.Id == scenario.TransactionA);
            Assert.Equal(scenario.CategoryA, transaction.CategoryId);
            Assert.True(transaction.IsIgnored);
            Assert.True(transaction.IsTransfer);
            Assert.Equal("manual", transaction.CategorizationSource);
        });
    }

    [Fact]
    public async Task ViewerCanSeeTransactionButCannotClassifyIt()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(UserRequest(
            HttpMethod.Patch,
            $"/api/transactions/{scenario.TransactionA}/classification?fullWorthSpaceId={scenario.SpaceA}",
            scenario.ViewerA,
            new TransactionClassification(scenario.CategoryA, true, false)));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task InvisibleCallerCannotClassifyKnownTransactionUuid()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(UserRequest(
            HttpMethod.Patch,
            $"/api/transactions/{scenario.TransactionA}/classification?fullWorthSpaceId={scenario.SpaceA}",
            scenario.MemberOnlyA,
            new TransactionClassification(scenario.CategoryA, true, false)));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task CrossSpaceCategoryCannotBeLinkedDuringClassification()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(UserRequest(
            HttpMethod.Patch,
            $"/api/transactions/{scenario.TransactionA}/classification?fullWorthSpaceId={scenario.SpaceA}",
            scenario.OwnerA,
            new TransactionClassification(scenario.CategoryB, false, false)));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await factory.SeedAsync(async db =>
            Assert.Null(await db.Transactions.Where(x => x.Id == scenario.TransactionA).Select(x => x.CategoryId).SingleAsync()));
    }

    [Fact]
    public async Task NormalTransactionDtosDoNotExposeRawProviderJson()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(UserRequest(
            HttpMethod.Get,
            $"/api/transactions/{scenario.TransactionA}?fullWorthSpaceId={scenario.SpaceA}",
            scenario.OwnerA));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("rawJson", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret-provider-payload", body, StringComparison.Ordinal);
    }

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
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid());

        await factory.SeedAsync(async db =>
        {
            foreach (var userId in new[] { scenario.OwnerA, scenario.OwnerB, scenario.ViewerA, scenario.MemberOnlyA })
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
                new FullWorthSpace { Id = scenario.SpaceA, Name = "Transaction Space A", BaseCurrency = "EUR" },
                new FullWorthSpace { Id = scenario.SpaceB, Name = "Transaction Space B", BaseCurrency = "EUR" });

            db.FullWorthSpaceMembers.AddRange(
                Member(scenario.SpaceA, scenario.OwnerA),
                Member(scenario.SpaceA, scenario.OwnerB),
                Member(scenario.SpaceA, scenario.ViewerA),
                Member(scenario.SpaceA, scenario.MemberOnlyA),
                Member(scenario.SpaceB, scenario.OwnerA));

            db.BankConnections.AddRange(
                Connection(scenario.ConnectionA, scenario.SpaceA, "Transaction Bank A"),
                Connection(scenario.ConnectionB, scenario.SpaceB, "Transaction Bank B"));

            db.Accounts.AddRange(
                Account(scenario.AccountA, scenario.SpaceA, scenario.ConnectionA, "Owner A account"),
                Account(scenario.AccountB, scenario.SpaceA, scenario.ConnectionA, "Owner B private account"),
                Account(scenario.AccountSpaceB, scenario.SpaceB, scenario.ConnectionB, "Owner A space B account"));

            db.AccountOwners.AddRange(
                Owner(scenario.AccountA, scenario.OwnerA, AccountOwnershipTypes.Owner),
                Owner(scenario.AccountA, scenario.ViewerA, AccountOwnershipTypes.Viewer),
                Owner(scenario.AccountB, scenario.OwnerB, AccountOwnershipTypes.Owner),
                Owner(scenario.AccountSpaceB, scenario.OwnerA, AccountOwnershipTypes.Owner));

            db.Categories.AddRange(
                new FinanceCategory
                {
                    Id = scenario.CategoryA,
                    FullWorthSpaceId = scenario.SpaceA,
                    Key = $"e3-a-{scenario.CategoryA:N}",
                    Name = "Space A category"
                },
                new FinanceCategory
                {
                    Id = scenario.CategoryB,
                    FullWorthSpaceId = scenario.SpaceB,
                    Key = $"e3-b-{scenario.CategoryB:N}",
                    Name = "Space B category"
                });

            db.Transactions.AddRange(
                Transaction(scenario.TransactionA, scenario.AccountA, "tx-a", "secret-provider-payload"),
                Transaction(scenario.TransactionB, scenario.AccountB, "tx-b", "private-b"),
                Transaction(scenario.TransactionSpaceB, scenario.AccountSpaceB, "tx-space-b", "space-b"));

            await db.SaveChangesAsync();

            // These tests predate the capability layer and assert account-ownership semantics; grant the
            // members the editor template so the deny-by-default capability gate stays transparent and the
            // account-level ownership checks remain the effective authorization boundary.
            await CapabilityTestSeeding.GrantEditorAsync(db, scenario.SpaceA,
                scenario.OwnerA, scenario.OwnerB, scenario.ViewerA, scenario.MemberOnlyA);
            await CapabilityTestSeeding.GrantEditorAsync(db, scenario.SpaceB, scenario.OwnerA);
        });

        return scenario;
    }

    private static FullWorthSpaceMember Member(Guid spaceId, Guid userId) => new()
    {
        FullWorthSpaceId = spaceId,
        UserId = userId,
        Role = FullWorthSpaceRoles.Member
    };

    private static BankConnection Connection(Guid id, Guid spaceId, string institutionName) => new()
    {
        Id = id,
        FullWorthSpaceId = spaceId,
        Provider = "test",
        InstitutionName = institutionName,
        Country = "DE",
        ProviderSessionId = $"e3-{id:N}",
        Status = "AUTHORIZED"
    };

    private static FinanceAccount Account(Guid id, Guid spaceId, Guid connectionId, string name) => new()
    {
        Id = id,
        FullWorthSpaceId = spaceId,
        BankConnectionId = connectionId,
        Provider = "test",
        IdentificationHash = $"e3-{id:N}",
        ProviderAccountId = $"provider-{id:N}",
        InstitutionName = "Transaction Test Bank",
        DisplayName = name,
        Currency = "EUR"
    };

    private static AccountOwner Owner(Guid accountId, Guid userId, string ownershipType) => new()
    {
        AccountId = accountId,
        UserId = userId,
        OwnershipType = ownershipType
    };

    private static FinanceTransaction Transaction(Guid id, Guid accountId, string externalKey, string rawMarker) => new()
    {
        Id = id,
        AccountId = accountId,
        ExternalKey = externalKey,
        Amount = -12.34m,
        Currency = "EUR",
        BookingDate = new DateOnly(2026, 8, 15),
        Counterparty = "E3 merchant",
        Description = "E3 authorization test",
        RawJson = JsonSerializer.Serialize(new { marker = rawMarker })
    };

    private sealed record Scenario(
        Guid OwnerA,
        Guid OwnerB,
        Guid ViewerA,
        Guid MemberOnlyA,
        Guid SpaceA,
        Guid SpaceB,
        Guid ConnectionA,
        Guid ConnectionB,
        Guid AccountA,
        Guid AccountB,
        Guid AccountSpaceB,
        Guid TransactionA,
        Guid TransactionB,
        Guid TransactionSpaceB,
        Guid CategoryA,
        Guid CategoryB);
}
