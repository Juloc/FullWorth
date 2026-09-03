using System.Net;
using System.Text.Json;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.BankConnections;
using FullWorth.Backend.Modules.Budgets;
using FullWorth.Backend.Modules.Categories;
using FullWorth.Backend.Modules.Contracts;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Portfolio;
using FullWorth.Backend.Modules.Purchases;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;

namespace FullWorth.Backend.Tests.Analytics;

public sealed class AnalyticsExportAuthorizationIntegrationTests
{
    [Fact]
    public async Task OverviewAndBudgetStatusExcludeOtherMembersPrivateTransactions()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();

        using var overview = await client.SendAsync(UserRequest(
            $"/api/analytics/overview?fullWorthSpaceId={scenario.SpaceA}&from=2026-08-01&to=2026-08-31&currency=EUR",
            scenario.UserA));
        Assert.Equal(HttpStatusCode.OK, overview.StatusCode);
        using var overviewJson = JsonDocument.Parse(await overview.Content.ReadAsStringAsync());
        Assert.Equal(100m, overviewJson.RootElement.GetProperty("income").GetDecimal());
        Assert.Equal(20m, overviewJson.RootElement.GetProperty("expenses").GetDecimal());
        Assert.Equal(80m, overviewJson.RootElement.GetProperty("net").GetDecimal());

        using var budget = await client.SendAsync(UserRequest(
            $"/api/analytics/budget-status?fullWorthSpaceId={scenario.SpaceA}&year=2026&month=8&currency=EUR",
            scenario.UserA));
        Assert.Equal(HttpStatusCode.OK, budget.StatusCode);
        using var budgetJson = JsonDocument.Parse(await budget.Content.ReadAsStringAsync());
        var item = Assert.Single(budgetJson.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(20m, item.GetProperty("spent").GetDecimal());
        Assert.Equal(80m, item.GetProperty("remaining").GetDecimal());
    }

    [Fact]
    public async Task DashboardExcludesPrivateBalanceAndPrivateLinkedContract()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(UserRequest(
            $"/api/analytics/dashboard?fullWorthSpaceId={scenario.SpaceA}&currency=EUR",
            scenario.UserA));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(100m, json.RootElement.GetProperty("accounts").GetDecimal());
        Assert.Equal(50m, json.RootElement.GetProperty("assets").GetDecimal());
        Assert.Equal(10m, json.RootElement.GetProperty("liabilities").GetDecimal());
        Assert.Equal(140m, json.RootElement.GetProperty("netWorth").GetDecimal());

        var ids = json.RootElement.GetProperty("upcoming").EnumerateArray()
            .Select(item => item.GetProperty("id").GetGuid()).ToArray();
        Assert.Contains(scenario.SharedContract, ids);
        Assert.Contains(scenario.ContractA, ids);
        Assert.DoesNotContain(scenario.PrivateContractB, ids);
    }

    [Fact]
    public async Task AnalyticsRejectsNonMember()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(UserRequest(
            $"/api/analytics/overview?fullWorthSpaceId={scenario.SpaceA}&currency=EUR",
            scenario.Outside));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ExportContainsOnlyAuthorizedResourcesAndNeverLeaksTechnicalFields()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(UserRequest(
            $"/api/export/snapshot?fullWorthSpaceId={scenario.SpaceA}", scenario.UserA));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);

        var accounts = json.RootElement.GetProperty("accounts").EnumerateArray().ToArray();
        Assert.Single(accounts);
        Assert.Equal(scenario.AccountA, accounts[0].GetProperty("id").GetGuid());

        var transactions = json.RootElement.GetProperty("transactions").EnumerateArray().ToArray();
        Assert.Equal(2, transactions.Length);
        Assert.All(transactions, item => Assert.Equal(scenario.AccountA, item.GetProperty("accountId").GetGuid()));

        var purchaseIds = json.RootElement.GetProperty("purchases").EnumerateArray()
            .Select(item => item.GetProperty("id").GetGuid()).ToArray();
        Assert.Contains(scenario.PurchaseA, purchaseIds);
        Assert.Contains(scenario.UnlinkedPurchase, purchaseIds);
        Assert.DoesNotContain(scenario.PrivatePurchaseB, purchaseIds);

        var history = json.RootElement.GetProperty("netWorthHistory").EnumerateArray().ToArray();
        Assert.Single(history);
        Assert.Equal(140m, history[0].GetProperty("netWorth").GetDecimal());

        Assert.DoesNotContain("visible-raw-marker", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private-raw-marker", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rawJson", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("receiptImagePath", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/secret/receipt/path", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("providerSessionId", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret-session", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("userId", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExportRejectsNonMember()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(UserRequest(
            $"/api/export/snapshot?fullWorthSpaceId={scenario.SpaceA}", scenario.Outside));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static HttpRequestMessage UserRequest(string path, Guid userId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        return request;
    }

    private static async Task<Scenario> SeedScenarioAsync(BackendWebApplicationFactory factory)
    {
        var scenario = new Scenario();
        await factory.SeedAsync(async db =>
        {
            foreach (var userId in new[] { scenario.UserA, scenario.UserB, scenario.Outside })
            {
                db.Users.Add(new FullWorthUser
                {
                    Id = userId,
                    EmailNormalized = $"{userId:N}@EXAMPLE.COM".ToUpperInvariant(),
                    DisplayName = $"E8 {userId:N}",
                    IsActive = true
                });
            }

            db.FullWorthSpaces.Add(new FullWorthSpace { Id = scenario.SpaceA, Name = "E8 Space", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.AddRange(
                new FullWorthSpaceMember { FullWorthSpaceId = scenario.SpaceA, UserId = scenario.UserA, Role = FullWorthSpaceRoles.Member },
                new FullWorthSpaceMember { FullWorthSpaceId = scenario.SpaceA, UserId = scenario.UserB, Role = FullWorthSpaceRoles.Member });

            db.BankConnections.Add(new BankConnection
            {
                Id = scenario.ConnectionA,
                FullWorthSpaceId = scenario.SpaceA,
                Provider = "test",
                InstitutionName = "E8 Bank",
                Country = "DE",
                ProviderSessionId = "secret-session",
                Status = "AUTHORIZED"
            });

            db.Accounts.AddRange(
                Account(scenario.AccountA, scenario.SpaceA, scenario.ConnectionA, "Visible A"),
                Account(scenario.PrivateAccountB, scenario.SpaceA, scenario.ConnectionA, "Private B"));
            db.AccountOwners.AddRange(
                new AccountOwner { AccountId = scenario.AccountA, UserId = scenario.UserA, OwnershipType = AccountOwnershipTypes.Owner },
                new AccountOwner { AccountId = scenario.PrivateAccountB, UserId = scenario.UserB, OwnershipType = AccountOwnershipTypes.Owner });
            db.BalanceSnapshots.AddRange(
                new BalanceSnapshot { AccountId = scenario.AccountA, Amount = 100m, Currency = "EUR", BalanceType = "closingAvailable", CapturedAt = DateTimeOffset.UtcNow },
                new BalanceSnapshot { AccountId = scenario.PrivateAccountB, Amount = 900m, Currency = "EUR", BalanceType = "closingAvailable", CapturedAt = DateTimeOffset.UtcNow });

            db.Categories.Add(new FinanceCategory
            {
                Id = scenario.CategoryA,
                FullWorthSpaceId = scenario.SpaceA,
                Key = $"e8-{scenario.CategoryA:N}",
                Name = "General"
            });
            db.Budgets.Add(new Budget
            {
                FullWorthSpaceId = scenario.SpaceA,
                Name = "Monthly budget",
                Amount = 100m,
                Currency = "EUR",
                Period = "monthly",
                IsActive = true
            });
            db.Assets.Add(new Asset { FullWorthSpaceId = scenario.SpaceA, Name = "Shared asset", CurrentValue = 50m, Currency = "EUR", IncludeInNetWorth = true });
            db.Liabilities.Add(new Liability { FullWorthSpaceId = scenario.SpaceA, Name = "Shared liability", CurrentBalance = 10m, Currency = "EUR", IncludeInNetWorth = true });

            db.Contracts.AddRange(
                new RecurringContract { Id = scenario.SharedContract, FullWorthSpaceId = scenario.SpaceA, Name = "Shared contract", Amount = 5m, Currency = "EUR", BillingCycle = "monthly", NextDueDate = DateOnly.FromDateTime(DateTime.Today).AddDays(1), IsActive = true },
                new RecurringContract { Id = scenario.ContractA, FullWorthSpaceId = scenario.SpaceA, AccountId = scenario.AccountA, Name = "Visible linked", Amount = 6m, Currency = "EUR", BillingCycle = "monthly", NextDueDate = DateOnly.FromDateTime(DateTime.Today).AddDays(2), IsActive = true },
                new RecurringContract { Id = scenario.PrivateContractB, FullWorthSpaceId = scenario.SpaceA, AccountId = scenario.PrivateAccountB, Name = "Private linked", Amount = 999m, Currency = "EUR", BillingCycle = "monthly", NextDueDate = DateOnly.FromDateTime(DateTime.Today).AddDays(3), IsActive = true });

            var income = Transaction(scenario.TransactionIncomeA, scenario.AccountA, "income-a", 100m, null, "visible-raw-marker");
            var expense = Transaction(scenario.TransactionExpenseA, scenario.AccountA, "expense-a", -20m, scenario.CategoryA, "visible-raw-marker");
            var privateExpense = Transaction(scenario.PrivateTransactionB, scenario.PrivateAccountB, "expense-b", -900m, scenario.CategoryA, "private-raw-marker");
            db.Transactions.AddRange(income, expense, privateExpense);

            db.Purchases.AddRange(
                new Purchase
                {
                    Id = scenario.PurchaseA,
                    FullWorthSpaceId = scenario.SpaceA,
                    TransactionId = expense.Id,
                    Source = "receipt",
                    Merchant = "Visible Store",
                    TotalAmount = 20m,
                    Currency = "EUR",
                    Status = "confirmed",
                    ReceiptImagePath = "/secret/receipt/path",
                    Items = [new PurchaseItem { Name = "Visible item", CategoryId = scenario.CategoryA, Quantity = 1m, TotalPrice = 20m, Currency = "EUR" }]
                },
                new Purchase
                {
                    Id = scenario.PrivatePurchaseB,
                    FullWorthSpaceId = scenario.SpaceA,
                    TransactionId = privateExpense.Id,
                    Source = "receipt",
                    Merchant = "Private Store",
                    TotalAmount = 900m,
                    Currency = "EUR",
                    Status = "confirmed",
                    ReceiptImagePath = "/secret/private/path"
                },
                new Purchase
                {
                    Id = scenario.UnlinkedPurchase,
                    FullWorthSpaceId = scenario.SpaceA,
                    Source = "receipt",
                    Merchant = "Shared Unlinked",
                    TotalAmount = 3m,
                    Currency = "EUR",
                    Status = "review"
                });

            db.NetWorthSnapshots.AddRange(
                new NetWorthSnapshot { FullWorthSpaceId = scenario.SpaceA, UserId = scenario.UserA, Date = new DateOnly(2026, 8, 19), Currency = "EUR", Accounts = 100m, Assets = 50m, Liabilities = 10m, NetWorth = 140m },
                new NetWorthSnapshot { FullWorthSpaceId = scenario.SpaceA, UserId = scenario.UserB, Date = new DateOnly(2026, 8, 19), Currency = "EUR", Accounts = 900m, Assets = 50m, Liabilities = 10m, NetWorth = 940m });
            await db.SaveChangesAsync();

            // UserA drives the snapshot export, which requires the export.read capability. This test
            // predates the capability layer and relies on account-ownership visibility filtering, so grant
            // the acting member the editor template; per-account visibility is still enforced downstream.
            await CapabilityTestSeeding.GrantEditorAsync(db, scenario.SpaceA, scenario.UserA);
        });
        return scenario;
    }

    private static FinanceAccount Account(Guid id, Guid spaceId, Guid connectionId, string displayName) => new()
    {
        Id = id,
        FullWorthSpaceId = spaceId,
        BankConnectionId = connectionId,
        Provider = "test",
        IdentificationHash = $"e8-{id:N}",
        ProviderAccountId = $"provider-{id:N}",
        InstitutionName = "E8 Bank",
        DisplayName = displayName,
        Currency = "EUR",
        IsActive = true,
        IncludeInNetWorth = true
    };

    private static FinanceTransaction Transaction(Guid id, Guid accountId, string externalKey, decimal amount, Guid? categoryId, string rawMarker) => new()
    {
        Id = id,
        AccountId = accountId,
        CategoryId = categoryId,
        ExternalKey = externalKey,
        Amount = amount,
        Currency = "EUR",
        BookingDate = new DateOnly(2026, 8, 10),
        Counterparty = "E8 Merchant",
        Description = "E8 authorization",
        RawJson = JsonSerializer.Serialize(new { marker = rawMarker })
    };

    private sealed class Scenario
    {
        public Guid UserA { get; } = Guid.NewGuid();
        public Guid UserB { get; } = Guid.NewGuid();
        public Guid Outside { get; } = Guid.NewGuid();
        public Guid SpaceA { get; } = Guid.NewGuid();
        public Guid ConnectionA { get; } = Guid.NewGuid();
        public Guid AccountA { get; } = Guid.NewGuid();
        public Guid PrivateAccountB { get; } = Guid.NewGuid();
        public Guid CategoryA { get; } = Guid.NewGuid();
        public Guid SharedContract { get; } = Guid.NewGuid();
        public Guid ContractA { get; } = Guid.NewGuid();
        public Guid PrivateContractB { get; } = Guid.NewGuid();
        public Guid TransactionIncomeA { get; } = Guid.NewGuid();
        public Guid TransactionExpenseA { get; } = Guid.NewGuid();
        public Guid PrivateTransactionB { get; } = Guid.NewGuid();
        public Guid PurchaseA { get; } = Guid.NewGuid();
        public Guid PrivatePurchaseB { get; } = Guid.NewGuid();
        public Guid UnlinkedPurchase { get; } = Guid.NewGuid();
    }
}
