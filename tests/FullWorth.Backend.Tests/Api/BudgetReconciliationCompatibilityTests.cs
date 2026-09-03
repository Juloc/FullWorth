using System.Net;
using System.Text.Json;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.Budgets;
using FullWorth.Backend.Modules.Categories;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Api;

public sealed class BudgetReconciliationCompatibilityTests
{
    [Fact]
    public async Task LegacyCategoryBudgetKeepsExactMatchAcrossAllPublicBudgetSurfaces()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var user = Guid.NewGuid();
        var account = Guid.NewGuid();
        var parent = Guid.NewGuid();
        var child = Guid.NewGuid();
        var budget = Guid.NewGuid();

        await SeedBase(factory, user, account);
        await factory.SeedAsync(async db =>
        {
            db.Categories.AddRange(
                Category(parent, "Food"),
                Category(child, "Restaurants", parent));
            db.Budgets.Add(new Budget
            {
                Id = budget,
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                Name = "Legacy Food",
                CategoryId = parent,
                Amount = 100m,
                Currency = "EUR",
                Period = "monthly",
                IsActive = true
            });
            db.Transactions.AddRange(
                Transaction(account, -20m, parent, "PARENT"),
                Transaction(account, -80m, child, "CHILD"));
            await db.SaveChangesAsync();
        });

        Assert.Equal(20m, await DetailSpent(client, user, budget, advanced: false));
        Assert.Equal(20m, await DetailSpent(client, user, budget, advanced: true));
        Assert.Equal(20m, await ListSpent(client, user, budget));
    }

    [Fact]
    public async Task AdvancedCategoryScopeOverridesLegacyCategoryAndRefundsNetIdenticallyEverywhere()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var user = Guid.NewGuid();
        var account = Guid.NewGuid();
        var legacyCategory = Guid.NewGuid();
        var scopedCategory = Guid.NewGuid();
        var budget = Guid.NewGuid();
        var original = Guid.NewGuid();

        await SeedBase(factory, user, account);
        await factory.SeedAsync(async db =>
        {
            db.Categories.AddRange(
                Category(legacyCategory, "Legacy"),
                Category(scopedCategory, "Scoped"));
            db.Budgets.Add(new Budget
            {
                Id = budget,
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                Name = "Advanced",
                CategoryId = legacyCategory,
                Amount = 100m,
                Currency = "EUR",
                Period = "monthly",
                IsActive = true
            });
            db.Transactions.AddRange(
                Transaction(account, -20m, legacyCategory, "LEGACY"),
                Transaction(account, -80m, scopedCategory, "SCOPED", original),
                new FinanceTransaction
                {
                    Id = Guid.NewGuid(),
                    AccountId = account,
                    ExternalKey = $"refund-{Guid.NewGuid():N}",
                    Status = "BOOK",
                    BookingDate = new DateOnly(2026, 8, 20),
                    ValueDate = new DateOnly(2026, 8, 20),
                    Amount = 30m,
                    Currency = "EUR",
                    Counterparty = "SCOPED REFUND",
                    NormalizedCounterparty = "SCOPED REFUND",
                    RefundOfTransactionId = original,
                    RawJson = "{}"
                });
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO "BudgetCategories" ("BudgetId","CategoryId","IncludeDescendants")
VALUES ({budget},{scopedCategory},{false});
""");
        });

        // Scoped spend = 80 expense - 30 linked refund = 50. The legacy-category 20 must not leak in.
        Assert.Equal(50m, await DetailSpent(client, user, budget, advanced: false));
        Assert.Equal(50m, await DetailSpent(client, user, budget, advanced: true));
        Assert.Equal(50m, await ListSpent(client, user, budget));
    }

    private static async Task<decimal> DetailSpent(HttpClient client, Guid user, Guid budget, bool advanced)
    {
        var path = advanced
            ? $"/api/budget-scopes/{budget:D}/status?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}&asOf=2026-08-30"
            : $"/api/budgets/{budget:D}/status?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}&asOf=2026-08-30";
        using var response = await client.SendAsync(UserRequest(HttpMethod.Get, path, user));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("spent").GetDecimal();
    }

    private static async Task<decimal> ListSpent(HttpClient client, Guid user, Guid budget)
    {
        using var response = await client.SendAsync(UserRequest(
            HttpMethod.Get,
            $"/api/analytics/budget-status?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}&year=2026&month=8&currency=EUR",
            user));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var item = doc.RootElement.GetProperty("items").EnumerateArray()
            .Single(row => row.GetProperty("id").GetGuid() == budget);
        return item.GetProperty("spent").GetDecimal();
    }

    private static async Task SeedBase(BackendWebApplicationFactory factory, Guid user, Guid account)
    {
        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = user,
                EmailNormalized = $"{user:N}@EXAMPLE.COM",
                DisplayName = "Budget reconciliation user",
                IsActive = true
            });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                UserId = user,
                Role = FullWorthSpaceRoles.Owner
            });
            db.Accounts.Add(new FinanceAccount
            {
                Id = account,
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                Provider = "manual",
                ProviderAccountId = $"manual-{account:N}",
                IdentificationHash = $"manual-{account:N}",
                InstitutionName = "Manual",
                DisplayName = "Budget account",
                Currency = "EUR",
                IsActive = true
            });
            db.AccountOwners.Add(new AccountOwner
            {
                AccountId = account,
                UserId = user,
                OwnershipType = AccountOwnershipTypes.Owner
            });
            await db.SaveChangesAsync();
        });
    }

    private static FinanceCategory Category(Guid id, string name, Guid? parentId = null) => new()
    {
        Id = id,
        FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
        Key = $"test.{id:N}",
        Name = name,
        ParentId = parentId,
        IsArchived = false
    };

    private static FinanceTransaction Transaction(
        Guid account,
        decimal amount,
        Guid category,
        string counterparty,
        Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        AccountId = account,
        CategoryId = category,
        ExternalKey = $"tx-{Guid.NewGuid():N}",
        Status = "BOOK",
        BookingDate = new DateOnly(2026, 8, 15),
        ValueDate = new DateOnly(2026, 8, 15),
        Amount = amount,
        Currency = "EUR",
        Counterparty = counterparty,
        NormalizedCounterparty = counterparty,
        RawJson = "{}"
    };

    private static HttpRequestMessage UserRequest(HttpMethod method, string path, Guid userId)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        return request;
    }
}
