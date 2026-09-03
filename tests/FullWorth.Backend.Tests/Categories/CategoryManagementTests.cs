using System.Net;
using System.Net.Http.Json;
using FullWorth.Backend.Modules.Categories;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Tests.Infrastructure;
using Xunit;

namespace FullWorth.Backend.Tests.Categories;

public sealed class CategoryManagementTests
{
    [Fact]
    public async Task OwnerCanReparentCategory()
    {
        using var scenario = await SeedScenarioAsync();
        using var client = scenario.Factory.CreateClient();

        // Move B (child of A) to be a child of C.
        using var reparent = await client.SendAsync(scenario.Request(HttpMethod.Put, scenario.ChildB, scenario.Owner,
            new { name = "Groceries", parentId = scenario.RootC, icon = (string?)null, sortOrder = (int?)null }));
        Assert.Equal(HttpStatusCode.OK, reparent.StatusCode);

        var categories = await ListAsync(client, scenario, includeArchived: false);
        Assert.Contains(categories, c => c.Id == scenario.ChildB && c.ParentId == scenario.RootC);
    }

    [Fact]
    public async Task ReparentingUnderOwnDescendantIsRejected()
    {
        using var scenario = await SeedScenarioAsync();
        using var client = scenario.Factory.CreateClient();

        // A is the parent of B. Moving A under B (its descendant) must be rejected as a cycle.
        using var cycle = await client.SendAsync(scenario.Request(HttpMethod.Put, scenario.ParentA, scenario.Owner,
            new { name = "Food", parentId = scenario.ChildB, icon = (string?)null, sortOrder = (int?)null }));
        Assert.Equal(HttpStatusCode.BadRequest, cycle.StatusCode);

        // Making a category its own parent is likewise rejected.
        using var self = await client.SendAsync(scenario.Request(HttpMethod.Put, scenario.ParentA, scenario.Owner,
            new { name = "Food", parentId = scenario.ParentA, icon = (string?)null, sortOrder = (int?)null }));
        Assert.Equal(HttpStatusCode.BadRequest, self.StatusCode);
    }

    [Fact]
    public async Task ArchiveRejectsActiveChildrenThenHidesFromDefaultList()
    {
        using var scenario = await SeedScenarioAsync();
        using var client = scenario.Factory.CreateClient();

        // A still has active child B, so archiving A is rejected.
        using var archiveParent = await client.SendAsync(scenario.Request(HttpMethod.Delete, scenario.ParentA, scenario.Owner));
        Assert.Equal(HttpStatusCode.BadRequest, archiveParent.StatusCode);

        // Archiving the leaf B succeeds.
        using var archiveChild = await client.SendAsync(scenario.Request(HttpMethod.Delete, scenario.ChildB, scenario.Owner));
        Assert.Equal(HttpStatusCode.NoContent, archiveChild.StatusCode);

        var visible = await ListAsync(client, scenario, includeArchived: false);
        Assert.DoesNotContain(visible, c => c.Id == scenario.ChildB);
        Assert.Contains(visible, c => c.Id == scenario.ParentA);

        var all = await ListAsync(client, scenario, includeArchived: true);
        Assert.Contains(all, c => c.Id == scenario.ChildB && c.IsArchived);
    }

    [Fact]
    public async Task RestoreBringsBackAnArchivedCategoryButNotUnderAnArchivedParent()
    {
        using var scenario = await SeedScenarioAsync();
        using var client = scenario.Factory.CreateClient();

        // Archive the leaf B, then archive its now-childless parent A.
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(scenario.Request(HttpMethod.Delete, scenario.ChildB, scenario.Owner))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(scenario.Request(HttpMethod.Delete, scenario.ParentA, scenario.Owner))).StatusCode);

        // Restoring B while its parent A is still archived is refused — it would resurface under an invisible parent.
        using var earlyRestore = await client.SendAsync(scenario.Request(HttpMethod.Post, $"/api/categories/{scenario.ChildB}/restore?fullWorthSpaceId={scenario.Space}", scenario.Owner));
        Assert.Equal(HttpStatusCode.BadRequest, earlyRestore.StatusCode);

        // Restore the parent first, then B.
        using var restoreParent = await client.SendAsync(scenario.Request(HttpMethod.Post, $"/api/categories/{scenario.ParentA}/restore?fullWorthSpaceId={scenario.Space}", scenario.Owner));
        Assert.Equal(HttpStatusCode.OK, restoreParent.StatusCode);
        using var restoreChild = await client.SendAsync(scenario.Request(HttpMethod.Post, $"/api/categories/{scenario.ChildB}/restore?fullWorthSpaceId={scenario.Space}", scenario.Owner));
        Assert.Equal(HttpStatusCode.OK, restoreChild.StatusCode);

        // Both are visible again on the default (non-archived) list.
        var visible = await ListAsync(client, scenario, includeArchived: false);
        Assert.Contains(visible, c => c.Id == scenario.ParentA && !c.IsArchived);
        Assert.Contains(visible, c => c.Id == scenario.ChildB && !c.IsArchived);

        // A member cannot restore.
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(scenario.Request(HttpMethod.Delete, scenario.RootC, scenario.Owner))).StatusCode);
        using var memberRestore = await client.SendAsync(scenario.Request(HttpMethod.Post, $"/api/categories/{scenario.RootC}/restore?fullWorthSpaceId={scenario.Space}", scenario.Member));
        Assert.Equal(HttpStatusCode.Forbidden, memberRestore.StatusCode);
    }

    [Fact]
    public async Task NonOwnerCannotManageAndUnknownIsNotFound()
    {
        using var scenario = await SeedScenarioAsync();
        using var client = scenario.Factory.CreateClient();

        using var asMember = await client.SendAsync(scenario.Request(HttpMethod.Put, scenario.RootC, scenario.Member,
            new { name = "Renamed", parentId = (Guid?)null, icon = (string?)null, sortOrder = (int?)null }));
        Assert.Equal(HttpStatusCode.Forbidden, asMember.StatusCode);

        using var unknown = await client.SendAsync(scenario.Request(HttpMethod.Put, Guid.NewGuid(), scenario.Owner,
            new { name = "Nope", parentId = (Guid?)null, icon = (string?)null, sortOrder = (int?)null }));
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    private static async Task<List<CategoryView>> ListAsync(HttpClient client, Scenario scenario, bool includeArchived)
    {
        var path = $"/api/categories?fullWorthSpaceId={scenario.Space}&includeArchived={(includeArchived ? "true" : "false")}";
        using var response = await client.SendAsync(scenario.Request(HttpMethod.Get, path, scenario.Owner));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<List<CategoryView>>())!;
    }

    private static async Task<Scenario> SeedScenarioAsync()
    {
        var factory = new BackendWebApplicationFactory();
        var scenario = new Scenario(factory, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        await factory.SeedFullWorthUserAsync(scenario.Owner);
        await factory.SeedFullWorthUserAsync(scenario.Member);
        await factory.SeedAsync(async db =>
        {
            var now = DateTimeOffset.UtcNow;
            db.Set<FullWorthSpace>().Add(new FullWorthSpace { Id = scenario.Space, Name = "Space", BaseCurrency = "EUR", CreatedAt = now, UpdatedAt = now });
            db.Set<FullWorthSpaceMember>().Add(new FullWorthSpaceMember { FullWorthSpaceId = scenario.Space, UserId = scenario.Owner, Role = FullWorthSpaceRoles.Owner, JoinedAt = now });
            db.Set<FullWorthSpaceMember>().Add(new FullWorthSpaceMember { FullWorthSpaceId = scenario.Space, UserId = scenario.Member, Role = FullWorthSpaceRoles.Member, JoinedAt = now });
            db.Set<FinanceCategory>().Add(new FinanceCategory { Id = scenario.ParentA, FullWorthSpaceId = scenario.Space, Key = "a", Name = "Food", SortOrder = 10 });
            db.Set<FinanceCategory>().Add(new FinanceCategory { Id = scenario.ChildB, FullWorthSpaceId = scenario.Space, Key = "b", Name = "Groceries", ParentId = scenario.ParentA, SortOrder = 20 });
            db.Set<FinanceCategory>().Add(new FinanceCategory { Id = scenario.RootC, FullWorthSpaceId = scenario.Space, Key = "c", Name = "Shopping", SortOrder = 30 });
            await db.SaveChangesAsync();
        });

        return scenario;
    }

    private sealed record CategoryView(Guid Id, string Name, Guid? ParentId, bool IsArchived);

    private sealed class Scenario(
        BackendWebApplicationFactory factory,
        Guid owner,
        Guid member,
        Guid space,
        Guid parentA,
        Guid childB,
        Guid rootC) : IDisposable
    {
        public BackendWebApplicationFactory Factory => factory;
        public Guid Owner => owner;
        public Guid Member => member;
        public Guid Space => space;
        public Guid ParentA => parentA;
        public Guid ChildB => childB;
        public Guid RootC => rootC;

        public HttpRequestMessage Request(HttpMethod method, Guid categoryId, Guid userId, object? body = null) =>
            Request(method, $"/api/categories/{categoryId}?fullWorthSpaceId={space}", userId, body);

        public HttpRequestMessage Request(HttpMethod method, string path, Guid userId, object? body = null)
        {
            var request = new HttpRequestMessage(method, path);
            request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
            request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
            if (body is not null) request.Content = JsonContent.Create(body);
            return request;
        }

        public void Dispose() => factory.Dispose();
    }
}
