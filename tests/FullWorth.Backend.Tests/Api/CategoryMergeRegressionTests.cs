using System.Net;
using System.Net.Http.Json;
using FullWorth.Backend.Modules.Budgets;
using FullWorth.Backend.Modules.Categories;
using FullWorth.Backend.Modules.Contracts;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Purchases;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Api;

public sealed class CategoryMergeRegressionTests
{
    [Fact]
    public async Task MergeReassignsReferencesAndCollapsesDuplicateBudgetScope()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var owner = Guid.NewGuid();
        var source = Guid.NewGuid();
        var target = Guid.NewGuid();
        var budget = Guid.NewGuid();
        var product = Guid.NewGuid();

        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = owner,
                EmailNormalized = $"{owner:N}@EXAMPLE.COM",
                DisplayName = "Category merge owner",
                IsActive = true
            });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                UserId = owner,
                Role = FullWorthSpaceRoles.Owner
            });
            db.Categories.AddRange(
                new FinanceCategory
                {
                    Id = source,
                    FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                    Key = "test.merge.source",
                    Name = "Merge source",
                    SortOrder = 10
                },
                new FinanceCategory
                {
                    Id = target,
                    FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                    Key = "test.merge.target",
                    Name = "Merge target",
                    SortOrder = 20
                });
            db.Budgets.Add(new Budget
            {
                Id = budget,
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                Name = "Merge budget",
                CategoryId = source,
                Amount = 500m,
                Currency = "EUR",
                Period = "monthly"
            });
            db.Contracts.Add(new RecurringContract
            {
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                Name = "Merge contract",
                CategoryId = source,
                Amount = 20m,
                Currency = "EUR"
            });
            db.CategorizationRules.Add(new CategorizationRule
            {
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                Name = "Merge rule",
                CategoryId = source,
                Target = "transaction",
                MatchField = "combined",
                MatchMode = "contains",
                Pattern = "merge",
                Direction = "expense"
            });
            // Canonical product carrying a default category: merging the source category must reassign it
            // to the target. (The legacy per-alias category concept was removed by the Products/Articles
            // unification, so aliases no longer carry a category.)
            db.Products.Add(new Product
            {
                Id = product,
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                CanonicalName = "Merge product",
                DefaultCategoryId = source
            });
            await db.SaveChangesAsync();

            await db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO "BudgetCategories" ("BudgetId","CategoryId","IncludeDescendants")
VALUES ({budget},{source},{false}),({budget},{target},{true});
""");
        });

        using var request = UserRequest(HttpMethod.Post,
            $"/api/category-merge/{source:D}?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", owner);
        request.Content = JsonContent.Create(new { targetCategoryId = target, deleteSource = false });
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await factory.SeedAsync(async db =>
        {
            var sourceRow = await db.Categories.AsNoTracking().SingleAsync(category => category.Id == source);
            Assert.True(sourceRow.IsArchived);
            Assert.Equal(target, (await db.Budgets.AsNoTracking().SingleAsync(row => row.Id == budget)).CategoryId);
            Assert.All(await db.Contracts.AsNoTracking().Where(row => row.Name == "Merge contract").ToListAsync(), row => Assert.Equal(target, row.CategoryId));
            Assert.All(await db.CategorizationRules.AsNoTracking().Where(row => row.Name == "Merge rule").ToListAsync(), row => Assert.Equal(target, row.CategoryId));

            var scope = await db.Database.SqlQueryRaw<BudgetScopeRow>(
                "SELECT \"CategoryId\" AS \"CategoryId\", \"IncludeDescendants\" AS \"IncludeDescendants\" FROM \"BudgetCategories\" WHERE \"BudgetId\"={0}", budget).ToListAsync();
            var only = Assert.Single(scope);
            Assert.Equal(target, only.CategoryId);
            Assert.True(only.IncludeDescendants);

            var productCategory = (await db.Products.AsNoTracking().SingleAsync(row => row.Id == product)).DefaultCategoryId;
            Assert.Equal(target, productCategory);
        });
    }

    [Fact]
    public async Task ActiveChildBlocksMergeWithoutChangingSource()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var owner = Guid.NewGuid();
        var source = Guid.NewGuid();
        var target = Guid.NewGuid();

        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = owner,
                EmailNormalized = $"{owner:N}@EXAMPLE.COM",
                DisplayName = "Category merge owner",
                IsActive = true
            });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                UserId = owner,
                Role = FullWorthSpaceRoles.Owner
            });
            db.Categories.AddRange(
                new FinanceCategory { Id = source, FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId, Key = "merge.parent", Name = "Parent" },
                new FinanceCategory { Id = target, FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId, Key = "merge.target2", Name = "Target" },
                new FinanceCategory { FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId, Key = "merge.child", Name = "Child", ParentId = source });
            await db.SaveChangesAsync();
        });

        using var request = UserRequest(HttpMethod.Post,
            $"/api/category-merge/{source:D}?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", owner);
        request.Content = JsonContent.Create(new { targetCategoryId = target, deleteSource = false });
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        await factory.SeedAsync(async db =>
            Assert.False((await db.Categories.AsNoTracking().SingleAsync(category => category.Id == source)).IsArchived));
    }

    private sealed class BudgetScopeRow
    {
        public Guid CategoryId { get; set; }
        public bool IncludeDescendants { get; set; }
    }

    private static HttpRequestMessage UserRequest(HttpMethod method, string path, Guid userId)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        return request;
    }
}
