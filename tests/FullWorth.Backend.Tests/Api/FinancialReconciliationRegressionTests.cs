using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.Budgets;
using FullWorth.Backend.Modules.Categories;
using FullWorth.Backend.Modules.Contracts;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Fx;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Api;

public sealed class FinancialReconciliationRegressionTests
{
    [Fact]
    public async Task AnalyticsAndBudgetUseSameSplitRefundTransferAndFxSemantics()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var userId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var foodId = Guid.NewGuid();
        var shoppingId = Guid.NewGuid();
        var budgetId = Guid.NewGuid();
        var splitPurchaseId = Guid.NewGuid();
        var refundId = Guid.NewGuid();
        var date = new DateOnly(2026, 8, 15);

        await SeedMemberAndAccount(factory, userId, accountId, "Primary");
        await factory.SeedAsync(async db =>
        {
            db.Categories.AddRange(
                new FinanceCategory
                {
                    Id = foodId,
                    FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                    Key = $"test.food.{foodId:N}",
                    Name = "Food",
                    SortOrder = 10
                },
                new FinanceCategory
                {
                    Id = shoppingId,
                    FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                    Key = $"test.shopping.{shoppingId:N}",
                    Name = "Shopping",
                    SortOrder = 20
                });
            db.Budgets.Add(new Budget
            {
                Id = budgetId,
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                Name = "Food budget",
                Amount = 500m,
                Currency = "EUR",
                Period = "monthly",
                IsActive = true
            });
            db.FxRates.Add(new FxRate
            {
                Date = date,
                Currency = "USD",
                Rate = 2m,
                FetchedAt = DateTimeOffset.UtcNow
            });

            db.Transactions.AddRange(
                Tx(splitPurchaseId, accountId, -100m, "EUR", date, "Split supermarket"),
                Tx(refundId, accountId, 50m, "EUR", date.AddDays(1), "Split supermarket refund", refundOf: splitPurchaseId),
                Tx(Guid.NewGuid(), accountId, -100m, "USD", date, "US food", categoryId: foodId),
                Tx(Guid.NewGuid(), accountId, 2000m, "EUR", date, "Salary"),
                Tx(Guid.NewGuid(), accountId, -500m, "EUR", date, "Internal transfer", isTransfer: true),
                Tx(Guid.NewGuid(), accountId, -999m, "EUR", date, "Ignored", isIgnored: true));
            db.TransactionAllocations.AddRange(
                new TransactionAllocation
                {
                    TransactionId = splitPurchaseId,
                    CategoryId = foodId,
                    Amount = -60m
                },
                new TransactionAllocation
                {
                    TransactionId = splitPurchaseId,
                    CategoryId = shoppingId,
                    Amount = -40m
                });
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO "BudgetCategories" ("BudgetId","CategoryId","IncludeDescendants")
VALUES ({budgetId},{foodId},{true})
""");
        });

        using var analyticsRequest = UserRequest(HttpMethod.Post,
            $"/api/analytics/query?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", userId);
        analyticsRequest.Content = JsonContent.Create(new
        {
            measure = "spend",
            dimension = "category",
            from = "2026-08-01",
            to = "2026-08-31",
            granularity = "month",
            accountIds = new[] { accountId },
            accountGroupIds = Array.Empty<Guid>(),
            categoryScopes = Array.Empty<object>(),
            tagIds = Array.Empty<Guid>(),
            normalizedMerchants = Array.Empty<string>(),
            contractIds = Array.Empty<Guid>(),
            currencies = Array.Empty<string>(),
            directions = new[] { "expense" },
            includeTransfers = false,
            includePending = false,
            includeIgnored = false,
            refundMode = "reverse"
        });
        using var analyticsResponse = await client.SendAsync(analyticsRequest);
        Assert.Equal(HttpStatusCode.OK, analyticsResponse.StatusCode);
        using var analytics = JsonDocument.Parse(await analyticsResponse.Content.ReadAsStringAsync());
        Assert.Equal(100m, analytics.RootElement.GetProperty("total").GetDecimal());
        Assert.False(analytics.RootElement.GetProperty("incomplete").GetBoolean());
        var series = analytics.RootElement.GetProperty("series").EnumerateArray()
            .ToDictionary(row => row.GetProperty("key").GetString()!, row => row.GetProperty("value").GetDecimal());
        Assert.Equal(80m, series["Food"]);       // 60 - 30 refund + 50 EUR-equivalent USD spend.
        Assert.Equal(20m, series["Shopping"]);   // 40 - 20 proportional refund.
        Assert.DoesNotContain("Internal transfer", analytics.RootElement.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Ignored", analytics.RootElement.ToString(), StringComparison.Ordinal);

        using var budgetResponse = await client.SendAsync(UserRequest(HttpMethod.Get,
            $"/api/budget-scopes/{budgetId:D}/status?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}&asOf=2026-08-30", userId));
        Assert.Equal(HttpStatusCode.OK, budgetResponse.StatusCode);
        using var budget = JsonDocument.Parse(await budgetResponse.Content.ReadAsStringAsync());
        Assert.Equal(80m, budget.RootElement.GetProperty("spent").GetDecimal());
        Assert.Equal(420m, budget.RootElement.GetProperty("remaining").GetDecimal());
        Assert.False(budget.RootElement.GetProperty("incompleteFx").GetBoolean());
        var contributionText = budget.RootElement.GetProperty("contributing").ToString();
        Assert.Contains("refund", contributionText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SankeyAndNetReconcileRefundsWithoutTreatingThemAsIncome()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var userId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var purchaseId = Guid.NewGuid();
        await SeedMemberAndAccount(factory, userId, accountId, "Primary");

        await factory.SeedAsync(async db =>
        {
            db.Categories.Add(new FinanceCategory
            {
                Id = categoryId,
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                Key = $"test.leisure.{categoryId:N}",
                Name = "Leisure"
            });
            db.Transactions.AddRange(
                Tx(purchaseId, accountId, -100m, "EUR", new DateOnly(2026, 8, 10), "Purchase", categoryId),
                Tx(Guid.NewGuid(), accountId, 40m, "EUR", new DateOnly(2026, 8, 11), "Refund", refundOf: purchaseId),
                Tx(Guid.NewGuid(), accountId, 1000m, "EUR", new DateOnly(2026, 8, 1), "Salary"));
            await db.SaveChangesAsync();
        });

        var payload = new
        {
            measure = "net",
            dimension = "category",
            from = "2026-08-01",
            to = "2026-08-31",
            granularity = "month",
            accountIds = new[] { accountId },
            accountGroupIds = Array.Empty<Guid>(),
            categoryScopes = Array.Empty<object>(),
            tagIds = Array.Empty<Guid>(),
            normalizedMerchants = Array.Empty<string>(),
            contractIds = Array.Empty<Guid>(),
            currencies = Array.Empty<string>(),
            directions = Array.Empty<string>(),
            includeTransfers = false,
            includePending = false,
            includeIgnored = false,
            refundMode = "reverse"
        };
        using var query = UserRequest(HttpMethod.Post,
            $"/api/analytics/query?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", userId);
        query.Content = JsonContent.Create(payload);
        using var queryResponse = await client.SendAsync(query);
        Assert.Equal(HttpStatusCode.OK, queryResponse.StatusCode);
        using var queryJson = JsonDocument.Parse(await queryResponse.Content.ReadAsStringAsync());
        Assert.Equal(940m, queryJson.RootElement.GetProperty("total").GetDecimal());

        using var sankey = UserRequest(HttpMethod.Post,
            $"/api/analytics/sankey?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", userId);
        sankey.Content = JsonContent.Create(payload);
        using var sankeyResponse = await client.SendAsync(sankey);
        Assert.Equal(HttpStatusCode.OK, sankeyResponse.StatusCode);
        using var sankeyJson = JsonDocument.Parse(await sankeyResponse.Content.ReadAsStringAsync());
        Assert.True(sankeyJson.RootElement.GetProperty("reconciles").GetBoolean());
        var json = sankeyJson.RootElement.ToString();
        Assert.Contains("Leisure", json, StringComparison.Ordinal);
        Assert.Contains("940", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CashflowPaceNetsRefundsAndExcludesKnownContractPayments()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var userId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var contractId = Guid.NewGuid();
        var contractPaymentId = Guid.NewGuid();
        var variablePurchaseId = Guid.NewGuid();
        var asOf = new DateOnly(2026, 8, 20);
        await SeedMemberAndAccount(factory, userId, accountId, "Primary");

        await factory.SeedAsync(async db =>
        {
            db.BalanceSnapshots.Add(new BalanceSnapshot
            {
                AccountId = accountId,
                Amount = 1000m,
                Currency = "EUR",
                BalanceType = "closingBooked",
                ReferenceDate = asOf,
                CapturedAt = DateTimeOffset.UtcNow
            });
            db.Contracts.Add(new RecurringContract
            {
                Id = contractId,
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                Name = "Internet",
                Kind = "contract",
                AccountId = accountId,
                Amount = 60m,
                Currency = "EUR",
                BillingCycle = "monthly",
                Interval = 1,
                NextDueDate = new DateOnly(2026, 8, 25),
                IsActive = true
            });
            db.Transactions.AddRange(
                Tx(variablePurchaseId, accountId, -300m, "EUR", new DateOnly(2026, 8, 5), "Variable purchase"),
                Tx(Guid.NewGuid(), accountId, 100m, "EUR", new DateOnly(2026, 8, 7), "Variable refund", refundOf: variablePurchaseId),
                Tx(contractPaymentId, accountId, -60m, "EUR", new DateOnly(2026, 8, 8), "Internet payment"));
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO "CashflowPlanSettings"
("FullWorthSpaceId","HorizonMode","SafetyReserveAmount","SafetyReserveCurrency","IncludePendingIncome","IncludePendingExpenses","VariableForecastMode","UpdatedAt")
VALUES ({FullWorthSpaceDefaults.LegacyId},{"end_of_month"},{0m},{"EUR"},{false},{false},{"pace_blend"},{DateTimeOffset.UtcNow})
ON CONFLICT ("FullWorthSpaceId") DO UPDATE SET
"HorizonMode"='end_of_month',"SafetyReserveAmount"=0,"IncludePendingIncome"=false,"IncludePendingExpenses"=false,"UpdatedAt"={DateTimeOffset.UtcNow}
""");
            await db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO "ContractTransactionLinks"
("Id","FullWorthSpaceId","ContractId","TransactionId","Amount","LinkSource","CreatedAt")
VALUES ({Guid.NewGuid()},{FullWorthSpaceDefaults.LegacyId},{contractId},{contractPaymentId},{60m},{"manual"},{DateTimeOffset.UtcNow})
""");
        });

        using var response = await client.SendAsync(UserRequest(HttpMethod.Get,
            $"/api/cashflow/available?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}&asOf={asOf:yyyy-MM-dd}", userId));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        // 300 variable expense - 100 refund = 200 net variable spend over 30 days.
        // Aug 20..31 inclusive = 12 days => 200 / 30 * 12 = 80 forecast.
        // The known 60 EUR contract payment is excluded from variable pace and appears only as the future fixed cost.
        Assert.Equal(80m, json.RootElement.GetProperty("forecastVariableSpend").GetDecimal());
        Assert.Equal(60m, json.RootElement.GetProperty("expectedFixedCosts").GetDecimal());
        Assert.Equal(860m, json.RootElement.GetProperty("available").GetDecimal());
        Assert.Equal(0m, json.RootElement.GetProperty("pendingVariableSpend").GetDecimal());
    }

    [Fact]
    public async Task AccountFilteredRefundCannotPullOriginalAccountBackIntoReport()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var userId = Guid.NewGuid();
        var originalAccount = Guid.NewGuid();
        var refundAccount = Guid.NewGuid();
        var originalCategory = Guid.NewGuid();
        var refundCategory = Guid.NewGuid();
        var purchaseId = Guid.NewGuid();
        await SeedMemberAndAccount(factory, userId, originalAccount, "Original");
        await SeedAccount(factory, userId, refundAccount, "Refund");

        await factory.SeedAsync(async db =>
        {
            db.Categories.AddRange(
                new FinanceCategory { Id = originalCategory, FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId, Key = $"orig.{originalCategory:N}", Name = "Original category" },
                new FinanceCategory { Id = refundCategory, FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId, Key = $"refund.{refundCategory:N}", Name = "Refund account category" });
            db.Transactions.AddRange(
                Tx(purchaseId, originalAccount, -100m, "EUR", new DateOnly(2026, 8, 1), "Original purchase", originalCategory),
                Tx(Guid.NewGuid(), refundAccount, 40m, "EUR", new DateOnly(2026, 8, 2), "Cross account refund", refundCategory, refundOf: purchaseId));
            await db.SaveChangesAsync();
        });

        using var request = UserRequest(HttpMethod.Post,
            $"/api/analytics/query?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", userId);
        request.Content = JsonContent.Create(new
        {
            measure = "spend",
            dimension = "category",
            from = "2026-08-01",
            to = "2026-08-31",
            granularity = "month",
            accountIds = new[] { refundAccount },
            accountGroupIds = Array.Empty<Guid>(),
            categoryScopes = Array.Empty<object>(),
            tagIds = Array.Empty<Guid>(),
            normalizedMerchants = Array.Empty<string>(),
            contractIds = Array.Empty<Guid>(),
            currencies = Array.Empty<string>(),
            directions = new[] { "expense" },
            includeTransfers = false,
            includePending = false,
            includeIgnored = false,
            refundMode = "reverse"
        });
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(0m, json.RootElement.GetProperty("total").GetDecimal());
        Assert.DoesNotContain("Original category", json.RootElement.ToString(), StringComparison.Ordinal);
    }

    private static FinanceTransaction Tx(
        Guid id,
        Guid accountId,
        decimal amount,
        string currency,
        DateOnly date,
        string label,
        Guid? categoryId = null,
        Guid? refundOf = null,
        bool isTransfer = false,
        bool isIgnored = false) => new()
    {
        Id = id,
        AccountId = accountId,
        CategoryId = categoryId,
        ExternalKey = $"reconciliation:{id:N}",
        Status = "BOOK",
        BookingDate = date,
        ValueDate = date,
        Amount = amount,
        Currency = currency,
        Counterparty = label,
        NormalizedCounterparty = label.ToUpperInvariant(),
        RefundOfTransactionId = refundOf,
        IsTransfer = isTransfer,
        IsIgnored = isIgnored,
        RawJson = "{}"
    };

    private static async Task SeedMemberAndAccount(BackendWebApplicationFactory factory, Guid userId, Guid accountId, string name)
    {
        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = userId,
                EmailNormalized = $"{userId:N}@EXAMPLE.COM",
                DisplayName = "Reconciliation user",
                IsActive = true
            });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                UserId = userId,
                Role = FullWorthSpaceRoles.Member
            });
            await db.SaveChangesAsync();
        });
        await SeedAccount(factory, userId, accountId, name);
    }

    private static async Task SeedAccount(BackendWebApplicationFactory factory, Guid userId, Guid accountId, string name)
    {
        await factory.SeedAsync(async db =>
        {
            db.Accounts.Add(new FinanceAccount
            {
                Id = accountId,
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                Provider = "manual",
                IdentificationHash = $"reconciliation-{accountId:N}",
                ProviderAccountId = $"reconciliation-{accountId:N}",
                InstitutionName = "Test Bank",
                DisplayName = name,
                Currency = "EUR",
                IsActive = true
            });
            db.AccountOwners.Add(new AccountOwner
            {
                AccountId = accountId,
                UserId = userId,
                OwnershipType = AccountOwnershipTypes.Owner
            });
            await db.SaveChangesAsync();
        });
    }

    private static HttpRequestMessage UserRequest(HttpMethod method, string path, Guid userId)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        return request;
    }
}
