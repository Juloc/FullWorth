using System.Net;
using System.Text.Json;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.BankConnections;
using FullWorth.Backend.Modules.Budgets;
using FullWorth.Backend.Modules.Categories;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;

namespace FullWorth.Backend.Tests.Budgets;

public sealed class BudgetStatusIntegrationTests
{
    [Fact]
    public async Task Status_SumsInWindowCategoryExpensesAndExcludesEverythingElse()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(UserRequest(HttpMethod.Get,
            $"/api/budgets/{scenario.CategoryBudget}/status?fullWorthSpaceId={scenario.Space}&asOf=2026-08-15", scenario.Owner));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        Assert.Equal("2026-08-01", root.GetProperty("periodStart").GetString());
        Assert.Equal("2026-08-31", root.GetProperty("periodEnd").GetString());
        Assert.Equal(500m, root.GetProperty("budgetAmount").GetDecimal());
        Assert.Equal(150m, root.GetProperty("spent").GetDecimal());
        Assert.Equal(350m, root.GetProperty("remaining").GetDecimal());
        Assert.Equal(30m, root.GetProperty("percentUsed").GetDecimal());
        Assert.False(root.GetProperty("partialAccess").GetBoolean());
    }

    [Fact]
    public async Task Status_ForCategorylessBudget_SumsAllVisibleSpaceExpenses()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(UserRequest(HttpMethod.Get,
            $"/api/budgets/{scenario.TotalBudget}/status?fullWorthSpaceId={scenario.Space}&asOf=2026-08-15", scenario.Owner));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(210m, json.RootElement.GetProperty("spent").GetDecimal());
    }

    [Fact]
    public async Task Status_IsReadableByMemberWithAccountViewerGrant()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(UserRequest(HttpMethod.Get,
            $"/api/budgets/{scenario.CategoryBudget}/status?fullWorthSpaceId={scenario.Space}&asOf=2026-08-15", scenario.Member));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(150m, json.RootElement.GetProperty("spent").GetDecimal());
        Assert.False(json.RootElement.GetProperty("partialAccess").GetBoolean());
    }

    [Fact]
    public async Task Status_UnknownOrForeignBudget_Returns404()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();

        using var outsider = await client.SendAsync(UserRequest(HttpMethod.Get,
            $"/api/budgets/{scenario.CategoryBudget}/status?fullWorthSpaceId={scenario.Space}&asOf=2026-08-15", scenario.Outside));
        using var missing = await client.SendAsync(UserRequest(HttpMethod.Get,
            $"/api/budgets/{Guid.NewGuid():D}/status?fullWorthSpaceId={scenario.Space}&asOf=2026-08-15", scenario.Owner));

        Assert.Equal(HttpStatusCode.NotFound, outsider.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    private static HttpRequestMessage UserRequest(HttpMethod method, string path, Guid userId)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        return request;
    }

    private static async Task<Scenario> SeedScenarioAsync(BackendWebApplicationFactory factory)
    {
        var scenario = new Scenario(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid());

        await factory.SeedAsync(async db =>
        {
            foreach (var userId in new[] { scenario.Owner, scenario.Member, scenario.Outside })
            {
                db.Users.Add(new FullWorthUser
                {
                    Id = userId,
                    EmailNormalized = $"{userId:N}@EXAMPLE.COM".ToUpperInvariant(),
                    DisplayName = $"J {userId:N}",
                    IsActive = true
                });
            }

            db.FullWorthSpaces.Add(new FullWorthSpace { Id = scenario.Space, Name = "J Space", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.AddRange(
                new FullWorthSpaceMember { FullWorthSpaceId = scenario.Space, UserId = scenario.Owner, Role = FullWorthSpaceRoles.Owner },
                new FullWorthSpaceMember { FullWorthSpaceId = scenario.Space, UserId = scenario.Member, Role = FullWorthSpaceRoles.Member });

            db.BankConnections.Add(new BankConnection
            {
                Id = scenario.Connection,
                FullWorthSpaceId = scenario.Space,
                Provider = "test",
                InstitutionName = "J Bank",
                Country = "DE",
                ProviderSessionId = $"j-{scenario.Connection:N}",
                Status = "AUTHORIZED"
            });

            db.Accounts.Add(new FinanceAccount
            {
                Id = scenario.Account,
                FullWorthSpaceId = scenario.Space,
                BankConnectionId = scenario.Connection,
                Provider = "test",
                IdentificationHash = $"j-{scenario.Account:N}",
                ProviderAccountId = $"provider-{scenario.Account:N}",
                InstitutionName = "J Bank",
                DisplayName = "J Account",
                Currency = "EUR"
            });
            db.AccountOwners.AddRange(
                new AccountOwner
                {
                    AccountId = scenario.Account,
                    UserId = scenario.Owner,
                    OwnershipType = AccountOwnershipTypes.Owner
                },
                new AccountOwner
                {
                    AccountId = scenario.Account,
                    UserId = scenario.Member,
                    OwnershipType = AccountOwnershipTypes.Viewer
                });

            db.Categories.AddRange(
                new FinanceCategory { Id = scenario.Category, FullWorthSpaceId = scenario.Space, Key = $"j-a-{scenario.Category:N}", Name = "Groceries" },
                new FinanceCategory { Id = scenario.OtherCategory, FullWorthSpaceId = scenario.Space, Key = $"j-o-{scenario.OtherCategory:N}", Name = "Transport" });

            db.Budgets.AddRange(
                new Budget { Id = scenario.CategoryBudget, FullWorthSpaceId = scenario.Space, Name = "Groceries budget", CategoryId = scenario.Category, Amount = 500m, Currency = "EUR", Period = "monthly" },
                new Budget { Id = scenario.TotalBudget, FullWorthSpaceId = scenario.Space, Name = "Total budget", CategoryId = null, Amount = 1000m, Currency = "EUR", Period = "monthly" });

            Add(db, scenario.Account, scenario.Category, -100m, new DateOnly(2026, 8, 3));
            Add(db, scenario.Account, scenario.Category, -50m, new DateOnly(2026, 8, 20));
            Add(db, scenario.Account, scenario.OtherCategory, -60m, new DateOnly(2026, 8, 10));
            Add(db, scenario.Account, scenario.Category, 200m, new DateOnly(2026, 8, 5));
            Add(db, scenario.Account, scenario.Category, -30m, new DateOnly(2026, 8, 6), isIgnored: true);
            Add(db, scenario.Account, scenario.Category, -40m, new DateOnly(2026, 8, 7), isTransfer: true);
            Add(db, scenario.Account, scenario.Category, -70m, new DateOnly(2026, 7, 31));

            await db.SaveChangesAsync();
        });

        return scenario;
    }

    private static void Add(
        FullWorth.Backend.Data.FullWorthDbContext db, Guid accountId, Guid? categoryId, decimal amount, DateOnly bookingDate,
        bool isIgnored = false, bool isTransfer = false) =>
        db.Transactions.Add(new FinanceTransaction
        {
            AccountId = accountId,
            CategoryId = categoryId,
            ExternalKey = $"J-{Guid.NewGuid():N}",
            Amount = amount,
            Currency = "EUR",
            BookingDate = bookingDate,
            IsIgnored = isIgnored,
            IsTransfer = isTransfer,
            RawJson = "{}"
        });

    private sealed record Scenario(
        Guid Owner,
        Guid Member,
        Guid Outside,
        Guid Space,
        Guid Connection,
        Guid Account,
        Guid Category,
        Guid OtherCategory,
        Guid CategoryBudget,
        Guid TotalBudget);
}
