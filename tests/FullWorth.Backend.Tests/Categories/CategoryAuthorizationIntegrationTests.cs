using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FullWorth.Backend.Modules.Categories;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Categories;

public sealed class CategoryAuthorizationIntegrationTests
{
    [Fact]
    public async Task OwnerAndMemberCanReadSpaceCategoriesAndRulesButOutsiderCannot()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();

        foreach (var userId in new[] { scenario.OwnerA, scenario.MemberA })
        {
            using var categories = await client.SendAsync(UserRequest(HttpMethod.Get,
                $"/api/categories?fullWorthSpaceId={scenario.SpaceA}", userId));
            Assert.Equal(HttpStatusCode.OK, categories.StatusCode);
            var categoryBody = await categories.Content.ReadAsStringAsync();
            Assert.Contains(scenario.CategoryA.ToString(), categoryBody, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(scenario.CategoryB.ToString(), categoryBody, StringComparison.OrdinalIgnoreCase);

            using var rules = await client.SendAsync(UserRequest(HttpMethod.Get,
                $"/api/categorization-rules?fullWorthSpaceId={scenario.SpaceA}", userId));
            Assert.Equal(HttpStatusCode.OK, rules.StatusCode);
            var ruleBody = await rules.Content.ReadAsStringAsync();
            Assert.Contains(scenario.RuleA.ToString(), ruleBody, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(scenario.RuleB.ToString(), ruleBody, StringComparison.OrdinalIgnoreCase);
        }

        using var outsider = await client.SendAsync(UserRequest(HttpMethod.Get,
            $"/api/categories?fullWorthSpaceId={scenario.SpaceA}", scenario.Outside));
        Assert.Equal(HttpStatusCode.NotFound, outsider.StatusCode);
    }

    [Fact]
    public async Task VisibleMemberCannotCreateCategoryOrRule()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();

        using var category = await client.SendAsync(UserRequest(HttpMethod.Post,
            $"/api/categories?fullWorthSpaceId={scenario.SpaceA}", scenario.MemberA,
            new CategoryWrite($"member-{Guid.NewGuid():N}", "Member category", null, null, 100)));
        Assert.Equal(HttpStatusCode.Forbidden, category.StatusCode);

        using var rule = await client.SendAsync(UserRequest(HttpMethod.Post,
            $"/api/categorization-rules?fullWorthSpaceId={scenario.SpaceA}", scenario.MemberA,
            RulePayload(scenario.CategoryA, "Member rule")));
        Assert.Equal(HttpStatusCode.Forbidden, rule.StatusCode);
    }

    [Fact]
    public async Task OwnerCreatesCategoryOnlyInsideOwnedVisibleSpace()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();
        var key = $"owner-{Guid.NewGuid():N}";

        using var success = await client.SendAsync(UserRequest(HttpMethod.Post,
            $"/api/categories?fullWorthSpaceId={scenario.SpaceA}", scenario.OwnerA,
            new CategoryWrite(key, "Owner category", scenario.CategoryA, "wallet", 123)));
        Assert.Equal(HttpStatusCode.OK, success.StatusCode);
        var created = await success.Content.ReadFromJsonAsync<FinanceCategory>();
        Assert.NotNull(created);
        Assert.Equal(scenario.SpaceA, created.FullWorthSpaceId);
        Assert.Equal(scenario.CategoryA, created.ParentId);

        using var foreignParent = await client.SendAsync(UserRequest(HttpMethod.Post,
            $"/api/categories?fullWorthSpaceId={scenario.SpaceA}", scenario.OwnerA,
            new CategoryWrite($"foreign-{Guid.NewGuid():N}", "Foreign parent", scenario.CategoryB, null, 124)));
        Assert.Equal(HttpStatusCode.NotFound, foreignParent.StatusCode);

        using var foreignSpace = await client.SendAsync(UserRequest(HttpMethod.Post,
            $"/api/categories?fullWorthSpaceId={scenario.SpaceB}", scenario.MemberA,
            new CategoryWrite($"space-{Guid.NewGuid():N}", "Wrong space", null, null, 125)));
        Assert.Equal(HttpStatusCode.NotFound, foreignSpace.StatusCode);
    }

    [Fact]
    public async Task OwnerRuleWriteRequiresSameSpaceCategoryAndRule()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();

        using var create = await client.SendAsync(UserRequest(HttpMethod.Post,
            $"/api/categorization-rules?fullWorthSpaceId={scenario.SpaceA}", scenario.OwnerA,
            RulePayload(scenario.CategoryA, "Owner rule")));
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        var created = await create.Content.ReadFromJsonAsync<CategorizationRule>();
        Assert.NotNull(created);
        Assert.Equal(scenario.SpaceA, created.FullWorthSpaceId);
        Assert.Equal(scenario.CategoryA, created.CategoryId);

        using var foreignCategory = await client.SendAsync(UserRequest(HttpMethod.Post,
            $"/api/categorization-rules?fullWorthSpaceId={scenario.SpaceA}", scenario.OwnerA,
            RulePayload(scenario.CategoryB, "Foreign category")));
        Assert.Equal(HttpStatusCode.NotFound, foreignCategory.StatusCode);

        using var foreignRule = await client.SendAsync(UserRequest(HttpMethod.Put,
            $"/api/categorization-rules/{scenario.RuleB}?fullWorthSpaceId={scenario.SpaceA}", scenario.OwnerA,
            RulePayload(scenario.CategoryA, "Foreign rule")));
        Assert.Equal(HttpStatusCode.NotFound, foreignRule.StatusCode);
    }

    [Fact]
    public async Task MemberCannotUpdateVisibleRuleButOwnerCan()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();

        using var member = await client.SendAsync(UserRequest(HttpMethod.Put,
            $"/api/categorization-rules/{scenario.RuleA}?fullWorthSpaceId={scenario.SpaceA}", scenario.MemberA,
            RulePayload(scenario.CategoryA, "Member attempted update")));
        Assert.Equal(HttpStatusCode.Forbidden, member.StatusCode);

        using var owner = await client.SendAsync(UserRequest(HttpMethod.Put,
            $"/api/categorization-rules/{scenario.RuleA}?fullWorthSpaceId={scenario.SpaceA}", scenario.OwnerA,
            RulePayload(scenario.CategoryA, "Owner updated rule")));
        Assert.Equal(HttpStatusCode.OK, owner.StatusCode);

        await factory.SeedAsync(async db =>
        {
            var stored = await db.CategorizationRules.AsNoTracking().SingleAsync(x => x.Id == scenario.RuleA);
            Assert.Equal("Owner updated rule", stored.Name);
            Assert.Equal(scenario.SpaceA, stored.FullWorthSpaceId);
        });
    }

    private static object RulePayload(Guid categoryId, string name) => new
    {
        name,
        isEnabled = true,
        priority = 100,
        target = "transaction",
        matchField = "combined",
        matchMode = "contains",
        pattern = "test",
        direction = "any",
        minAmount = (decimal?)null,
        maxAmount = (decimal?)null,
        merchantCategoryCode = (string?)null,
        categoryId,
        markAsTransfer = false,
        stopProcessing = true
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
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid());

        await factory.SeedAsync(async db =>
        {
            foreach (var userId in new[] { scenario.OwnerA, scenario.MemberA, scenario.Outside })
            {
                db.Users.Add(new FullWorthUser
                {
                    Id = userId,
                    EmailNormalized = $"{userId:N}@EXAMPLE.COM".ToUpperInvariant(),
                    DisplayName = $"Category user {userId:N}",
                    IsActive = true
                });
            }

            db.FullWorthSpaces.AddRange(
                new FullWorthSpace { Id = scenario.SpaceA, Name = "Category Space A", BaseCurrency = "EUR" },
                new FullWorthSpace { Id = scenario.SpaceB, Name = "Category Space B", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.AddRange(
                new FullWorthSpaceMember { FullWorthSpaceId = scenario.SpaceA, UserId = scenario.OwnerA, Role = FullWorthSpaceRoles.Owner },
                new FullWorthSpaceMember { FullWorthSpaceId = scenario.SpaceA, UserId = scenario.MemberA, Role = FullWorthSpaceRoles.Member },
                new FullWorthSpaceMember { FullWorthSpaceId = scenario.SpaceB, UserId = scenario.Outside, Role = FullWorthSpaceRoles.Owner });

            db.Categories.AddRange(
                new FinanceCategory { Id = scenario.CategoryA, FullWorthSpaceId = scenario.SpaceA, Key = $"cat-a-{scenario.CategoryA:N}", Name = "Category A" },
                new FinanceCategory { Id = scenario.CategoryB, FullWorthSpaceId = scenario.SpaceB, Key = $"cat-b-{scenario.CategoryB:N}", Name = "Category B" });
            db.CategorizationRules.AddRange(
                new CategorizationRule
                {
                    Id = scenario.RuleA,
                    FullWorthSpaceId = scenario.SpaceA,
                    Name = "Rule A",
                    CategoryId = scenario.CategoryA,
                    MatchField = "combined",
                    MatchMode = "contains",
                    Pattern = "a",
                    Direction = "any"
                },
                new CategorizationRule
                {
                    Id = scenario.RuleB,
                    FullWorthSpaceId = scenario.SpaceB,
                    Name = "Rule B",
                    CategoryId = scenario.CategoryB,
                    MatchField = "combined",
                    MatchMode = "contains",
                    Pattern = "b",
                    Direction = "any"
                });
            await db.SaveChangesAsync();
        });

        return scenario;
    }

    private sealed record Scenario(
        Guid OwnerA,
        Guid MemberA,
        Guid Outside,
        Guid SpaceA,
        Guid SpaceB,
        Guid CategoryA,
        Guid CategoryB,
        Guid RuleA,
        Guid RuleB);
}
