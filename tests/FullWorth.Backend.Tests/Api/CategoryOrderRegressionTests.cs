using System.Net;
using System.Net.Http.Json;
using FullWorth.Backend.Modules.Categories;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Api;

public sealed class CategoryOrderRegressionTests
{
    [Fact]
    public async Task BulkOrderUpdatesSiblingOrderInOneRequest()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var owner = Guid.NewGuid();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        await SeedOwner(factory, owner);
        await factory.SeedAsync(async db =>
        {
            db.Categories.AddRange(
                new FinanceCategory { Id = first, FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId, Key = "order.first", Name = "First", SortOrder = 10 },
                new FinanceCategory { Id = second, FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId, Key = "order.second", Name = "Second", SortOrder = 20 });
            await db.SaveChangesAsync();
        });

        using var request = UserRequest(HttpMethod.Put,
            $"/api/category-order?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", owner);
        request.Content = JsonContent.Create(new
        {
            items = new[]
            {
                new { id = first, parentId = (Guid?)null, sortOrder = 20 },
                new { id = second, parentId = (Guid?)null, sortOrder = 10 }
            }
        });
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await factory.SeedAsync(async db =>
        {
            var rows = await db.Categories.AsNoTracking().Where(category => category.Id == first || category.Id == second).ToDictionaryAsync(category => category.Id);
            Assert.Equal(20, rows[first].SortOrder);
            Assert.Equal(10, rows[second].SortOrder);
        });
    }

    [Fact]
    public async Task ParentCycleIsRejectedWithoutPartialOrderChange()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var owner = Guid.NewGuid();
        var parent = Guid.NewGuid();
        var child = Guid.NewGuid();
        await SeedOwner(factory, owner);
        await factory.SeedAsync(async db =>
        {
            db.Categories.AddRange(
                new FinanceCategory { Id = parent, FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId, Key = "cycle.parent", Name = "Parent", SortOrder = 10 },
                new FinanceCategory { Id = child, FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId, Key = "cycle.child", Name = "Child", ParentId = parent, SortOrder = 10 });
            await db.SaveChangesAsync();
        });

        using var request = UserRequest(HttpMethod.Put,
            $"/api/category-order?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", owner);
        request.Content = JsonContent.Create(new
        {
            items = new[]
            {
                new { id = parent, parentId = (Guid?)child, sortOrder = 50 },
                new { id = child, parentId = (Guid?)parent, sortOrder = 60 }
            }
        });
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        await factory.SeedAsync(async db =>
        {
            var parentRow = await db.Categories.AsNoTracking().SingleAsync(category => category.Id == parent);
            var childRow = await db.Categories.AsNoTracking().SingleAsync(category => category.Id == child);
            Assert.Null(parentRow.ParentId);
            Assert.Equal(10, parentRow.SortOrder);
            Assert.Equal(parent, childRow.ParentId);
            Assert.Equal(10, childRow.SortOrder);
        });
    }

    [Fact]
    public async Task CrossSpaceParentIsRejected()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var owner = Guid.NewGuid();
        var local = Guid.NewGuid();
        var foreignSpace = Guid.NewGuid();
        var foreignParent = Guid.NewGuid();
        await SeedOwner(factory, owner);
        await factory.SeedAsync(async db =>
        {
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = foreignSpace, Name = "Other Space" });
            db.Categories.AddRange(
                new FinanceCategory { Id = local, FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId, Key = "local.order", Name = "Local", SortOrder = 10 },
                new FinanceCategory { Id = foreignParent, FullWorthSpaceId = foreignSpace, Key = "foreign.parent", Name = "Foreign", SortOrder = 10 });
            await db.SaveChangesAsync();
        });

        using var request = UserRequest(HttpMethod.Put,
            $"/api/category-order?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", owner);
        request.Content = JsonContent.Create(new
        {
            items = new[] { new { id = local, parentId = (Guid?)foreignParent, sortOrder = 20 } }
        });
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static async Task SeedOwner(BackendWebApplicationFactory factory, Guid owner)
    {
        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = owner,
                EmailNormalized = $"{owner:N}@EXAMPLE.COM",
                DisplayName = "Category order owner",
                IsActive = true
            });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                UserId = owner,
                Role = FullWorthSpaceRoles.Owner
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
