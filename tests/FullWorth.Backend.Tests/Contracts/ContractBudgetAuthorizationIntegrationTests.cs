using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.BankConnections;
using FullWorth.Backend.Modules.Budgets;
using FullWorth.Backend.Modules.Categories;
using FullWorth.Backend.Modules.Contracts;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Contracts;

public sealed class ContractBudgetAuthorizationIntegrationTests
{
    [Fact]
    public async Task UnlinkedContractIsReadableByMemberButMemberCannotMutate()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();

        using var read = await client.SendAsync(UserRequest(HttpMethod.Get,
            $"/api/contracts/{scenario.UnlinkedContract}?fullWorthSpaceId={scenario.SpaceA}", scenario.Member));
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);

        using var update = await client.SendAsync(UserRequest(HttpMethod.Put,
            $"/api/contracts/{scenario.UnlinkedContract}?fullWorthSpaceId={scenario.SpaceA}", scenario.Member,
            ContractPayload(null, scenario.CategoryA, "Member attempt")));
        Assert.Equal(HttpStatusCode.Forbidden, update.StatusCode);
    }

    [Fact]
    public async Task LinkedContractRequiresExplicitAccountVisibilityAndOwnerForWrite()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();
        var path = $"/api/contracts/{scenario.LinkedContract}?fullWorthSpaceId={scenario.SpaceA}";

        using var ownerRead = await client.SendAsync(UserRequest(HttpMethod.Get, path, scenario.Owner));
        using var viewerRead = await client.SendAsync(UserRequest(HttpMethod.Get, path, scenario.OwnerViewer));
        using var memberRead = await client.SendAsync(UserRequest(HttpMethod.Get, path, scenario.Member));
        Assert.Equal(HttpStatusCode.OK, ownerRead.StatusCode);
        Assert.Equal(HttpStatusCode.OK, viewerRead.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, memberRead.StatusCode);

        using var viewerWrite = await client.SendAsync(UserRequest(HttpMethod.Put, path, scenario.OwnerViewer,
            ContractPayload(scenario.AccountA, scenario.CategoryA, "Viewer attempt")));
        Assert.Equal(HttpStatusCode.Forbidden, viewerWrite.StatusCode);
    }

    [Fact]
    public async Task ContractListDoesNotRevealPrivateLinkedAccountContracts()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(UserRequest(HttpMethod.Get,
            $"/api/contracts?fullWorthSpaceId={scenario.SpaceA}", scenario.Owner));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var ids = json.RootElement.EnumerateArray().Select(item => item.GetProperty("id").GetGuid()).ToArray();
        Assert.Contains(scenario.UnlinkedContract, ids);
        Assert.Contains(scenario.LinkedContract, ids);
        Assert.DoesNotContain(scenario.PrivateLinkedContract, ids);
    }

    [Fact]
    public async Task ContractCreateRejectsViewerAndCrossSpaceReferences()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();

        using var viewerAccount = await client.SendAsync(UserRequest(HttpMethod.Post,
            $"/api/contracts?fullWorthSpaceId={scenario.SpaceA}", scenario.OwnerViewer,
            ContractPayload(scenario.AccountA, scenario.CategoryA, "Viewer linked")));
        Assert.Equal(HttpStatusCode.Forbidden, viewerAccount.StatusCode);

        using var crossAccount = await client.SendAsync(UserRequest(HttpMethod.Post,
            $"/api/contracts?fullWorthSpaceId={scenario.SpaceA}", scenario.Owner,
            ContractPayload(scenario.AccountSpaceB, scenario.CategoryA, "Cross account")));
        Assert.Equal(HttpStatusCode.NotFound, crossAccount.StatusCode);

        using var crossCategory = await client.SendAsync(UserRequest(HttpMethod.Post,
            $"/api/contracts?fullWorthSpaceId={scenario.SpaceA}", scenario.Owner,
            ContractPayload(scenario.AccountA, scenario.CategoryB, "Cross category")));
        Assert.Equal(HttpStatusCode.NotFound, crossCategory.StatusCode);
    }

    [Fact]
    public async Task ContractDeleteArchivesInsteadOfDeletingHistory()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(UserRequest(HttpMethod.Delete,
            $"/api/contracts/{scenario.LinkedContract}?fullWorthSpaceId={scenario.SpaceA}", scenario.Owner));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        await factory.SeedAsync(async db =>
        {
            var contract = await db.Contracts.AsNoTracking().SingleAsync(item => item.Id == scenario.LinkedContract);
            Assert.False(contract.IsActive);
            Assert.Equal(scenario.AccountA, contract.AccountId);
        });
    }

    [Fact]
    public async Task ContractDetectionOnlyUsesCallerVisibleAccounts()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(UserRequest(HttpMethod.Get,
            $"/api/contracts/detection?fullWorthSpaceId={scenario.SpaceA}", scenario.Owner));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var accountIds = json.RootElement.EnumerateArray()
            .Where(item => item.TryGetProperty("accountId", out var accountId) && accountId.ValueKind != JsonValueKind.Null)
            .Select(item => item.GetProperty("accountId").GetGuid())
            .ToArray();
        Assert.Contains(scenario.AccountA, accountIds);
        Assert.DoesNotContain(scenario.PrivateAccount, accountIds);

        using var outsider = await client.SendAsync(UserRequest(HttpMethod.Get,
            $"/api/contracts/detection?fullWorthSpaceId={scenario.SpaceA}", scenario.Outside));
        Assert.Equal(HttpStatusCode.NotFound, outsider.StatusCode);
    }

    [Fact]
    public async Task BudgetIsReadableByMemberButOnlyOwnerCanWrite()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();
        var path = $"/api/budgets/{scenario.BudgetA}?fullWorthSpaceId={scenario.SpaceA}";

        using var read = await client.SendAsync(UserRequest(HttpMethod.Get, path, scenario.Member));
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);

        using var update = await client.SendAsync(UserRequest(HttpMethod.Put, path, scenario.Member,
            BudgetPayload(scenario.CategoryA, "Member budget")));
        Assert.Equal(HttpStatusCode.Forbidden, update.StatusCode);

        using var ownerUpdate = await client.SendAsync(UserRequest(HttpMethod.Put, path, scenario.Owner,
            BudgetPayload(scenario.CategoryA, "Updated budget")));
        Assert.Equal(HttpStatusCode.OK, ownerUpdate.StatusCode);
    }

    [Fact]
    public async Task BudgetCrossSpaceAndMissingIdsDoNotLeak()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();

        using var crossSpace = await client.SendAsync(UserRequest(HttpMethod.Get,
            $"/api/budgets/{scenario.BudgetB}?fullWorthSpaceId={scenario.SpaceA}", scenario.Owner));
        using var missing = await client.SendAsync(UserRequest(HttpMethod.Get,
            $"/api/budgets/{Guid.NewGuid():D}?fullWorthSpaceId={scenario.SpaceA}", scenario.Owner));
        Assert.Equal(HttpStatusCode.NotFound, crossSpace.StatusCode);
        Assert.Equal(missing.StatusCode, crossSpace.StatusCode);

        using var crossCategory = await client.SendAsync(UserRequest(HttpMethod.Post,
            $"/api/budgets?fullWorthSpaceId={scenario.SpaceA}", scenario.Owner,
            BudgetPayload(scenario.CategoryB, "Cross-category budget")));
        Assert.Equal(HttpStatusCode.NotFound, crossCategory.StatusCode);
    }

    [Fact]
    public async Task BudgetDeleteArchivesDefinition()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(UserRequest(HttpMethod.Delete,
            $"/api/budgets/{scenario.BudgetA}?fullWorthSpaceId={scenario.SpaceA}", scenario.Owner));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        await factory.SeedAsync(async db =>
            Assert.False(await db.Budgets.Where(budget => budget.Id == scenario.BudgetA).Select(budget => budget.IsActive).SingleAsync()));
    }

    private static object ContractPayload(Guid? accountId, Guid? categoryId, string name) => new
    {
        name,
        providerName = "Provider",
        kind = "contract",
        categoryId,
        accountId,
        amount = 29.99m,
        currency = "EUR",
        billingCycle = "monthly",
        interval = 1,
        startDate = "2026-01-01",
        endDate = (string?)null,
        nextDueDate = "2026-09-01",
        isActive = true,
        notes = "authorization test"
    };

    private static object BudgetPayload(Guid? categoryId, string name) => new
    {
        name,
        categoryId,
        amount = 500m,
        currency = "EUR",
        period = "monthly",
        carryOver = false,
        isActive = true,
        startDate = "2026-08-01",
        endDate = (string?)null
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
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid());

        await factory.SeedAsync(async db =>
        {
            foreach (var userId in new[] { scenario.Owner, scenario.OwnerViewer, scenario.Member, scenario.PrivateOwner, scenario.Outside })
            {
                db.Users.Add(new FullWorthUser
                {
                    Id = userId,
                    EmailNormalized = $"{userId:N}@EXAMPLE.COM".ToUpperInvariant(),
                    DisplayName = $"E6 {userId:N}",
                    IsActive = true
                });
            }

            db.FullWorthSpaces.AddRange(
                new FullWorthSpace { Id = scenario.SpaceA, Name = "E6 Space A", BaseCurrency = "EUR" },
                new FullWorthSpace { Id = scenario.SpaceB, Name = "E6 Space B", BaseCurrency = "EUR" });

            db.FullWorthSpaceMembers.AddRange(
                Member(scenario.SpaceA, scenario.Owner, FullWorthSpaceRoles.Owner),
                Member(scenario.SpaceA, scenario.OwnerViewer, FullWorthSpaceRoles.Owner),
                Member(scenario.SpaceA, scenario.Member, FullWorthSpaceRoles.Member),
                Member(scenario.SpaceA, scenario.PrivateOwner, FullWorthSpaceRoles.Owner),
                Member(scenario.SpaceB, scenario.Owner, FullWorthSpaceRoles.Owner));

            db.BankConnections.AddRange(
                Connection(scenario.ConnectionA, scenario.SpaceA),
                Connection(scenario.ConnectionB, scenario.SpaceB));

            db.Accounts.AddRange(
                Account(scenario.AccountA, scenario.SpaceA, scenario.ConnectionA, "E6 Account A"),
                Account(scenario.PrivateAccount, scenario.SpaceA, scenario.ConnectionA, "E6 Private Account"),
                Account(scenario.AccountSpaceB, scenario.SpaceB, scenario.ConnectionB, "E6 Space B Account"));

            db.AccountOwners.AddRange(
                Owner(scenario.AccountA, scenario.Owner, AccountOwnershipTypes.Owner),
                Owner(scenario.AccountA, scenario.OwnerViewer, AccountOwnershipTypes.Viewer),
                Owner(scenario.PrivateAccount, scenario.PrivateOwner, AccountOwnershipTypes.Owner),
                Owner(scenario.AccountSpaceB, scenario.Owner, AccountOwnershipTypes.Owner));

            db.Categories.AddRange(
                new FinanceCategory { Id = scenario.CategoryA, FullWorthSpaceId = scenario.SpaceA, Key = $"e6-a-{scenario.CategoryA:N}", Name = "E6 A" },
                new FinanceCategory { Id = scenario.CategoryB, FullWorthSpaceId = scenario.SpaceB, Key = $"e6-b-{scenario.CategoryB:N}", Name = "E6 B" });

            db.Contracts.AddRange(
                new RecurringContract { Id = scenario.UnlinkedContract, FullWorthSpaceId = scenario.SpaceA, Name = "Unlinked", Amount = 10m, Currency = "EUR", BillingCycle = "monthly" },
                new RecurringContract { Id = scenario.LinkedContract, FullWorthSpaceId = scenario.SpaceA, Name = "Linked", AccountId = scenario.AccountA, CategoryId = scenario.CategoryA, Amount = 20m, Currency = "EUR", BillingCycle = "monthly" },
                new RecurringContract { Id = scenario.PrivateLinkedContract, FullWorthSpaceId = scenario.SpaceA, Name = "Private linked", AccountId = scenario.PrivateAccount, Amount = 30m, Currency = "EUR", BillingCycle = "monthly" },
                new RecurringContract { Id = scenario.CrossSpaceContract, FullWorthSpaceId = scenario.SpaceB, Name = "Space B", AccountId = scenario.AccountSpaceB, Amount = 40m, Currency = "EUR", BillingCycle = "monthly" });

            db.Budgets.AddRange(
                new Budget { Id = scenario.BudgetA, FullWorthSpaceId = scenario.SpaceA, Name = "Budget A", CategoryId = scenario.CategoryA, Amount = 500m, Currency = "EUR", Period = "monthly" },
                new Budget { Id = scenario.BudgetB, FullWorthSpaceId = scenario.SpaceB, Name = "Budget B", CategoryId = scenario.CategoryB, Amount = 600m, Currency = "EUR", Period = "monthly" });

            AddRecurringTransactions(db, scenario.AccountA, "VISIBLE-SUB", "Visible Subscription");
            AddRecurringTransactions(db, scenario.PrivateAccount, "PRIVATE-SUB", "Private Subscription");
            await db.SaveChangesAsync();
        });

        return scenario;
    }

    private static void AddRecurringTransactions(FullWorth.Backend.Data.FullWorthDbContext db, Guid accountId, string prefix, string counterparty)
    {
        for (var month = 5; month <= 7; month++)
        {
            db.Transactions.Add(new FinanceTransaction
            {
                AccountId = accountId,
                ExternalKey = $"{prefix}-{month}",
                Amount = -19.99m,
                Currency = "EUR",
                BookingDate = new DateOnly(2026, month, 5),
                Counterparty = counterparty,
                NormalizedCounterparty = counterparty,
                Description = "Recurring E6 test",
                RawJson = "{}"
            });
        }
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
        InstitutionName = "E6 Bank",
        Country = "DE",
        ProviderSessionId = $"e6-{id:N}",
        Status = "AUTHORIZED"
    };

    private static FinanceAccount Account(Guid id, Guid spaceId, Guid connectionId, string name) => new()
    {
        Id = id,
        FullWorthSpaceId = spaceId,
        BankConnectionId = connectionId,
        Provider = "test",
        IdentificationHash = $"e6-{id:N}",
        ProviderAccountId = $"provider-{id:N}",
        InstitutionName = "E6 Bank",
        DisplayName = name,
        Currency = "EUR"
    };

    private static AccountOwner Owner(Guid accountId, Guid userId, string ownershipType) => new()
    {
        AccountId = accountId,
        UserId = userId,
        OwnershipType = ownershipType
    };

    private sealed record Scenario(
        Guid Owner,
        Guid OwnerViewer,
        Guid Member,
        Guid PrivateOwner,
        Guid Outside,
        Guid SpaceA,
        Guid SpaceB,
        Guid ConnectionA,
        Guid ConnectionB,
        Guid AccountA,
        Guid PrivateAccount,
        Guid AccountSpaceB,
        Guid CategoryA,
        Guid CategoryB,
        Guid UnlinkedContract,
        Guid LinkedContract,
        Guid PrivateLinkedContract,
        Guid CrossSpaceContract,
        Guid BudgetA,
        Guid BudgetB);
}
