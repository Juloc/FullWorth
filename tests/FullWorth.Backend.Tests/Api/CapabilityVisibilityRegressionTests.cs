using System.Net;
using System.Net.Http.Json;
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

public sealed class CapabilityVisibilityRegressionTests
{
    [Fact]
    public async Task IncomeScheduleListDoesNotRevealHiddenAccountSchedule()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var user = Guid.NewGuid();
        var visible = Guid.NewGuid();
        var hidden = Guid.NewGuid();
        await SeedMember(factory, user, editor: false);
        await SeedAccount(factory, visible, user, AccountOwnershipTypes.Viewer, "Visible");
        await SeedAccount(factory, hidden, null, null, "Hidden");

        await factory.SeedAsync(async db =>
        {
            var now = DateTimeOffset.UtcNow;
            await db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO "IncomeSchedules"
("Id","FullWorthSpaceId","Name","AccountId","ExpectedAmount","Currency","Cycle","Interval","ValueMode","AutoDetected","IsActive","CreatedAt","UpdatedAt")
VALUES ({Guid.NewGuid()},{FullWorthSpaceDefaults.LegacyId},{"Visible salary"},{visible},{1000m},{"EUR"},{"monthly"},{1},{"manual"},{false},{true},{now},{now})
""");
            await db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO "IncomeSchedules"
("Id","FullWorthSpaceId","Name","AccountId","ExpectedAmount","Currency","Cycle","Interval","ValueMode","AutoDetected","IsActive","CreatedAt","UpdatedAt")
VALUES ({Guid.NewGuid()},{FullWorthSpaceDefaults.LegacyId},{"Hidden salary"},{hidden},{5000m},{"EUR"},{"monthly"},{1},{"manual"},{false},{true},{now},{now})
""");
        });

        using var response = await client.SendAsync(UserRequest(HttpMethod.Get,
            $"/api/income-schedules?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", user));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Visible salary", body);
        Assert.DoesNotContain("Hidden salary", body);
    }

    [Fact]
    public async Task LegacyBudgetStatusExcludesHiddenAccountTransactionsAndSignalsPartialAccess()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var user = Guid.NewGuid();
        var visible = Guid.NewGuid();
        var hidden = Guid.NewGuid();
        var budgetId = Guid.NewGuid();
        await SeedMember(factory, user, editor: false);
        await SeedAccount(factory, visible, user, AccountOwnershipTypes.Viewer, "Visible");
        await SeedAccount(factory, hidden, null, null, "Hidden");

        await factory.SeedAsync(async db =>
        {
            db.Budgets.Add(new Budget
            {
                Id = budgetId,
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                Name = "Household",
                Amount = 1000m,
                Currency = "EUR",
                Period = "monthly",
                IsActive = true
            });
            db.Transactions.AddRange(
                NewTransaction(visible, -100m, "visible-spend"),
                NewTransaction(hidden, -900m, "hidden-spend"));
            await db.SaveChangesAsync();
        });

        using var response = await client.SendAsync(UserRequest(HttpMethod.Get,
            $"/api/budgets/{budgetId:D}/status?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}&asOf=2026-08-30", user));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(100m, doc.RootElement.GetProperty("spent").GetDecimal());
        Assert.True(doc.RootElement.GetProperty("partialAccess").GetBoolean());
        var json = doc.RootElement.ToString();
        // Budget-status contributions echo the canonical normalized merchant (upper-cased, punctuation
        // collapsed to spaces), so assert against that form. The privacy guarantee itself is enforced by
        // spent=100 and the excluded hidden account never contributing.
        Assert.Contains("VISIBLE SPEND", json);
        Assert.DoesNotContain("HIDDEN SPEND", json);
    }

    [Fact]
    public async Task EditorCanCreateLegacyBudgetWhileViewerCannot()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var editor = Guid.NewGuid();
        var viewer = Guid.NewGuid();
        await SeedMember(factory, editor, editor: true);
        await SeedMember(factory, viewer, editor: false);

        var payload = new
        {
            name = "Groceries",
            categoryId = (Guid?)null,
            amount = 500m,
            currency = "EUR",
            period = "monthly",
            carryOver = false,
            isActive = true,
            startDate = (string?)null,
            endDate = (string?)null
        };

        using var editorRequest = UserRequest(HttpMethod.Post,
            $"/api/budgets?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", editor);
        editorRequest.Content = JsonContent.Create(payload);
        using var editorResponse = await client.SendAsync(editorRequest);
        Assert.Equal(HttpStatusCode.OK, editorResponse.StatusCode);

        using var viewerRequest = UserRequest(HttpMethod.Post,
            $"/api/budgets?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", viewer);
        viewerRequest.Content = JsonContent.Create(payload);
        using var viewerResponse = await client.SendAsync(viewerRequest);
        Assert.Equal(HttpStatusCode.Forbidden, viewerResponse.StatusCode);
    }

    [Fact]
    public async Task EditorContractWriteStillRequiresWritableAccount()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var editor = Guid.NewGuid();
        var writable = Guid.NewGuid();
        var readOnly = Guid.NewGuid();
        await SeedMember(factory, editor, editor: true);
        await SeedAccount(factory, writable, editor, AccountOwnershipTypes.Owner, "Writable");
        await SeedAccount(factory, readOnly, editor, AccountOwnershipTypes.Viewer, "Read only");

        async Task<HttpStatusCode> Create(Guid accountId)
        {
            using var request = UserRequest(HttpMethod.Post,
                $"/api/contracts?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", editor);
            request.Content = JsonContent.Create(new
            {
                name = "Internet",
                providerName = "Provider",
                kind = "contract",
                categoryId = (Guid?)null,
                accountId,
                amount = 39.99m,
                currency = "EUR",
                billingCycle = "monthly",
                interval = 1,
                startDate = (string?)null,
                endDate = (string?)null,
                nextDueDate = (string?)null,
                isActive = true,
                notes = (string?)null
            });
            using var response = await client.SendAsync(request);
            return response.StatusCode;
        }

        Assert.Equal(HttpStatusCode.OK, await Create(writable));
        Assert.Equal(HttpStatusCode.Forbidden, await Create(readOnly));
    }

    [Fact]
    public async Task CategoryEditorCanMaintainCategoriesButCannotCreateGlobalRuleWithPartialAccountAccess()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var editor = Guid.NewGuid();
        var own = Guid.NewGuid();
        var hidden = Guid.NewGuid();
        await SeedMember(factory, editor, editor: true);
        await SeedAccount(factory, own, editor, AccountOwnershipTypes.Owner, "Own");
        await SeedAccount(factory, hidden, null, null, "Hidden");

        using var categoryRequest = UserRequest(HttpMethod.Post,
            $"/api/categories?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", editor);
        categoryRequest.Content = JsonContent.Create(new
        {
            key = $"test.{Guid.NewGuid():N}",
            name = "Editor category",
            parentId = (Guid?)null,
            icon = "tag",
            sortOrder = 500
        });
        using var categoryResponse = await client.SendAsync(categoryRequest);
        Assert.Equal(HttpStatusCode.OK, categoryResponse.StatusCode);
        using var categoryDoc = JsonDocument.Parse(await categoryResponse.Content.ReadAsStringAsync());
        var categoryId = categoryDoc.RootElement.GetProperty("id").GetGuid();

        using var ruleRequest = UserRequest(HttpMethod.Post,
            $"/api/categorization-rules?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", editor);
        ruleRequest.Content = JsonContent.Create(new
        {
            name = "Global merchant rule",
            isEnabled = true,
            priority = 100,
            target = "transaction",
            matchField = "counterparty",
            matchMode = "contains",
            pattern = "REWE",
            direction = "expense",
            minAmount = (decimal?)null,
            maxAmount = (decimal?)null,
            merchantCategoryCode = (string?)null,
            categoryId,
            markAsTransfer = false,
            stopProcessing = true
        });
        using var ruleResponse = await client.SendAsync(ruleRequest);
        Assert.Equal(HttpStatusCode.Forbidden, ruleResponse.StatusCode);
    }

    [Fact]
    public async Task ViewerCannotMutateTransactionEvenWhenAccountOwner()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var viewer = Guid.NewGuid();
        var account = Guid.NewGuid();
        var txId = Guid.NewGuid();
        await SeedMember(factory, viewer, editor: false);
        await SeedAccount(factory, account, viewer, AccountOwnershipTypes.Owner, "Owned but viewer role");
        await factory.SeedAsync(async db =>
        {
            db.Transactions.Add(NewTransaction(account, -10m, "viewer-tx", txId));
            await db.SaveChangesAsync();
        });

        using var request = UserRequest(HttpMethod.Patch,
            $"/api/transactions/{txId:D}/classification?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", viewer);
        request.Content = JsonContent.Create(new
        {
            categoryId = (Guid?)null,
            isIgnored = false,
            isTransfer = false,
            transferPurpose = (string?)null,
            userNote = (string?)null
        });
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task CategorizeOnlyEditorCanChangeCategoryButCannotChangeNote()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var editor = Guid.NewGuid();
        var account = Guid.NewGuid();
        var txId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        await SeedMember(factory, editor, editor: true);
        await SeedAccount(factory, account, editor, AccountOwnershipTypes.Owner, "Editor account");
        await factory.SeedAsync(async db =>
        {
            db.Categories.Add(new FinanceCategory
            {
                Id = categoryId,
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                Key = $"test.{categoryId:N}",
                Name = "Target"
            });
            db.Transactions.Add(NewTransaction(account, -10m, "editor-tx", txId));
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO "FinanceCapabilityGrants" ("FullWorthSpaceId","UserId","Capability","IsAllowed","UpdatedAt")
VALUES ({FullWorthSpaceDefaults.LegacyId},{editor},{"transactions.write"},{false},{DateTimeOffset.UtcNow})
ON CONFLICT ("FullWorthSpaceId","UserId","Capability") DO UPDATE SET "IsAllowed"=false
""");
        });

        using var categorize = UserRequest(HttpMethod.Patch,
            $"/api/transactions/{txId:D}/classification?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", editor);
        categorize.Content = JsonContent.Create(new
        {
            categoryId,
            isIgnored = false,
            isTransfer = false,
            transferPurpose = (string?)null,
            userNote = (string?)null
        });
        using var categorizeResponse = await client.SendAsync(categorize);
        Assert.Equal(HttpStatusCode.NoContent, categorizeResponse.StatusCode);

        using var write = UserRequest(HttpMethod.Patch,
            $"/api/transactions/{txId:D}/classification?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", editor);
        write.Content = JsonContent.Create(new
        {
            categoryId,
            isIgnored = false,
            isTransfer = false,
            transferPurpose = (string?)null,
            userNote = "should be blocked"
        });
        using var writeResponse = await client.SendAsync(write);
        Assert.Equal(HttpStatusCode.Forbidden, writeResponse.StatusCode);
    }

    private static FinanceTransaction NewTransaction(Guid accountId, decimal amount, string label, Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        AccountId = accountId,
        ExternalKey = $"test:{Guid.NewGuid():N}",
        Status = "BOOK",
        BookingDate = new DateOnly(2026, 8, 15),
        ValueDate = new DateOnly(2026, 8, 15),
        Amount = amount,
        Currency = "EUR",
        Counterparty = label,
        NormalizedCounterparty = label.ToUpperInvariant(),
        RawJson = "{}"
    };

    private static async Task SeedMember(BackendWebApplicationFactory factory, Guid userId, bool editor)
    {
        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = userId,
                EmailNormalized = $"{userId:N}@EXAMPLE.COM",
                DisplayName = $"Capability {userId:N}",
                IsActive = true
            });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                UserId = userId,
                Role = "member"
            });
            await db.SaveChangesAsync();
            if (editor)
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO "FinanceMemberRoleTemplates" ("FullWorthSpaceId","UserId","Template","UpdatedAt")
VALUES ({FullWorthSpaceDefaults.LegacyId},{userId},{"editor"},{DateTimeOffset.UtcNow})
ON CONFLICT ("FullWorthSpaceId","UserId") DO UPDATE SET "Template"='editor',"UpdatedAt"={DateTimeOffset.UtcNow}
""");
            }
        });
    }

    private static async Task SeedAccount(
        BackendWebApplicationFactory factory,
        Guid accountId,
        Guid? userId,
        string? ownershipType,
        string name)
    {
        await factory.SeedAsync(async db =>
        {
            db.Accounts.Add(new FinanceAccount
            {
                Id = accountId,
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                Provider = "manual",
                IdentificationHash = $"test-{accountId:N}",
                ProviderAccountId = $"test-{accountId:N}",
                InstitutionName = "Test",
                DisplayName = name,
                Currency = "EUR",
                IsActive = true
            });
            if (userId.HasValue && ownershipType is not null)
            {
                db.AccountOwners.Add(new AccountOwner
                {
                    AccountId = accountId,
                    UserId = userId.Value,
                    OwnershipType = ownershipType
                });
            }
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
