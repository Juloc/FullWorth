using System.Net;
using System.Net.Http.Json;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.Categories;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Api;

public sealed class CategoryIntelligenceCapabilityTests
{
    [Fact]
    public async Task CategorizeCapability_AllowsTagsButViewerWithoutCapabilityIsForbidden()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var editor = Guid.NewGuid();
        var viewer = Guid.NewGuid();
        await SeedMember(factory, editor);
        await SeedMember(factory, viewer);
        await Grant(factory, editor, "transactions.categorize", true);

        using var create = UserRequest(HttpMethod.Post,
            $"/api/category-intelligence/tags?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", editor);
        create.Content = JsonContent.Create(new { name = "Urlaub", color = "#336699" });
        using var created = await client.SendAsync(create);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        using var denied = UserRequest(HttpMethod.Post,
            $"/api/category-intelligence/tags?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", viewer);
        denied.Content = JsonContent.Create(new { name = "Privat", color = "#123456" });
        using var deniedResponse = await client.SendAsync(denied);
        Assert.Equal(HttpStatusCode.Forbidden, deniedResponse.StatusCode);
    }

    [Fact]
    public async Task BulkCategoryNeedsCategorizeButIgnoreNeedsTransactionWrite()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var editor = Guid.NewGuid();
        var account = Guid.NewGuid();
        var category = Guid.NewGuid();
        var transaction = Guid.NewGuid();
        await SeedMember(factory, editor);
        await Grant(factory, editor, "transactions.categorize", true);
        await SeedAccountAndTransaction(factory, editor, account, transaction, category);

        using var categorize = UserRequest(HttpMethod.Post,
            $"/api/category-intelligence/bulk?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", editor);
        categorize.Content = JsonContent.Create(new
        {
            transactionIds = new[] { transaction },
            updateCategory = true,
            categoryId = category
        });
        using var categorized = await client.SendAsync(categorize);
        Assert.Equal(HttpStatusCode.OK, categorized.StatusCode);

        using var ignore = UserRequest(HttpMethod.Post,
            $"/api/category-intelligence/bulk?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", editor);
        ignore.Content = JsonContent.Create(new
        {
            transactionIds = new[] { transaction },
            isIgnored = true
        });
        using var ignored = await client.SendAsync(ignore);
        Assert.Equal(HttpStatusCode.Forbidden, ignored.StatusCode);

        await factory.SeedAsync(async db =>
        {
            var row = await db.Transactions.AsNoTracking().SingleAsync(x => x.Id == transaction);
            Assert.Equal(category, row.CategoryId);
            Assert.False(row.IsIgnored);
        });
    }

    [Fact]
    public async Task ExistingLearningOnlyTouchesWritableAccounts_ButFutureLearningNeedsFullCoverage()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var editor = Guid.NewGuid();
        var ownAccount = Guid.NewGuid();
        var hiddenAccount = Guid.NewGuid();
        var ownTransaction = Guid.NewGuid();
        var hiddenTransaction = Guid.NewGuid();
        var category = Guid.NewGuid();
        await SeedMember(factory, editor);
        await Grant(factory, editor, "transactions.categorize", true);

        await factory.SeedAsync(async db =>
        {
            db.Categories.Add(new FinanceCategory
            {
                Id = category,
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                Key = $"test-{category:N}",
                Name = "Lebensmittel"
            });
            db.Accounts.AddRange(
                ManualAccount(ownAccount, "Eigenes Konto"),
                ManualAccount(hiddenAccount, "Verstecktes Konto"));
            db.AccountOwners.Add(new AccountOwner
            {
                AccountId = ownAccount,
                UserId = editor,
                OwnershipType = AccountOwnershipTypes.Owner
            });
            db.Transactions.AddRange(
                Transaction(ownTransaction, ownAccount, "REWE"),
                Transaction(hiddenTransaction, hiddenAccount, "REWE"));
            await db.SaveChangesAsync();
        });

        using var existing = UserRequest(HttpMethod.Post,
            $"/api/category-intelligence/learn?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", editor);
        existing.Content = JsonContent.Create(new { transactionId = ownTransaction, categoryId = category, scope = "existing" });
        using var existingResponse = await client.SendAsync(existing);
        Assert.Equal(HttpStatusCode.OK, existingResponse.StatusCode);

        await factory.SeedAsync(async db =>
        {
            var own = await db.Transactions.AsNoTracking().SingleAsync(x => x.Id == ownTransaction);
            var hidden = await db.Transactions.AsNoTracking().SingleAsync(x => x.Id == hiddenTransaction);
            Assert.Equal(category, own.CategoryId);
            Assert.Null(hidden.CategoryId);
        });

        using var future = UserRequest(HttpMethod.Post,
            $"/api/category-intelligence/learn?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", editor);
        future.Content = JsonContent.Create(new { transactionId = ownTransaction, categoryId = category, scope = "future" });
        using var futureResponse = await client.SendAsync(future);
        Assert.Equal(HttpStatusCode.Forbidden, futureResponse.StatusCode);
        await factory.SeedAsync(async db =>
        {
            Assert.False(await db.CategorizationRules.AnyAsync(x =>
                x.FullWorthSpaceId == FullWorthSpaceDefaults.LegacyId && x.Pattern == "REWE"));
        });
    }

    [Fact]
    public async Task FutureLearningWorksAfterEditorOwnsEveryActiveAccount()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var editor = Guid.NewGuid();
        var account = Guid.NewGuid();
        var transaction = Guid.NewGuid();
        var category = Guid.NewGuid();
        await SeedMember(factory, editor);
        await Grant(factory, editor, "transactions.categorize", true);
        await SeedAccountAndTransaction(factory, editor, account, transaction, category, "DM");

        using var learn = UserRequest(HttpMethod.Post,
            $"/api/category-intelligence/learn?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", editor);
        learn.Content = JsonContent.Create(new { transactionId = transaction, categoryId = category, scope = "future" });
        using var response = await client.SendAsync(learn);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await factory.SeedAsync(async db =>
        {
            Assert.True(await db.CategorizationRules.AnyAsync(x =>
                x.FullWorthSpaceId == FullWorthSpaceDefaults.LegacyId && x.Pattern == "DM" && x.CategoryId == category));
        });
    }

    private static async Task SeedMember(BackendWebApplicationFactory factory, Guid userId)
    {
        await factory.SeedAsync(async db =>
        {
            if (!await db.Users.AnyAsync(x => x.Id == userId))
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
                Role = FullWorthSpaceRoles.Member
            });
            await db.SaveChangesAsync();
        });
    }

    private static Task Grant(BackendWebApplicationFactory factory, Guid userId, string capability, bool allowed) =>
        factory.SeedAsync(db => db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO "FinanceCapabilityGrants" ("FullWorthSpaceId","UserId","Capability","IsAllowed","UpdatedAt")
VALUES ({FullWorthSpaceDefaults.LegacyId},{userId},{capability},{allowed},{DateTimeOffset.UtcNow})
ON CONFLICT ("FullWorthSpaceId","UserId","Capability") DO UPDATE SET "IsAllowed"={allowed},"UpdatedAt"={DateTimeOffset.UtcNow}
"""));

    private static async Task SeedAccountAndTransaction(
        BackendWebApplicationFactory factory, Guid userId, Guid accountId, Guid transactionId, Guid categoryId,
        string merchant = "REWE")
    {
        await factory.SeedAsync(async db =>
        {
            db.Categories.Add(new FinanceCategory
            {
                Id = categoryId,
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                Key = $"test-{categoryId:N}",
                Name = "Test Category"
            });
            db.Accounts.Add(ManualAccount(accountId, "Writable"));
            db.AccountOwners.Add(new AccountOwner
            {
                AccountId = accountId,
                UserId = userId,
                OwnershipType = AccountOwnershipTypes.Owner
            });
            db.Transactions.Add(Transaction(transactionId, accountId, merchant));
            await db.SaveChangesAsync();
        });
    }

    private static FinanceAccount ManualAccount(Guid id, string name) => new()
    {
        Id = id,
        FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
        Provider = "manual",
        IdentificationHash = $"manual-{id:N}",
        ProviderAccountId = $"manual-{id:N}",
        InstitutionName = "Manual",
        DisplayName = name,
        Currency = "EUR",
        IsActive = true
    };

    private static FinanceTransaction Transaction(Guid id, Guid accountId, string merchant) => new()
    {
        Id = id,
        AccountId = accountId,
        ExternalKey = $"test-{id:N}",
        Status = "BOOK",
        BookingDate = new DateOnly(2026, 8, 20),
        ValueDate = new DateOnly(2026, 8, 20),
        Amount = -20m,
        Currency = "EUR",
        Counterparty = merchant,
        NormalizedCounterparty = merchant,
        CategorizationSource = "none",
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
